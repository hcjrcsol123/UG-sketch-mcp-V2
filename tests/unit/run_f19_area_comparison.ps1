param([Parameter(Mandatory=$true)][string]$CandidateBuild,[switch]$DiagnosticsOnly)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$candidate=(Resolve-Path -LiteralPath $CandidateBuild).Path
$entry=Join-Path $candidate 'M2Entry.dll'
if(-not (Test-Path -LiteralPath $entry)){throw "candidate M2Entry.dll not found: $entry"}
$out=Join-Path $root 'workspace/v2/f19-area-comparison'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe=Join-Path $out 'F19AreaComparisonHarness.exe'
$suffix=if($DiagnosticsOnly){'-diagnostics-test.log'}else{'-test.log'}
$log=Join-Path $out ((Split-Path -Leaf $candidate)+$suffix)
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source=Join-Path $PSScriptRoot 'F19AreaComparisonHarness.cs'
@("Candidate: $candidate", "Compiler: $compiler", "Source: $source", "Executable: $exe", "UTC: $([DateTime]::UtcNow.ToString('o'))", 'Compile: /nologo /langversion:5 /target:exe') | Tee-Object -FilePath $log
& $compiler /nologo /langversion:5 /target:exe '/reference:System.Web.Extensions.dll' "/out:$exe" $source 2>&1 | Tee-Object -FilePath $log -Append
if($LASTEXITCODE -ne 0){throw "area comparison harness compilation failed; see $log"}
if($DiagnosticsOnly) {
    "Run: $exe $entry --diagnostics-only" | Tee-Object -FilePath $log -Append
    & $exe $entry '--diagnostics-only' 2>&1 | Tee-Object -FilePath $log -Append
} else {
    "Run: $exe $entry" | Tee-Object -FilePath $log -Append
    & $exe $entry 2>&1 | Tee-Object -FilePath $log -Append
}
if($LASTEXITCODE -ne 0){throw "area comparison harness failed; see $log"}
