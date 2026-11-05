# Root-only journal dialog helper; requires an already open NX Journal Manager.
param([Parameter(Mandatory=$true)][int]$NxPid,[Parameter(Mandatory=$true)][string]$JournalPath)
$ErrorActionPreference='Stop'
$journal=(Resolve-Path -LiteralPath $JournalPath).Path
$project=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if(-not $journal.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Journal outside project'}
if((Get-Process -Id $NxPid).ProcessName -ne 'ugraf'){throw 'PID is not NX'}
Add-Type -TypeDefinition @'
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public class F11GuiJournal {
 public delegate bool Cb(IntPtr h,IntPtr x);
 [DllImport("user32.dll")]static extern bool EnumWindows(Cb c,IntPtr x);
 [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr h,Cb c,IntPtr x);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,string text);
 [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern IntPtr ClickMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 static string Text(IntPtr h){var b=new StringBuilder(1000);GetWindowText(h,b,1000);return b.ToString();}
 static string Class(IntPtr h){var b=new StringBuilder(200);GetClassName(h,b,200);return b.ToString();}
 public static string Run(uint pid,string path){
  var dialogs=new List<IntPtr>();EnumWindows(delegate(IntPtr h,IntPtr x){uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&IsWindowVisible(h)&&Text(h)=="操作记录管理器")dialogs.Add(h);return true;},IntPtr.Zero);
  if(dialogs.Count!=1)throw new Exception("Expected exactly one visible NX Journal Manager");
  var edits=new List<IntPtr>();var runs=new List<IntPtr>();
  EnumChildWindows(dialogs[0],delegate(IntPtr h,IntPtr x){if(IsWindowVisible(h)){if(Class(h)=="Edit")edits.Add(h);if(Class(h)=="Button"&&Text(h)=="运行")runs.Add(h);}return true;},IntPtr.Zero);
  if(edits.Count!=1||runs.Count!=1)throw new Exception("Journal controls not unique");
  SendMessage(edits[0],0x000C,IntPtr.Zero,path);if(!IsWindowEnabled(runs[0]))throw new Exception("Journal Run is disabled");
  ClickMessage(runs[0],0x00F5,IntPtr.Zero,IntPtr.Zero);return "Journal Run returned; verify the journal output separately";
 }
}
'@
[F11GuiJournal]::Run($NxPid,$journal)
