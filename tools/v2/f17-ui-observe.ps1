param([Parameter(Mandatory=$true)][int]$NxPid, [switch]$OpenDialog)
$ErrorActionPreference='Stop'
if((Get-Process -Id $NxPid).ProcessName -ne 'ugraf'){throw 'Expected the recorded NX process'}
Add-Type -TypeDefinition @'
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public class F17WindowInfo { public long handle; public string title; public string window_class; }
public static class F17Windows {
 delegate bool Callback(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback c,IntPtr l);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 public static F17WindowInfo[] List(uint pid) {
  var result=new List<F17WindowInfo>();
  EnumWindows(delegate(IntPtr h,IntPtr l){uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&IsWindowVisible(h)) {
   var title=new StringBuilder(512);var cls=new StringBuilder(128);GetWindowText(h,title,512);GetClassName(h,cls,128);
   result.Add(new F17WindowInfo{handle=h.ToInt64(),title=title.ToString(),window_class=cls.ToString()});
  }return true;},IntPtr.Zero);return result.ToArray();
 }
}
'@
$windows=@([F17Windows]::List($NxPid))
if($OpenDialog){
 $main=@($windows | Where-Object {$_.title.StartsWith('Designcenter')})
 $dialogs=@($windows | Where-Object {$_.window_class -eq '#32770'})
 if($main.Count -ne 1 -or $dialogs.Count -gt 0){throw 'Main NX window is not uniquely ready; no keys sent'}
 [void][F17Windows]::SetForegroundWindow([IntPtr]$main[0].handle)
 Start-Sleep -Milliseconds 200
 [uint32]$foregroundOwner=0
 [void][F17Windows]::GetWindowThreadProcessId([F17Windows]::GetForegroundWindow(),[ref]$foregroundOwner)
 if($foregroundOwner -ne $NxPid){throw 'NX did not receive foreground; no keys sent'}
 $keyboard=New-Object -ComObject WScript.Shell
 $keyboard.SendKeys('^o')
 Start-Sleep -Milliseconds 500
 $windows=@([F17Windows]::List($NxPid))
}
$windows | ConvertTo-Json -Depth 3
