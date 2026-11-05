# Offline compile only. Does not start NX or run the probe.
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$evidence = Join-Path $projectRoot 'workspace\v2\evidence'
$nxManaged = 'C:\0sf\ug\NXBIN\managed'
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$compilerCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    (Join-Path $framework 'csc.exe')
)
$compiler = $null
foreach ($candidate in $compilerCandidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $compiler = $candidate; break }
}
if (-not $compiler) { throw 'No offline C# compiler found.' }

$probeSource = Join-Path $PSScriptRoot 'f09-pattern-variants.cs'
$wrapperSource = Join-Path $PSScriptRoot 'f09-pattern-variants-journal.cs'
$nxOpen = Join-Path $nxManaged 'NXOpen.dll'
$nxUtilities = Join-Path $nxManaged 'NXOpen.Utilities.dll'
$system = Join-Path $framework 'System.dll'
$systemCore = Join-Path $framework 'System.Core.dll'
$webExtensions = Join-Path $framework 'System.Web.Extensions.dll'
foreach ($required in @($probeSource, $wrapperSource, $nxOpen, $nxUtilities, $system, $systemCore, $webExtensions)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Required source/reference missing: ' + $required) }
}

New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$probeDll = Join-Path $evidence 'F09PatternVariants.dll'
$wrapperDll = Join-Path $evidence 'F09PatternVariantsJournal.dll'

$probeArguments = @('/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+', ('/out:' + $probeDll))
foreach ($reference in @($system, $systemCore, $webExtensions, $nxOpen, $nxUtilities)) {
    $probeArguments += '/reference:' + $reference
}
$probeArguments += $probeSource
& $compiler @probeArguments
if ($LASTEXITCODE -ne 0) { throw ('F09 pattern variants probe compile failed: ' + $LASTEXITCODE) }

$wrapperArguments = @('/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+', ('/out:' + $wrapperDll),
    ('/reference:' + $system), ('/reference:' + $nxOpen), $wrapperSource)
& $compiler @wrapperArguments
if ($LASTEXITCODE -ne 0) { throw ('F09 pattern variants Journal wrapper compile failed: ' + $LASTEXITCODE) }

foreach ($artifact in @($probeDll, $wrapperDll)) {
    $item = Get-Item -LiteralPath $artifact
    [pscustomobject]@{ path = $item.FullName; bytes = $item.Length }
}
