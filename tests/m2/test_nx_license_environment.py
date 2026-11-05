"""Windows PowerShell 5.1 launcher environment checks; never starts NX."""
from pathlib import Path
import base64
import os
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]
LAUNCHER = ROOT / "tools" / "start-nx-global.ps1"
POWERSHELL = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"


@unittest.skipUnless(os.name == "nt" and POWERSHELL.is_file(), "Windows PowerShell 5.1 is required")
class NxLicenseEnvironmentTests(unittest.TestCase):
    def run_script(self, script):
        encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
        completed = subprocess.run([str(POWERSHELL), "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                                   cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        self.assertEqual(completed.returncode, 0, "PowerShell environment test failed: " + completed.stderr + completed.stdout)

    def test_resolution_precedence(self):
        script = r'''
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path (Get-Location) 'tools\start-nx-global.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw 'Launcher syntax error' }
$fn=$ast.FindAll({param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $x.Name -eq 'Get-NxEnvironmentOverride'},$true)
if ($fn.Count -ne 1) { throw 'Missing environment resolver' }
Invoke-Expression $fn[0].Extent.Text
if ($null -ne (Get-NxEnvironmentOverride -ProcessValue 'explicit' -UserValue 'user' -MachineValue 'machine')) { throw 'Explicit process value must not be replaced' }
if ((Get-NxEnvironmentOverride -ProcessValue '' -UserValue 'user' -MachineValue 'machine') -cne 'user') { throw 'User fallback failed' }
if ((Get-NxEnvironmentOverride -ProcessValue '' -UserValue '' -MachineValue 'machine') -cne 'machine') { throw 'Machine fallback failed' }
if ($null -ne (Get-NxEnvironmentOverride -ProcessValue '' -UserValue '' -MachineValue '')) { throw 'Missing setting should not be invented' }
if ($null -ne (Get-NxEnvironmentOverride -ProcessValue 'japanese' -UserValue 'simpl_chinese' -MachineValue 'english')) { throw 'Explicit language must win' }
if ((Get-NxEnvironmentOverride -ProcessValue '' -UserValue 'simpl_chinese' -MachineValue 'english') -cne 'simpl_chinese') { throw 'User language must win' }
if ((Get-NxEnvironmentOverride -ProcessValue '' -UserValue '' -MachineValue 'english') -cne 'english') { throw 'Machine language fallback failed' }
if ($null -ne (Get-NxEnvironmentOverride -ProcessValue '' -UserValue '' -MachineValue '')) { throw 'Missing language must not be invented' }
'''
        self.run_script(script)

    def test_launcher_child_environment_and_restore_with_fake_process(self):
        # Extract the real launch block and replace only the process creator with
        # a fake; no NX, firewall, user registry or machine environment is changed.
        script = r'''
$ErrorActionPreference='Stop'
$path=Join-Path (Get-Location) 'tools\start-nx-global.ps1'
$text=[IO.File]::ReadAllText($path)
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw 'Launcher syntax error' }
$fn=$ast.FindAll({param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $x.Name -eq 'Get-NxEnvironmentOverride'},$true)
Invoke-Expression $fn[0].Extent.Text
$start=$text.IndexOf('    $childEnvironment = @{')
$end=$text.IndexOf("`n}`ncatch {",$start)
if ($start -lt 0 -or $end -lt 0) { throw 'Cannot isolate launcher block' }
$block=[scriptblock]::Create($text.Substring($start,$end-$start))
function Write-LaunchResult($Ignored) {}
function Start-Process {
    param($FilePath,$WorkingDirectory,$WindowStyle,[switch]$PassThru)
    $script:observedLicense=[Environment]::GetEnvironmentVariable('SPLM_LICENSE_SERVER','Process')
    $script:observedLanguage=[Environment]::GetEnvironmentVariable('UGII_LANG','Process')
    if ([Environment]::GetEnvironmentVariable('NX_SKETCH_MCP_M2_PIPE','Process') -cne 'fake-pipe') { throw 'Bridge environment was not applied' }
    if ($script:failLaunch) { throw 'fake launch failure' }
    $fake=[pscustomobject]@{Id=123;StartTime=[DateTime]::UtcNow}
    $fake | Add-Member -MemberType ScriptMethod -Name Refresh -Value {} -PassThru
}
$pipeName='fake-pipe'; $tokenPath='fake-token-path'; $entryPath='fake-entry-path'; $startupPath='C:\fake\startup'; $nxPath='C:\fake\ugraf.exe'
$names=@('SPLM_LICENSE_SERVER','UGII_LANG','NX_SKETCH_MCP_M2_PIPE','NX_SKETCH_MCP_M2_TOKEN_FILE','NX_SKETCH_MCP_M2_ENTRY_DLL','UGII_USER_DIR')
foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$null,'Process') }
$user=[Environment]::GetEnvironmentVariable('SPLM_LICENSE_SERVER','User')
$machine=[Environment]::GetEnvironmentVariable('SPLM_LICENSE_SERVER','Machine')
$expectedLicense=Get-NxEnvironmentOverride -ProcessValue '' -UserValue $user -MachineValue $machine
$userLang=[Environment]::GetEnvironmentVariable('UGII_LANG','User')
$machineLang=[Environment]::GetEnvironmentVariable('UGII_LANG','Machine')
$expectedLanguage=Get-NxEnvironmentOverride -ProcessValue '' -UserValue $userLang -MachineValue $machineLang
$script:failLaunch=$false; & $block
if ($script:observedLicense -cne $expectedLicense -or $script:observedLanguage -cne $expectedLanguage) { throw 'Missing process values did not reach fake child according to policy' }
foreach ($name in $names) { if ($null -ne [Environment]::GetEnvironmentVariable($name,'Process')) { throw 'Environment not restored' } }
[Environment]::SetEnvironmentVariable('SPLM_LICENSE_SERVER','synthetic-explicit','Process')
$script:failLaunch=$false; & $block
if ($script:observedLicense -cne 'synthetic-explicit' -or $script:observedLanguage -cne $expectedLanguage -or [Environment]::GetEnvironmentVariable('SPLM_LICENSE_SERVER','Process') -cne 'synthetic-explicit' -or $null -ne [Environment]::GetEnvironmentVariable('UGII_LANG','Process')) { throw 'Explicit license and fallback language were not independent' }
[Environment]::SetEnvironmentVariable('SPLM_LICENSE_SERVER',$null,'Process')
[Environment]::SetEnvironmentVariable('UGII_LANG','japanese','Process')
$script:failLaunch=$false; & $block
if ($script:observedLicense -cne $expectedLicense -or $script:observedLanguage -cne 'japanese' -or $null -ne [Environment]::GetEnvironmentVariable('SPLM_LICENSE_SERVER','Process') -or [Environment]::GetEnvironmentVariable('UGII_LANG','Process') -cne 'japanese') { throw 'Explicit language and fallback license were not independent' }
[Environment]::SetEnvironmentVariable('UGII_LANG',$null,'Process')
$script:failLaunch=$true
try { & $block; throw 'Expected fake launch failure' } catch { if ($_.Exception.Message -ne 'fake launch failure') { throw } }
foreach ($name in $names) { if ($null -ne [Environment]::GetEnvironmentVariable($name,'Process')) { throw 'Environment not restored after failure' } }
exit 0
'''
        self.run_script(script)


if __name__ == "__main__":
    unittest.main()
