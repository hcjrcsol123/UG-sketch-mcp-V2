# Calls only the pure static F13Acceptance.Persist method through reflection.
# It does not open an NX session, start an NX process, execute a journal, or call NX APIs.
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$acceptanceAssembly = Join-Path $projectRoot 'workspace\v2\evidence\F13Acceptance.dll'
$productionBuild = Join-Path $projectRoot 'workspace\build\current\M2'
$nxManaged = 'C:\0sf\ug\NXBIN\managed'
$evidenceDirectory = Join-Path $projectRoot 'workspace\v2\evidence'
if (-not (Test-Path -LiteralPath $acceptanceAssembly)) {
    throw "Compile tests/nx/v2/build-f13-acceptance.ps1 before running this control: $acceptanceAssembly"
}

$resolver = [System.ResolveEventHandler] {
    param($sender, $eventArgs)
    $simpleName = ([System.Reflection.AssemblyName]::new($eventArgs.Name)).Name + '.dll'
    foreach ($root in @($productionBuild, $nxManaged)) {
        $candidate = Join-Path $root $simpleName
        if (Test-Path -LiteralPath $candidate) {
            return [System.Reflection.Assembly]::LoadFrom($candidate)
        }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

$assembly = [System.Reflection.Assembly]::LoadFrom($acceptanceAssembly)
$entryType = $assembly.GetType('F13Acceptance', $true, $false)
$privateStatic = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static
$persist = $entryType.GetMethod('Persist', $privateStatic)
if ($null -eq $persist) { throw 'F13Acceptance.Persist was not found.' }

function Set-PrivateStaticField([System.Type]$Type, [string]$Name, [object]$Value) {
    $field = $Type.GetField($Name, $privateStatic)
    if ($null -eq $field) { throw "Private static field was not found: $Name" }
    $field.SetValue($null, $Value)
}
function Get-PrivateStaticField([System.Type]$Type, [string]$Name) {
    $field = $Type.GetField($Name, $privateStatic)
    if ($null -eq $field) { throw "Private static field was not found: $Name" }
    return $field.GetValue($null)
}

function Invoke-PersistAndGetUnknown([System.Reflection.MethodInfo]$Method) {
    $caught = $null
    try { $Method.Invoke($null, [object[]]@()) | Out-Null }
    catch {
        $caught = $_.Exception
        while ($null -ne $caught.InnerException) { $caught = $caught.InnerException }
    }
    if ($null -eq $caught -or $caught.GetType().Name -ne 'UnknownOutcomeException') {
        throw 'Persist did not throw F13Acceptance.UnknownOutcomeException.'
    }
    if (-not (Get-PrivateStaticField $entryType '_writesHalted') -or
        -not (Get-PrivateStaticField $entryType '_unknownOutcome') -or
        -not (Get-PrivateStaticField $entryType '_suppressFailureSnapshot')) {
        throw 'Persist did not latch all writes, UNKNOWN state, and forensic snapshot suppression before throwing.'
    }
    return $caught
}

function New-ControlReport([string]$RunId, [bool]$InvalidNestedMap) {
    $case = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    $case['id'] = 'F13-N10-save-copy-reopen-roundtrip'
    $case['status'] = 'RUNNING'
    if ($InvalidNestedMap) {
        $invalidMap = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
        $invalidMap.Add('before-unite', 'after-unite')
        $invalidMap.Add('before-sentinel', 'after-sentinel')
        $case['revolve_feature_id_cross_load_mapping'] = $invalidMap
    }
    $cases = [System.Collections.Generic.List[object]]::new()
    $cases.Add($case)
    $report = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    $report['test'] = 'F13 persistence failure safety control'
    $report['run_id'] = $RunId
    $report['current_case'] = 'F13-N10-save-copy-reopen-roundtrip'
    $report['cases'] = $cases
    return $report
}

function Reset-PersistState([string]$ReportPath, [object]$Report) {
    Set-PrivateStaticField $entryType '_report' $Report
    Set-PrivateStaticField $entryType '_reportPath' $ReportPath
    Set-PrivateStaticField $entryType '_reportPersistenceFailed' $false
    Set-PrivateStaticField $entryType '_writesHalted' $false
    Set-PrivateStaticField $entryType '_unknownOutcome' $false
    Set-PrivateStaticField $entryType '_suppressFailureSnapshot' $false
    Set-PrivateStaticField $entryType '_failureSnapshotSuppressionReason' $null
}

$runId = [guid]::NewGuid().ToString('N')
$reportPath = Join-Path $evidenceDirectory ('F13-persist-failure-injection-' + $runId + '.json')
$previousReport = '{"test":"preserve-existing-report","run_id":"' + $runId + '"}'
[System.IO.File]::WriteAllText($reportPath, $previousReport, [System.Text.UTF8Encoding]::new($false))
Reset-PersistState $reportPath (New-ControlReport $runId $true)
$serializationException = Invoke-PersistAndGetUnknown $persist
if ([System.IO.File]::ReadAllText($reportPath) -cne $previousReport) {
    throw 'Persist changed or overwrote the pre-existing report file after JSON serialization failed.'
}
$baseName = [System.IO.Path]::GetFileNameWithoutExtension($reportPath)
$serializationSidecars = @(Get-ChildItem -LiteralPath $evidenceDirectory -Filter ($baseName + '-persistence-failure-*.json') -File)
if ($serializationSidecars.Count -ne 1) { throw "Expected one serialization-failure sidecar; found $($serializationSidecars.Count)." }
$serializationSidecar = Get-Content -LiteralPath $serializationSidecars[0].FullName -Raw | ConvertFrom-Json
if ($serializationSidecar.status -ne 'UNKNOWN' -or $serializationSidecar.failure_phase -ne 'json_serialization' -or
    $serializationSidecar.run_id -ne $runId -or $serializationSidecar.report_path -ne $reportPath -or
    $serializationSidecar.main_report_write_attempted -ne $false -or
    $serializationSidecar.writes_halted -ne $true -or $serializationSidecar.unknown_outcome -ne $true -or
    $serializationSidecar.new_nx_calls_permitted -ne $false -or
    $serializationSidecar.new_model_mutation_permitted -ne $false -or
    $serializationSidecar.new_native_readback_permitted -ne $false -or
    $serializationSidecar.active_read_only_builder_destroy_unwind_may_run -ne $true -or
    $serializationSidecar.forensic_savecopy_permitted -ne $false -or
    $serializationSidecar.release_permitted -ne $false -or
    $serializationSidecar.failure_snapshot_suppressed -ne $true) {
    throw 'Serialization-failure sidecar omitted its required safe UNKNOWN/stop evidence.'
}

$ioRunId = [guid]::NewGuid().ToString('N')
$ioReportPath = Join-Path $evidenceDirectory ('F13-persist-io-failure-injection-' + $ioRunId + '.json')
$ioPreviousReport = '{"test":"preserve-locked-report","run_id":"' + $ioRunId + '"}'
[System.IO.File]::WriteAllText($ioReportPath, $ioPreviousReport, [System.Text.UTF8Encoding]::new($false))
$fileLock = [System.IO.File]::Open($ioReportPath, [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    Reset-PersistState $ioReportPath (New-ControlReport $ioRunId $false)
    $ioException = Invoke-PersistAndGetUnknown $persist
}
finally { $fileLock.Dispose() }
if ([System.IO.File]::ReadAllText($ioReportPath) -cne $ioPreviousReport) {
    throw 'Persist changed the locked pre-existing report during the controlled write failure.'
}
$ioBaseName = [System.IO.Path]::GetFileNameWithoutExtension($ioReportPath)
$ioSidecars = @(Get-ChildItem -LiteralPath $evidenceDirectory -Filter ($ioBaseName + '-persistence-failure-*.json') -File)
if ($ioSidecars.Count -ne 1) { throw "Expected one report-write failure sidecar; found $($ioSidecars.Count)." }
$ioSidecar = Get-Content -LiteralPath $ioSidecars[0].FullName -Raw | ConvertFrom-Json
if ($ioSidecar.status -ne 'UNKNOWN' -or $ioSidecar.failure_phase -ne 'report_write' -or
    $ioSidecar.run_id -ne $ioRunId -or $ioSidecar.report_path -ne $ioReportPath -or
    $ioSidecar.main_report_write_attempted -ne $true -or
    $ioSidecar.main_report_may_be_partial -ne $true -or
    $ioSidecar.new_nx_calls_permitted -ne $false -or
    $ioSidecar.new_model_mutation_permitted -ne $false -or
    $ioSidecar.new_native_readback_permitted -ne $false -or
    $ioSidecar.active_read_only_builder_destroy_unwind_may_run -ne $true -or
    $ioSidecar.forensic_savecopy_permitted -ne $false -or $ioSidecar.release_permitted -ne $false) {
    throw 'Report-write failure sidecar omitted its required UNKNOWN/stop evidence.'
}

$sourcePath = Join-Path $PSScriptRoot 'F13Acceptance.cs'
$sourceText = Get-Content -LiteralPath $sourcePath -Raw
$forensicGuard = '(?s)if\s*\(_writesHalted\s*&&\s*!_unknownOutcome\s*&&\s*!_suppressFailureSnapshot\s*&&\s*_adapter\s*!=\s*null\s*&&\s*_binding\s*!=\s*null\)\s*PreserveKnownFailureSnapshot'
$releaseGuard = 'if\s*\(_adapter\s*!=\s*null\s*&&\s*_binding\s*!=\s*null\s*&&\s*!_writesHalted\)\s*_adapter\.Release\(_binding\)'
$builderDestroyGuard = 'finally\s*\{\s*DestroyReadOnlyBuilder\(builder\);\s*\}'
$destroyFailureUnknown = '(?s)private static void DestroyReadOnlyBuilder\(RevolveBuilder builder\).*?if \(!_reportPersistenceFailed\) throw;.*?throw new UnknownOutcomeException'
if ($sourceText -cnotmatch $forensicGuard -or $sourceText -cnotmatch $releaseGuard -or
    ([regex]::Matches($sourceText, $builderDestroyGuard)).Count -lt 2 -or
    $sourceText -cnotmatch $destroyFailureUnknown) {
    throw 'Static guards no longer block Main forensic SaveCopy/Release or preserve UNKNOWN on the one read-only builder Destroy unwind.'
}

$controlResultPath = Join-Path $evidenceDirectory ('F13-persist-failure-control-' + $runId + '.json')
$result = [ordered]@{
    test = 'F13 Persist serialization and report-write failure safety control'
    result = 'PASSED'
    run_id = $runId
    nx_calls = 0
    serialization_failure_exception_type = $serializationException.GetType().FullName
    report_write_failure_exception_type = $ioException.GetType().FullName
    writes_halted = $true
    unknown_outcome = $true
    failure_snapshot_suppressed = $true
    serialization_case_existing_report_preserved = $true
    locked_write_case_existing_report_preserved = $true
    serialization_sidecar_path = $serializationSidecars[0].FullName
    report_write_sidecar_path = $ioSidecars[0].FullName
    main_finally_savecopy_release_guards = 'STATICALLY_VERIFIED'
    live_builder_destroy_guards = 'STATICALLY_VERIFIED'
}
[System.IO.File]::WriteAllText($controlResultPath,
    ($result | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json -Depth 5
