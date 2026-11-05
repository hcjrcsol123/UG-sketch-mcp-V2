# Reflectively invokes only the pure F13 JSON round-trip control without opening an NX session,
# starting an NX process, or executing an NX journal. Build F13Acceptance.dll first.
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$acceptanceAssembly = Join-Path $projectRoot 'workspace\v2\evidence\F13Acceptance.dll'
$productionBuild = Join-Path $projectRoot 'workspace\build\current\M2'
$nxManaged = 'C:\0sf\ug\NXBIN\managed'
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
$bindingFlags = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static
$control = $entryType.GetMethod('RunSerializationControl', $bindingFlags)
if ($null -eq $control) { throw 'F13Acceptance.RunSerializationControl was not found.' }
$control.Invoke($null, [object[]]@())
