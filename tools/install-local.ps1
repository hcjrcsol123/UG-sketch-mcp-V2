# Update the already configured local installation. No NX/process termination or config/token changes.
param([string]$InstallRoot = '', [string]$Version = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $codexHome = $env:CODEX_HOME
    if ([string]::IsNullOrWhiteSpace($codexHome)) {
        $codexHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
    }
    $InstallRoot = Join-Path $codexHome 'nx-sketch-mcp'
}
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\','/')
$metadataPath = Join-Path $install 'installation.json'
if (-not (Test-Path -LiteralPath $metadataPath)) { throw 'Existing installation.json is required; this tool does not register a new MCP installation.' }
$metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([IO.Path]::GetFullPath([string]$metadata.source_root).TrimEnd('\','/') -ne $projectRoot) { throw 'Installation source_root differs from this source tree.' }
if (Get-Process -Name ugraf,run_journal,M2JsonlHelper -ErrorAction SilentlyContinue) {
    throw 'NX, an NX journal, or a bridge helper is running. Save and close the appropriate session before replacing loaded assemblies; no process was changed.'
}
$app = Join-Path $install 'app'
$files = [Collections.Generic.List[object]]::new()
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/nx_sketch_mcp') -Filter '*.py' -File) {
    $files.Add(@{ source=$source.FullName; target=(Join-Path $app ('src/nx_sketch_mcp/' + $source.Name)) })
}
foreach ($name in @('M2Bridge.dll','M2Entry.dll','M2JsonlHelper.exe','M2Startup.dll')) {
    $files.Add(@{ source=(Join-Path $projectRoot ('workspace/build/current/M2/' + $name)); target=(Join-Path $app ('workspace/build/current/M2/' + $name)) })
}
$files.Add(@{ source=(Join-Path $projectRoot 'workspace/build/current/M2/M2Startup.dll'); target=(Join-Path $app 'nx-profile/startup/M2Startup.dll') })
$files.Add(@{ source=(Join-Path $projectRoot 'README.md'); target=(Join-Path $app 'README.md') })
$files.Add(@{ source=(Join-Path $projectRoot 'docs/M7-native-helical.md'); target=(Join-Path $app 'docs/M7-native-helical.md') })
# Ship the current feature manuals and API notes, not development review artifacts.
$featureDocs = @('F01-F02.md')
$featureDocs += @(3..22 | ForEach-Object { 'F{0:D2}.md' -f $_ })
$featureDocs += @(8..22 | ForEach-Object { 'F{0:D2}-api-notes.md' -f $_ })
$featureDocs += @('V2.md', 'testing.md')
foreach ($name in $featureDocs) {
    $files.Add(@{ source=(Join-Path $projectRoot ('docs/' + $name)); target=(Join-Path $app ('docs/' + $name)) })
}
$files.Add(@{ source=(Join-Path $projectRoot 'tools/start-nx-global.ps1'); target=(Join-Path $app 'tools/start-nx-global.ps1') })
$files.Add(@{ source=(Join-Path $projectRoot 'docs/installed-README.md'); target=(Join-Path $install 'README.md') })
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.source -PathType Leaf)) { throw ('Missing source: ' + $file.source) }
    $target = [IO.Path]::GetFullPath($file.target)
    if (-not $target.StartsWith($app + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($target, (Join-Path $install 'README.md'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation target escaped allowed app/README paths.'
    }
}
foreach ($file in $files) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file.target) | Out-Null
    Copy-Item -LiteralPath $file.source -Destination $file.target -Force
}

$discoveryRoot = Join-Path ([IO.Path]::GetTempPath()) ('nx-sketch-mcp-discovery-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $discoveryRoot | Out-Null
$discoveryConfigPath = Join-Path $discoveryRoot 'config.json'
$discoveryConfig = @{
    pipe_name = 'nx-install-discovery'
    token_file = Join-Path $discoveryRoot 'unused.token'
    helper_path = Join-Path $app 'workspace/build/current/M2/M2JsonlHelper.exe'
    timeout_seconds = 1
}
[IO.File]::WriteAllText(
    $discoveryConfigPath,
    ($discoveryConfig | ConvertTo-Json -Depth 4 -Compress),
    [Text.UTF8Encoding]::new($false)
)
$discoveryCode = @'
import json
import os
import subprocess
import sys

config_path = sys.argv[1]
requests = (
    {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2025-11-25", "capabilities": {},
        "clientInfo": {"name": "nx-sketch-mcp-installer", "version": "1"}}},
    {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
)
child = subprocess.Popen(
    [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", config_path],
    cwd=os.getcwd(), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding="utf-8",
)
try:
    payload = "\n".join(json.dumps(request) for request in requests) + "\n"
    stdout, _ = child.communicate(payload, timeout=10)
except subprocess.TimeoutExpired:
    child.kill()
    child.communicate()
    raise SystemExit("MCP discovery timed out")
if child.returncode != 0:
    raise SystemExit("MCP discovery process failed")
responses = [json.loads(line) for line in stdout.splitlines() if line.strip()]
initialized = next((item for item in responses if item.get("id") == 1), None)
listing = next((item for item in responses if item.get("id") == 2), None)
server_info = initialized.get("result", {}).get("serverInfo") if isinstance(initialized, dict) else None
if not isinstance(server_info, dict) or not isinstance(server_info.get("version"), str):
    raise SystemExit("MCP initialize omitted server version")
tools = listing.get("result", {}).get("tools") if isinstance(listing, dict) else None
if not isinstance(tools, list):
    raise SystemExit("MCP tools/list did not return a tool list")
names = sorted(item.get("name") for item in tools if isinstance(item, dict))
if len(names) != len(tools) or not names or any(not isinstance(name, str) or not name for name in names):
    raise SystemExit("MCP tools/list returned an invalid tool list")
print(json.dumps({"version": server_info["version"], "tool_names": names}))
'@
$previousPythonPath = $env:PYTHONPATH
$previousPythonUtf8 = $env:PYTHONUTF8
$previousNoBytecode = $env:PYTHONDONTWRITEBYTECODE
try {
    $env:PYTHONPATH = Join-Path $app 'src'
    $env:PYTHONUTF8 = '1'
    $env:PYTHONDONTWRITEBYTECODE = '1'
    $pythonExe = (Get-Command python.exe -ErrorAction Stop | Select-Object -First 1).Path
    $discoveryOutput = & $pythonExe -B -c $discoveryCode $discoveryConfigPath
    if ($LASTEXITCODE -ne 0) { throw 'Installed MCP tool discovery failed.' }
    $discovery = ($discoveryOutput | Out-String) | ConvertFrom-Json
    $discoveredVersion = [string]$discovery.version
    if ([string]::IsNullOrWhiteSpace($discoveredVersion)) { throw 'Installed MCP discovery returned no version.' }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = $discoveredVersion
    } elseif ($Version -ne $discoveredVersion) {
        throw 'Requested version differs from the installed MCP server version.'
    }
    $toolNames = @($discovery.tool_names)
} finally {
    if ($null -eq $previousPythonPath) { Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue } else { $env:PYTHONPATH = $previousPythonPath }
    if ($null -eq $previousPythonUtf8) { Remove-Item Env:PYTHONUTF8 -ErrorAction SilentlyContinue } else { $env:PYTHONUTF8 = $previousPythonUtf8 }
    if ($null -eq $previousNoBytecode) { Remove-Item Env:PYTHONDONTWRITEBYTECODE -ErrorAction SilentlyContinue } else { $env:PYTHONDONTWRITEBYTECODE = $previousNoBytecode }
    Remove-Item -LiteralPath $discoveryConfigPath -Force -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem -LiteralPath $discoveryRoot -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $discoveryRoot -Force -ErrorAction SilentlyContinue
    }
}

$evidence = @($files | ForEach-Object { @{ file=$_.target } })
$metadata | Add-Member -NotePropertyName version -NotePropertyValue $Version -Force
$metadata | Add-Member -NotePropertyName updated_utc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath -Encoding utf8
@{ version=$Version; files=$evidence; tool_names=$toolNames; tool_count=$toolNames.Count; reconnect_mcp_required=$true; existing_nx_hot_reloaded=$false } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $projectRoot 'workspace/build/current/M2/installed-m7.json') -Encoding utf8
Write-Output ('Installed version ' + $Version + '; copied ' + $files.Count + ' files and discovered ' + $toolNames.Count + ' MCP tools. Reconnect MCP to use them.')
