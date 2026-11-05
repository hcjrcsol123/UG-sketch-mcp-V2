param([Parameter(Mandatory=$true)][string]$CandidateBuild)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$candidate=(Resolve-Path -LiteralPath $CandidateBuild).Path
$out=Join-Path $root 'workspace/build/f19-expression-test'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$bridge=Join-Path $candidate 'M2Bridge.dll'
$entry=Join-Path $candidate 'M2Entry.dll'
Copy-Item -LiteralPath $bridge -Destination (Join-Path $out 'M2Bridge.dll') -Force
$exe=Join-Path $out 'F19ExpressionSerializationHarness.exe'
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /langversion:5 /target:exe "/reference:$bridge" "/out:$exe" (Join-Path $PSScriptRoot 'F19ExpressionSerializationHarness.cs')
if($LASTEXITCODE -ne 0){throw 'serialization harness compilation failed'}
& $exe $entry
if($LASTEXITCODE -ne 0){throw 'serialization harness failed'}
