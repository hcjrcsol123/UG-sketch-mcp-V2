# Offline compile only. Does not start NX or execute the acceptance journal.
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$productionBuild = Join-Path $projectRoot 'workspace\build\current\M2'
$outputDirectory = Join-Path $projectRoot 'workspace\v2\evidence'
$compilerCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
)
$compiler = $null
foreach ($candidate in $compilerCandidates) {
    if (Test-Path -LiteralPath $candidate) { $compiler = $candidate; break }
}
if (-not $compiler) { throw 'No offline C# compiler found (Roslyn or .NET Framework csc).' }

$nxManaged = 'C:\0sf\ug\NXBIN\managed'
$bridge = Join-Path $productionBuild 'M2Bridge.dll'
$adapter = Join-Path $productionBuild 'M2Entry.dll'
$nxOpen = Join-Path $nxManaged 'NXOpen.dll'
$nxUf = Join-Path $nxManaged 'NXOpen.UF.dll'
$nxUtilities = Join-Path $nxManaged 'NXOpen.Utilities.dll'
$source = Join-Path $PSScriptRoot 'F06Acceptance.cs'
$wrapperSource = Join-Path $PSScriptRoot 'RunF06Acceptance.cs'
$output = Join-Path $outputDirectory 'F06Acceptance.dll'
$wrapperOutput = Join-Path $outputDirectory 'RunF06AcceptanceJournal.dll'

foreach ($required in @($bridge, $adapter, $nxOpen, $nxUf, $nxUtilities, $source, $wrapperSource)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required production reference or source is missing: $required" }
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$arguments = @(
    '/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+', '/deterministic',
    ('/out:' + $output),
    ('/reference:' + $bridge), ('/reference:' + $adapter),
    ('/reference:' + $nxOpen), ('/reference:' + $nxUf), ('/reference:' + $nxUtilities),
    $source
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "F06 acceptance compilation failed (exit $LASTEXITCODE)." }

[string[]]$wrapperArguments = @(
    '/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+', '/deterministic',
    ('/out:' + $wrapperOutput), ('/reference:' + $nxOpen), $wrapperSource
)
& $compiler @wrapperArguments
if ($LASTEXITCODE -ne 0) { throw "RunF06Acceptance journal wrapper compilation failed (exit $LASTEXITCODE)." }

$artifacts = @((Get-Item -LiteralPath $output), (Get-Item -LiteralPath $wrapperOutput))
[pscustomobject]@{
    result = 'compiled_offline'
    artifacts = @($artifacts | ForEach-Object { [pscustomobject]@{ path = $_.FullName; bytes = $_.Length } })
    nx_started = $false
}
