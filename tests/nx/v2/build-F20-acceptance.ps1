param([Parameter(Mandatory=$true)][string]$ProductionBuild)
# Offline compile only, into a unique evidence directory. Never starts NX.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = (Resolve-Path -LiteralPath $ProductionBuild).Path
$nx = 'C:\0sf\ug\NXBIN\managed'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$id = [guid]::NewGuid().ToString('N')
$output = Join-Path $root ('workspace\v2\evidence\f20-harness-build-' + $id)
New-Item -ItemType Directory -Path $output | Out-Null
$harness = Join-Path $output 'F20Acceptance.dll'
$wrapper = Join-Path $output 'RunF20AcceptanceJournal.dll'
$harnessSource = Join-Path $output 'F20Acceptance.cs'
$wrapperSource = Join-Path $output 'RunF20Acceptance.cs'
$references = @((Join-Path $build 'M2Bridge.dll'),(Join-Path $build 'M2Entry.dll'),
    (Join-Path $nx 'NXOpen.dll'),(Join-Path $nx 'NXOpen.UF.dll'),(Join-Path $nx 'NXOpen.Utilities.dll'))
foreach ($file in ($references + @((Join-Path $PSScriptRoot 'F20Acceptance.cs'),(Join-Path $PSScriptRoot 'RunF20Acceptance.cs')))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw ('Missing required file: ' + $file) }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'F20Acceptance.cs') -Destination $harnessSource
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RunF20Acceptance.cs') -Destination $wrapperSource
$compileArgs = @('/nologo','/langversion:5','/target:library','/platform:x64','/optimize+',('/out:'+$harness))
foreach ($reference in $references) { $compileArgs += '/reference:'+$reference }
$compileArgs += $harnessSource
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw ('F20 harness compile failed: ' + $LASTEXITCODE) }
& $compiler /nologo /langversion:5 /target:library /platform:x64 /optimize+ ('/out:'+$wrapper) ('/reference:'+(Join-Path $nx 'NXOpen.dll')) ('/reference:'+(Join-Path $nx 'NXOpen.Utilities.dll')) $wrapperSource
if ($LASTEXITCODE -ne 0) { throw ('F20 wrapper compile failed: ' + $LASTEXITCODE) }
$record = [ordered]@{scope='offline compile only; not acceptance'; run_id=$id; production_build=$build;
    harness=$harness; wrapper=$wrapper; nx_started=$false; complete=$true}
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build.json') -Encoding UTF8
$record | ConvertTo-Json -Compress
