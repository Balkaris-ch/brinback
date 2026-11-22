# Writes obj/Interop.UIAutomationClient.dll from the UI Automation type library that ships inside
# Windows (UIAutomationCore.dll). build.cmd compiles with /link against it, which copies the few
# interface definitions the app uses into Brinback.exe itself - so the exe still needs no
# other file next to it.
#
# Why UI Automation at all: the Snap Layouts flyout and Snap Assist are the only way Windows
# forms a real Snap Group (one the taskbar and Alt+Tab show as "Group"), and the COM UI
# Automation API is the only one that can read and press their buttons. The older managed
# System.Windows.Automation sees an empty pane there.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$obj  = Join-Path $root 'obj'
$name = 'Interop.UIAutomationClient.dll'
New-Item -ItemType Directory -Force $obj | Out-Null

Add-Type @'
using System; using System.Reflection; using System.Runtime.InteropServices;
public class WgTypeLib : ITypeLibImporterNotifySink {
  [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
  static extern void LoadTypeLibEx(string file, int kind, [MarshalAs(UnmanagedType.Interface)] out object lib);
  public void ReportEvent(ImporterEventKind kind, int code, string msg) { }
  public Assembly ResolveRef(object lib) { return null; }
  public static void Make(string dll, string fileName) {
    object lib; LoadTypeLibEx(dll, 2 /* REGKIND_NONE */, out lib);
    var asm = new TypeLibConverter().ConvertTypeLibToAssembly(lib, fileName, TypeLibImporterFlags.None,
        new WgTypeLib(), null, null, "Interop.UIAutomationClient", null);
    ((System.Reflection.Emit.AssemblyBuilder)asm).Save(fileName);
  }
}
'@
# AssemblyBuilder.Save writes into the current directory.
Push-Location $obj
[Environment]::CurrentDirectory = $obj
try { [WgTypeLib]::Make((Join-Path $env:WINDIR 'System32\UIAutomationCore.dll'), $name) }
finally { Pop-Location; [Environment]::CurrentDirectory = $root }
Write-Host "Wrote obj/$name"
