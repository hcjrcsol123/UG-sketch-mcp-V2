param()
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$out=Join-Path $root ('workspace\v2\evidence\f20-roundtrip-reader-build-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$source=Join-Path $out 'F20RoundtripReader.cs'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'F20RoundtripReader.cs') -Destination $source
$dll=Join-Path $out 'F20RoundtripReader.dll'
$nx='C:\0sf\ug\NXBIN\managed'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /langversion:5 /target:library /platform:x64 /optimize+ ('/out:'+$dll) ('/reference:'+(Join-Path $nx 'NXOpen.dll')) ('/reference:'+(Join-Path $nx 'NXOpen.UF.dll')) ('/reference:'+(Join-Path $nx 'NXOpen.Utilities.dll')) /reference:System.Web.Extensions.dll $source
if($LASTEXITCODE -ne 0){throw 'roundtrip reader compile failed'}
$record=[ordered]@{scope='offline compile only; no NX execution';dll=$dll;source=$source;nx_started=$false}
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'build.json') -Encoding UTF8
$record | ConvertTo-Json -Compress
