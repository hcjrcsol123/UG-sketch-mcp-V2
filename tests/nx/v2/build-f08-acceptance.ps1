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
$references = @(
    $bridge,
    $adapter,
    (Join-Path $nxManaged 'NXOpen.dll'),
    (Join-Path $nxManaged 'NXOpen.UF.dll'),
    (Join-Path $nxManaged 'NXOpen.Utilities.dll')
)
$source = Join-Path $PSScriptRoot 'F08Acceptance.cs'
$wrapperSource = Join-Path $PSScriptRoot 'RunF08Acceptance.cs'
$output = Join-Path $outputDirectory 'F08Acceptance.dll'
$wrapperOutput = Join-Path $outputDirectory 'RunF08AcceptanceJournal.dll'

foreach ($required in ($references + @($source, $wrapperSource))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required production reference or source is missing: $required" }
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$arguments = @(
    '/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+',
    ('/out:' + $output)
)
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
$arguments += $source
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "F08 acceptance compilation failed (exit $LASTEXITCODE)." }

[string[]]$wrapperArguments = @(
    '/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+',
    ('/out:' + $wrapperOutput), ('/reference:' + (Join-Path $nxManaged 'NXOpen.dll')),
    $wrapperSource
)
& $compiler @wrapperArguments
if ($LASTEXITCODE -ne 0) { throw "RunF08Acceptance journal wrapper compilation failed (exit $LASTEXITCODE)." }

$artifacts = @((Get-Item -LiteralPath $output), (Get-Item -LiteralPath $wrapperOutput))
[pscustomobject]@{
    result = 'compiled_offline'
    artifacts = @($artifacts | ForEach-Object { [pscustomobject]@{ path = $_.FullName; bytes = $_.Length } })
    nx_started = $false
}
