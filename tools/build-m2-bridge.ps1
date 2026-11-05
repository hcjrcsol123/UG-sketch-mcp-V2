# Offline build for the M2/M3 CLR-only IPC bridge, JSONL helper, and NX entry assembly.
# Output: workspace/build/current/M2/{M2Bridge.dll,M2JsonlHelper.exe,M2Entry.dll,M2Startup.dll}
# This script compiles only. It does not start NX or load an NX image.
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceRoot = Join-Path $projectRoot 'src\bridge'
$outputDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot 'workspace\build\current\M2'
} else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$frameworkDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$compilerCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    (Join-Path $frameworkDir 'csc.exe')
)
$compiler = $null
foreach ($candidate in $compilerCandidates) {
    if (Test-Path -LiteralPath $candidate) { $compiler = $candidate; break }
}
if (-not $compiler) { throw 'No offline C# compiler found (Roslyn or .NET Framework csc).' }

$frameworkRefs = @(
    'System.dll',
    'System.Core.dll',
    'System.Xml.dll',
    'System.Security.dll',
    'System.Runtime.Remoting.dll',
    'System.Windows.Forms.dll',
    'System.Drawing.dll'
)
$refs = @()
foreach ($name in $frameworkRefs) {
    $path = Join-Path $frameworkDir $name
    if (-not (Test-Path -LiteralPath $path)) { throw "required Framework reference missing: $name" }
    $refs += $path
}

$bridgeSource = Join-Path $sourceRoot 'M2Bridge.cs'
$contractSource = Join-Path $sourceRoot 'M2ModelContract.cs'
$m3ContractSource = Join-Path $sourceRoot 'M3ModelContract.cs'
$f20GeometrySource = Join-Path $sourceRoot 'F20SpatialGeometry.cs'
$f20AdapterSource = Join-Path $sourceRoot 'V2SpatialCurveAdapter.cs'
$f21FactorySource = Join-Path $sourceRoot 'V2SweepFactory.cs'
$f21AdapterSource = Join-Path $sourceRoot 'V2SweepAdapter.cs'
$f21GeometrySource = Join-Path $sourceRoot 'V2SweepGeometry.cs'
$f21DefinitionComparisonSource = Join-Path $sourceRoot 'F21SweepDefinitionComparison.cs'
$helperSource = Join-Path $sourceRoot 'M2JsonlHelper.cs'
$entrySource = Join-Path $sourceRoot 'M2Entry.cs'
$adapterSource = Join-Path $sourceRoot 'M2OpenModelAdapter.cs'
$m3FactorySource = Join-Path $sourceRoot 'M3NxFeatureFactory.cs'
$v2SketchFactorySource = Join-Path $sourceRoot 'V2SketchFactory.cs'
$v2SketchCornerFactorySource = Join-Path $sourceRoot 'V2SketchCornerFactory.cs'
$v2SketchCornerAdapterSource = Join-Path $sourceRoot 'V2SketchCornerAdapter.cs'
$v2SketchOffsetFactorySource = Join-Path $sourceRoot 'V2SketchOffsetFactory.cs'
$v2SketchOffsetAdapterSource = Join-Path $sourceRoot 'V2SketchOffsetAdapter.cs'
$v2SketchPatternFactorySource = Join-Path $sourceRoot 'V2SketchPatternFactory.cs'
$v2SketchPatternAdapterSource = Join-Path $sourceRoot 'V2SketchPatternAdapter.cs'
$v2SketchProjectionFactorySource = Join-Path $sourceRoot 'V2SketchProjectionFactory.cs'
$v2SketchProjectionAdapterSource = Join-Path $sourceRoot 'V2SketchProjectionAdapter.cs'
$v2SketchReferenceConversionFactorySource = Join-Path $sourceRoot 'V2SketchReferenceConversionFactory.cs'
$v2SketchReferenceConversionAdapterSource = Join-Path $sourceRoot 'V2SketchReferenceConversionAdapter.cs'
$v2ProfileAnalyzerSource = Join-Path $sourceRoot 'V2SketchProfileAnalyzer.cs'
$f12BodyGeometrySnapshotSource = Join-Path $sourceRoot 'F12BodyGeometrySnapshot.cs'
$f12ExtrudePreservationSource = Join-Path $sourceRoot 'F12ExtrudePreservation.cs'
$v2SketchRevolveFactorySource = Join-Path $sourceRoot 'V2SketchRevolveFactory.cs'
$v2SketchRevolveAdapterSource = Join-Path $sourceRoot 'V2SketchRevolveAdapter.cs'
$v2BooleanFactorySource = Join-Path $sourceRoot 'V2BooleanFactory.cs'
$v2BooleanAdapterSource = Join-Path $sourceRoot 'V2BooleanAdapter.cs'
$v2EdgeBlendFactorySource = Join-Path $sourceRoot 'V2EdgeBlendFactory.cs'
$v2EdgeBlendAdapterSource = Join-Path $sourceRoot 'V2EdgeBlendAdapter.cs'
$v2EdgeBlendCoverageSource = Join-Path $sourceRoot 'V2EdgeBlendCoverage.cs'
$v2ChamferFactorySource = Join-Path $sourceRoot 'V2ChamferFactory.cs'
$v2ChamferAdapterSource = Join-Path $sourceRoot 'V2ChamferAdapter.cs'
$v2ChamferCoverageSource = Join-Path $sourceRoot 'V2ChamferCoverage.cs'
$v2FeaturePatternFactorySource = Join-Path $sourceRoot 'V2FeaturePatternFactory.cs'
$v2FeaturePatternAdapterSource = Join-Path $sourceRoot 'V2FeaturePatternAdapter.cs'
$v2FeaturePatternCoverageSource = Join-Path $sourceRoot 'V2FeaturePatternCoverage.cs'
$v2FeatureMirrorFactorySource = Join-Path $sourceRoot 'V2FeatureMirrorFactory.cs'
$v2FeatureMirrorAdapterSource = Join-Path $sourceRoot 'V2FeatureMirrorAdapter.cs'
$v2FeatureMirrorCoverageSource = Join-Path $sourceRoot 'V2FeatureMirrorCoverage.cs'
$v2FeatureMirrorComparisonSource = Join-Path $sourceRoot 'V2FeatureMirrorComparison.cs'
$v2HoleFactorySource = Join-Path $sourceRoot 'V2HoleFactory.cs'
$v2HoleAdapterSource = Join-Path $sourceRoot 'V2HoleAdapter.cs'
$v2HoleCoverageSource = Join-Path $sourceRoot 'V2HoleCoverage.cs'
$f14ThreadStandardSource = Join-Path $sourceRoot 'F14ThreadStandard.cs'
$m7FactorySource = Join-Path $sourceRoot 'M7NxHelicalFactory.cs'
$targetSource = Join-Path $sourceRoot 'M2FrameworkTarget.cs'
$startupSource = Join-Path $sourceRoot 'M2Startup.cs'
foreach ($source in @($bridgeSource, $contractSource, $m3ContractSource, $f20GeometrySource, $f20AdapterSource, $f21FactorySource, $f21AdapterSource, $f21GeometrySource, $f21DefinitionComparisonSource, $helperSource, $entrySource, $adapterSource, $m3FactorySource, $v2SketchFactorySource, $v2SketchCornerFactorySource, $v2SketchCornerAdapterSource, $v2SketchOffsetFactorySource, $v2SketchOffsetAdapterSource, $v2SketchPatternFactorySource, $v2SketchPatternAdapterSource, $v2SketchProjectionFactorySource, $v2SketchProjectionAdapterSource, $v2SketchReferenceConversionFactorySource, $v2SketchReferenceConversionAdapterSource, $v2SketchRevolveFactorySource, $v2SketchRevolveAdapterSource, $v2BooleanFactorySource, $v2BooleanAdapterSource, $v2EdgeBlendFactorySource, $v2EdgeBlendAdapterSource, $v2EdgeBlendCoverageSource, $v2ChamferFactorySource, $v2ChamferAdapterSource, $v2ChamferCoverageSource, $v2FeaturePatternFactorySource, $v2FeaturePatternAdapterSource, $v2FeaturePatternCoverageSource, $v2FeatureMirrorFactorySource, $v2FeatureMirrorAdapterSource, $v2FeatureMirrorCoverageSource, $v2FeatureMirrorComparisonSource, $v2HoleFactorySource, $v2HoleAdapterSource, $v2HoleCoverageSource, $f14ThreadStandardSource, $v2ProfileAnalyzerSource, $f12BodyGeometrySnapshotSource, $f12ExtrudePreservationSource, $m7FactorySource, $targetSource, $startupSource)) {
    if (-not (Test-Path -LiteralPath $source)) { throw "source file missing: $source" }
}

$nxBase = $env:UGII_BASE_DIR
if ([string]::IsNullOrWhiteSpace($nxBase)) { throw 'UGII_BASE_DIR is required to build M2Entry.dll.' }
$nxOpen = Join-Path $nxBase 'NXBIN\managed\NXOpen.dll'
$nxUf = Join-Path $nxBase 'NXBIN\managed\NXOpen.UF.dll'
$nxUtilities = Join-Path $nxBase 'NXBIN\managed\NXOpen.Utilities.dll'
$nxUi = Join-Path $nxBase 'NXBIN\managed\NXOpenUI.dll'
foreach ($reference in @($nxOpen, $nxUf, $nxUtilities, $nxUi)) {
    if (-not (Test-Path -LiteralPath $reference)) { throw "NXOpen reference missing under UGII_BASE_DIR: $reference" }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$bridgeDll = Join-Path $outputDir 'M2Bridge.dll'
$helperExe = Join-Path $outputDir 'M2JsonlHelper.exe'
$entryDll = Join-Path $outputDir 'M2Entry.dll'
$startupDll = Join-Path $outputDir 'M2Startup.dll'

function Invoke-M2Csc([string]$label, [string]$target, [string]$output, [string[]]$references, [string[]]$sources) {
    $arguments = @('/nologo', '/langversion:5', "/target:$target", '/platform:x64', '/optimize+', '/deterministic', "/out:$output")
    foreach ($reference in $references) { $arguments += "/reference:$reference" }
    $arguments += $sources
    Write-Output ("compile " + $label)
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $label (exit $LASTEXITCODE)" }
}

$bridgeRefs = @($refs)
Invoke-M2Csc 'M2Bridge.dll' 'library' $bridgeDll $bridgeRefs @($bridgeSource, $contractSource, $m3ContractSource, $f20GeometrySource, $targetSource)
Invoke-M2Csc 'M2JsonlHelper.exe' 'exe' $helperExe (@($refs) + @($bridgeDll)) @($helperSource, $targetSource)
Invoke-M2Csc 'M2Entry.dll' 'library' $entryDll (@($refs) + @($bridgeDll, $nxOpen, $nxUf, $nxUtilities, $nxUi)) @($entrySource, $adapterSource, $f20AdapterSource, $f21FactorySource, $f21AdapterSource, $f21GeometrySource, $f21DefinitionComparisonSource, $m3FactorySource, $v2SketchFactorySource, $v2SketchCornerFactorySource, $v2SketchCornerAdapterSource, $v2SketchOffsetFactorySource, $v2SketchOffsetAdapterSource, $v2SketchPatternFactorySource, $v2SketchPatternAdapterSource, $v2SketchProjectionFactorySource, $v2SketchProjectionAdapterSource, $v2SketchReferenceConversionFactorySource, $v2SketchReferenceConversionAdapterSource, $v2SketchRevolveFactorySource, $v2SketchRevolveAdapterSource, $v2BooleanFactorySource, $v2BooleanAdapterSource, $v2EdgeBlendFactorySource, $v2EdgeBlendAdapterSource, $v2EdgeBlendCoverageSource, $v2ChamferFactorySource, $v2ChamferAdapterSource, $v2ChamferCoverageSource, $v2FeaturePatternFactorySource, $v2FeaturePatternAdapterSource, $v2FeaturePatternCoverageSource, $v2FeatureMirrorFactorySource, $v2FeatureMirrorAdapterSource, $v2FeatureMirrorCoverageSource, $v2FeatureMirrorComparisonSource, $v2HoleFactorySource, $v2HoleAdapterSource, $v2HoleCoverageSource, $f14ThreadStandardSource, $v2ProfileAnalyzerSource, $f12BodyGeometrySnapshotSource, $f12ExtrudePreservationSource, $m7FactorySource, $targetSource)
Invoke-M2Csc 'M2Startup.dll' 'library' $startupDll (@($refs) + @($nxOpen)) @($startupSource, $targetSource)

Write-Output ''
Write-Output 'build complete'
foreach ($artifact in @($bridgeDll, $helperExe, $entryDll, $startupDll)) {
    $item = Get-Item -LiteralPath $artifact
    [pscustomobject]@{ path = $item.FullName; bytes = $item.Length }
}
