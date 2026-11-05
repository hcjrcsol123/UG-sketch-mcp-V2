# Offline compile only. This script does not start, connect to, or control NX.
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$productionBuild = Join-Path $projectRoot 'workspace\build\current\M2'
$outputDirectory = Join-Path $projectRoot 'workspace\v2\evidence'
$runId = [guid]::NewGuid().ToString('N')
$reportPath = Join-Path $outputDirectory ('f13-acceptance-build-' + $runId + '.json')
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
$acceptanceSource = Join-Path $PSScriptRoot 'F13Acceptance.cs'
$surfaceVolumeSource = Join-Path $PSScriptRoot 'F13SurfaceVolumeDiagnostic.cs'
$journalSource = Join-Path $PSScriptRoot 'RunF13Acceptance.cs'
$pythonJournal = Join-Path $PSScriptRoot 'RunF13Acceptance.py'
$acceptanceOutput = Join-Path $outputDirectory 'F13Acceptance.dll'
$journalOutput = Join-Path $outputDirectory 'RunF13AcceptanceJournal.dll'
$references = @(
    $bridge,
    $adapter,
    (Join-Path $nxManaged 'NXOpen.dll'),
    (Join-Path $nxManaged 'NXOpen.UF.dll'),
    (Join-Path $nxManaged 'NXOpen.Utilities.dll')
)

foreach ($required in ($references + @($acceptanceSource, $surfaceVolumeSource, $journalSource, $pythonJournal))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required production reference or F13 harness source is missing: $required"
    }
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$acceptanceArguments = @(
    '/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+',
    ('/out:' + $acceptanceOutput)
)
foreach ($reference in $references) { $acceptanceArguments += '/reference:' + $reference }
$acceptanceArguments += @($acceptanceSource, $surfaceVolumeSource)
& $compiler @acceptanceArguments
if ($LASTEXITCODE -ne 0) { throw "F13 native acceptance compilation failed (exit $LASTEXITCODE)." }

[string[]]$journalArguments = @(
    '/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+',
    ('/out:' + $journalOutput),
    ('/reference:' + (Join-Path $nxManaged 'NXOpen.dll')),
    $journalSource
)
& $compiler @journalArguments
if ($LASTEXITCODE -ne 0) { throw "F13 journal wrapper compilation failed (exit $LASTEXITCODE)." }

$artifacts = @((Get-Item -LiteralPath $acceptanceOutput), (Get-Item -LiteralPath $journalOutput))
$result = [pscustomobject]@{
    result = 'compiled_offline'
    scope = 'F13 native acceptance harness compile only'
    nx_started = $false
    nx_connected = $false
    production_assembly_directory = $productionBuild
    sources = @($acceptanceSource, $surfaceVolumeSource, $journalSource, $pythonJournal)
    artifacts = @($artifacts | ForEach-Object { [pscustomobject]@{ path = $_.FullName; bytes = $_.Length } })
    report_path = $reportPath
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$result
