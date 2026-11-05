$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path ([IO.Path]::GetTempPath()) ('f19-bridge-' + [guid]::NewGuid().ToString('N'))
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Path $out | Out-Null
$bridge = Join-Path $out 'M2Bridge.dll'
$harness = Join-Path $out 'F19BridgeContractHarness.exe'
& $compiler /nologo /langversion:5 /target:library /reference:System.Windows.Forms.dll /reference:System.Runtime.Remoting.dll "/out:$bridge" (Join-Path $root 'src\bridge\M2Bridge.cs') (Join-Path $root 'src\bridge\M2ModelContract.cs') (Join-Path $root 'src\bridge\M3ModelContract.cs')
if ($LASTEXITCODE -ne 0) { throw 'F19 bridge compilation failed' }
& $compiler /nologo /langversion:5 /target:exe "/reference:$bridge" "/out:$harness" (Join-Path $PSScriptRoot 'F19BridgeContractHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'F19 harness compilation failed' }
& $harness
if ($LASTEXITCODE -ne 0) { throw 'F19 harness failed' }
