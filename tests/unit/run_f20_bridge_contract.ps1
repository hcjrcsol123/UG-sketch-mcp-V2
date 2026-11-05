param([Parameter(Mandatory=$true)][string]$CandidateDirectory)
$ErrorActionPreference = 'Stop'
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$bridge = Join-Path $candidate 'M2Bridge.dll'
if (-not (Test-Path -LiteralPath $bridge)) { throw 'Candidate M2Bridge.dll is missing.' }
$out = Join-Path ([IO.Path]::GetTempPath()) ('f20-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
Copy-Item -LiteralPath $bridge -Destination (Join-Path $out 'M2Bridge.dll')
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'F20BridgeContractHarness.cs'
$exe = Join-Path $out 'F20BridgeContractHarness.exe'
& $compiler /nologo /langversion:5 /target:exe "/reference:$out\M2Bridge.dll" "/out:$exe" $source
if ($LASTEXITCODE -ne 0) { throw 'F20 contract harness compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'F20 contract harness failed.' }
