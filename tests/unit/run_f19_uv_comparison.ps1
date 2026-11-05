param([Parameter(Mandatory=$true)][string]$CandidateBuild)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$candidate=(Resolve-Path -LiteralPath $CandidateBuild).Path
$entry=Join-Path $candidate 'M2Entry.dll'
if(-not (Test-Path -LiteralPath $entry)){throw "candidate M2Entry.dll not found: $entry"}
$out=Join-Path $root 'workspace/v2/f19-uv-comparison'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe=Join-Path $out 'F19UvComparisonHarness.exe'
$runId=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')
$log=Join-Path $out ((Split-Path -Leaf $candidate)+'-'+$runId+'-test.log')
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source=Join-Path $PSScriptRoot 'F19UvComparisonHarness.cs'
$nativeEvidence=Join-Path $root 'workspace/v2/evidence/f19-native-bcf49556e1244705929ab097fddbf8d5.json'
if(-not (Test-Path -LiteralPath $nativeEvidence)){throw "native evidence missing: $nativeEvidence"}
$losslessEvidence=Join-Path $out ('f19-native-ieee754-replay-'+$runId+'.json')
$encodePython=@'
import json, struct, sys
class FloatValue:
    def __init__(self, literal):
        self.literal = literal
        self.value = float(literal)
count = 0
def encode(value):
    global count
    if isinstance(value, FloatValue):
        bits = struct.pack(">d", value.value).hex()
        assert struct.pack(">d", float.fromhex(value.value.hex())).hex() == bits
        count += 1
        return {"__f19_ieee754_bits__": bits, "__f19_literal__": value.literal, "__f19_python_float_hex__": value.value.hex()}
    if isinstance(value, list):
        return [encode(item) for item in value]
    if isinstance(value, dict):
        return {key: encode(item) for key, item in value.items()}
    return value
with open(sys.argv[1], encoding="utf-8-sig") as source:
    data = json.load(source, parse_float=FloatValue)
with open(sys.argv[2], "w", encoding="utf-8") as output:
    json.dump(encode(data), output, separators=(",", ":"), ensure_ascii=False)
print("Lossless JSON decimal literals converted to IEEE-754 bits:", count)
'@
@("Candidate: $candidate", "Compiler: $compiler", "Source: $source", "Executable: $exe", "UTC: $([DateTime]::UtcNow.ToString('o'))", 'Compile: /nologo /langversion:5 /target:exe') | Tee-Object -FilePath $log
& python -c $encodePython $nativeEvidence $losslessEvidence 2>&1 | Tee-Object -FilePath $log -Append
if($LASTEXITCODE -ne 0){throw 'lossless native evidence conversion failed'}
& $compiler /nologo /langversion:5 /target:exe '/reference:System.Web.Extensions.dll' "/out:$exe" $source 2>&1 | Tee-Object -FilePath $log -Append
if($LASTEXITCODE -ne 0){throw "UV comparison harness compilation failed; see $log"}
"Native source: $nativeEvidence; Python IEEE-754 reconstruction: $losslessEvidence" | Tee-Object -FilePath $log -Append
"Run: $exe $entry $losslessEvidence" | Tee-Object -FilePath $log -Append
& $exe $entry $losslessEvidence 2>&1 | Tee-Object -FilePath $log -Append
if($LASTEXITCODE -ne 0){throw "UV comparison harness failed; see $log"}
