param(
    [string] $Config = '',
    [string] $NxExe = 'C:\0sf\ug\NXBIN\ugraf.exe',
    [string] $StartupDirectory = '',
    [int] $ExistingProcessId = 0,
    [string] $ExistingStartTicks = '',
    [switch] $AsJson
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Write-LaunchResult($Result) {
    if ($AsJson) { [Console]::Out.WriteLine(($Result | ConvertTo-Json -Depth 5 -Compress)) }
    else { Write-Output ([pscustomobject]$Result) }
}

function Test-AbsoluteWindowsPath([string] $PathValue) {
    return $PathValue -match '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+[\\/])'
}

function Get-NxEnvironmentOverride([string] $ProcessValue, [string] $UserValue, [string] $MachineValue) {
    # Preserve explicit per-process settings, including session-specific choices.
    if (-not [string]::IsNullOrWhiteSpace($ProcessValue)) { return $null }
    if (-not [string]::IsNullOrWhiteSpace($UserValue)) { return $UserValue }
    if (-not [string]::IsNullOrWhiteSpace($MachineValue)) { return $MachineValue }
    return $null
}

function Get-RunningNx {
    return @(Get-Process -Name 'ugraf' -ErrorAction SilentlyContinue)
}

$failureCode = 'LAUNCH_FAILED'
$launchMutex = $null
$journalGate = $null
$mutexAcquired = $false
try {
    if ([string]::IsNullOrWhiteSpace($Config)) {
        $nxMcpCodexHome = $env:CODEX_HOME
        if ([string]::IsNullOrWhiteSpace($nxMcpCodexHome)) {
            $nxMcpCodexHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
        }
        $Config = Join-Path (Join-Path $nxMcpCodexHome 'nx-sketch-mcp') 'mcp-config.json'
    }
    $configPath = [IO.Path]::GetFullPath($Config)
    $failureCode = 'LAUNCH_CONFIGURATION_INVALID'
    $configData = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    $pipeName = [string]$configData.pipe_name
    $tokenValue = [string]$configData.token_file
    $helperValue = [string]$configData.helper_path
    if ($pipeName -notmatch '^[A-Za-z0-9_.-]{1,128}$' -or
        -not (Test-AbsoluteWindowsPath $tokenValue) -or -not (Test-AbsoluteWindowsPath $helperValue)) {
        throw 'Bridge configuration requires a valid pipe_name and absolute token_file/helper_path.'
    }
    if ([string]::IsNullOrWhiteSpace($StartupDirectory) -and $null -ne $configData.launch) {
        $StartupDirectory = [string]$configData.launch.startup_directory
        if ($PSBoundParameters.ContainsKey('NxExe') -eq $false) { $NxExe = [string]$configData.launch.nx_executable }
    }
    if (-not (Test-AbsoluteWindowsPath $StartupDirectory) -or
        -not (Test-AbsoluteWindowsPath $NxExe)) {
        throw 'A launch profile requires absolute nx_executable and startup_directory paths.'
    }
    $nxPath = [IO.Path]::GetFullPath($NxExe)
    $startupPath = [IO.Path]::GetFullPath($StartupDirectory).TrimEnd('\', '/')
    $tokenPath = [IO.Path]::GetFullPath($tokenValue)
    $helperPath = [IO.Path]::GetFullPath($helperValue)
    $entryPath = Join-Path (Split-Path -Parent $helperPath) 'M2Entry.dll'
    $bridgePath = Join-Path (Split-Path -Parent $helperPath) 'M2Bridge.dll'
    $startupDll = Join-Path $startupPath 'M2Startup.dll'
    if ((Split-Path -Leaf $nxPath) -ine 'ugraf.exe') { throw 'nx_executable must identify ugraf.exe.' }
    foreach ($required in @($nxPath, $helperPath, $entryPath, $bridgePath, $startupDll)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required installed file is missing: $required" }
    }
    if ((Split-Path -Leaf $startupPath) -ine 'startup') { throw 'startup_directory must identify the NX user directory startup subdirectory.' }
    $startupDlls = @(Get-ChildItem -LiteralPath $startupPath -Filter '*.dll' -File)
    if ($startupDlls.Count -ne 1 -or $startupDlls[0].Name -ine 'M2Startup.dll') {
        throw 'The dedicated startup directory must contain only M2Startup.dll as a startup DLL.'
    }
    $tokenDirectory = Split-Path -Parent $tokenPath
    if (-not (Test-Path -LiteralPath $tokenDirectory -PathType Container)) { throw 'The private session directory is missing.' }

    # Shared by all profiles and direct launcher invocations for this Windows user.
    $failureCode = 'STARTUP_COORDINATION_UNAVAILABLE'
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $launchMutex = [Threading.Mutex]::new($false, ('Global\NxSketchMcpDedicatedLaunch-' + $userSid))
    try { $mutexAcquired = $launchMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $mutexAcquired = $true }
    if (-not $mutexAcquired) {
        Write-LaunchResult @{state='STARTING'; ready=$false; code='STARTUP_IN_PROGRESS';
            message='Another launcher is preparing NX for this Windows user. Retry nx_ensure_ready.'}
        exit 0
    }

    # The same exclusive file gate is held for the entire standalone Journal run.
    # A durable marker survives a crashed supervisor and deliberately blocks startup.
    $coordinationRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex/nx-sketch-mcp/journal-coordination'
    [IO.Directory]::CreateDirectory($coordinationRoot) | Out-Null
    $failureCode = 'JOURNAL_COORDINATION_UNAVAILABLE'
    try {
        $journalGate = [IO.File]::Open((Join-Path $coordinationRoot 'gate.lock'), [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        Write-LaunchResult @{state='NEEDS_MANUAL_ACTION'; ready=$false; code='JOURNAL_COORDINATION_BUSY';
            message='A Journal or launcher holds the shared coordination gate; no NX process was changed.'}
        exit 0
    }
    if (Test-Path -LiteralPath (Join-Path $coordinationRoot 'active.json')) {
        Write-LaunchResult @{state='NEEDS_MANUAL_ACTION'; ready=$false; code='JOURNAL_OUTCOME_UNRESOLVED';
            message='A persistent Journal activity marker exists. Inspect the operation before any manual recovery; it is never cleared automatically.'}
        exit 0
    }

    $running = @(Get-RunningNx)
    $managed = @($running | Where-Object { $_.Id -eq $ExistingProcessId })
    if ($managed.Count -eq 1 -and $ExistingStartTicks -match '^\d+$' -and
        [string]$managed[0].StartTime.ToUniversalTime().Ticks -eq $ExistingStartTicks -and
        [string]$managed[0].Path -ieq $nxPath) {
        Write-LaunchResult @{state='EXISTING_MANAGED'; nx_pid=$managed[0].Id; process_start_ticks=$ExistingStartTicks}
        exit 0
    }
    if ($running.Count -gt 0) {
        Write-LaunchResult @{state='NEEDS_MANUAL_ACTION'; ready=$false; code='EXISTING_NX';
            nx_pids=@($running | ForEach-Object { $_.Id });
            message='An existing NX session is not this managed startup. Load the bridge there manually or close it yourself before retrying. No NX process was changed.'}
        exit 0
    }

    $failureCode = 'INBOUND_ISOLATION_UNAVAILABLE'
    # Read-only check; this call deliberately does not create a per-launch report file.
    $networkChecker = Join-Path $PSScriptRoot 'verify-m2-local-network.ps1'
    & $networkChecker -NxExe $nxPath | Out-Null
    if (@(Get-RunningNx).Count -gt 0) {
        Write-LaunchResult @{state='NEEDS_MANUAL_ACTION'; ready=$false; code='EXISTING_NX';
            message='NX appeared during preparation. Inspect the existing session before retrying; no process was changed.'}
        exit 0
    }
    $failureCode = 'SESSION_PATH_INVALID'
    $installDirectory = Split-Path -Parent $configPath
    $rootPrefix = [IO.Path]::GetFullPath($installDirectory).TrimEnd('\') + '\'
    if (-not $tokenPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'token_file must be below the configuration installation directory.'
    }
    $relativeToken = $tokenPath.Substring($rootPrefix.Length).Replace('/', '\')
    $sessionMatch = [regex]::Match($relativeToken, '^sessions\\(?<session>[0-9a-fA-F]{32})\\private\\bridge\.token$')
    if (-not $sessionMatch.Success) { throw 'token_file must be sessions/<guid>/private/bridge.token below the configuration directory.' }
    foreach ($directory in @($installDirectory, (Join-Path $installDirectory 'sessions'),
        (Join-Path (Join-Path $installDirectory 'sessions') $sessionMatch.Groups['session'].Value), $tokenDirectory)) {
        $item = Get-Item -LiteralPath $directory -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The session path contains a reparse point; refusing token cleanup.'
        }
    }
    if (Test-Path -LiteralPath $tokenPath) {
        $item = Get-Item -LiteralPath $tokenPath -Force -ErrorAction Stop
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The stale token is not a regular file; refusing cleanup.'
        }
        if (($item.Attributes -band ([IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Hidden)) -ne 0) {
            Write-LaunchResult @{state='NEEDS_MANUAL_ACTION'; ready=$false; code='SESSION_TOKEN_PROTECTED';
                message='The stale private token is read-only or hidden. Inspect the session directory before retrying.'}
            exit 0
        }
        $failureCode = 'SESSION_TOKEN_CLEANUP_UNAVAILABLE'
        Remove-Item -LiteralPath $tokenPath -ErrorAction Stop
    }

    $failureCode = 'NX_START_FAILED'
    $childEnvironment = @{
        NX_SKETCH_MCP_M2_PIPE = $pipeName
        NX_SKETCH_MCP_M2_TOKEN_FILE = $tokenPath
        NX_SKETCH_MCP_M2_ENTRY_DLL = $entryPath
        UGII_USER_DIR = (Split-Path -Parent $startupPath)
    }
    # Only these two NX settings receive fallbacks when the host filtered them.
    # Explicit process settings win; never persist user or machine changes.
    foreach ($name in @('SPLM_LICENSE_SERVER', 'UGII_LANG')) {
        $processValue = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ([string]::IsNullOrWhiteSpace($processValue)) {
            $userValue = [Environment]::GetEnvironmentVariable($name, 'User')
            $machineValue = $null
            if ([string]::IsNullOrWhiteSpace($userValue)) {
                $machineValue = [Environment]::GetEnvironmentVariable($name, 'Machine')
            }
            $fallback = Get-NxEnvironmentOverride -ProcessValue $processValue -UserValue $userValue -MachineValue $machineValue
            if (-not [string]::IsNullOrWhiteSpace($fallback)) {
                $childEnvironment[$name] = $fallback
            }
        }
    }
    $previousEnvironment = @{}
    try {
        foreach ($key in $childEnvironment.Keys) {
            $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $childEnvironment[$key], 'Process')
        }
        $nxProcess = Start-Process -FilePath $nxPath -WorkingDirectory ([Environment]::GetFolderPath('MyDocuments')) `
            -WindowStyle Hidden -PassThru -ErrorAction Stop
        $nxProcess.Refresh()
        Write-LaunchResult @{state='STARTED'; nx_pid=$nxProcess.Id;
            process_start_ticks=[string]$nxProcess.StartTime.ToUniversalTime().Ticks;
            message='A dedicated NX GUI was started with the installed startup bridge. No model was opened or taken over.'}
    }
    finally {
        foreach ($key in $previousEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
        }
    }
}
catch {
    $failureState = if ($failureCode -in @('SESSION_TOKEN_CLEANUP_UNAVAILABLE', 'SESSION_PATH_INVALID')) { 'NEEDS_MANUAL_ACTION' } else { 'FAILED' }
    Write-LaunchResult @{state=$failureState; ready=$false; code=$failureCode; message=$_.Exception.Message}
    exit 1
}
finally {
    if ($null -ne $journalGate) { $journalGate.Dispose() }
    if ($null -ne $launchMutex) {
        if ($mutexAcquired) { $launchMutex.ReleaseMutex() }
        $launchMutex.Dispose()
    }
}
