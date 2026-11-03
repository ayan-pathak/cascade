# Runs Cascade with an extra stylesheet (and optional script) injected, measures it, then puts everything back.
#   .\tools\perf-experiment.ps1 -Css "*{animation:none!important}" -Js "..." -Scenario apps
param([string]$Css = "", [string]$Js = "", [string]$Scenario = "apps", [int]$Seconds = 12)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ExpKeys { [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  public static void Reload() { foreach (byte k in new byte[] { 0x11, 0x12, 0x52 }) keybd_event(k,0,0,UIntPtr.Zero); foreach (byte k in new byte[] { 0x52, 0x12, 0x11 }) keybd_event(k,0,2,UIntPtr.Zero); } }
'@
$ui = "$env:LOCALAPPDATA\Cascade\ui"; $idx = "$ui\index.html"; $bak = "$env:TEMP\cascade-index.bak"
Copy-Item $idx $bak -Force
try {
  $html = [IO.File]::ReadAllText($idx)
  $html = $html.Replace('<link rel="stylesheet" href="app.css">', '<link rel="stylesheet" href="app.css"><link rel="stylesheet" href="perftest.css">')
  $html = $html.Replace('<script src="app.js"></script>', '<script src="app.js"></script><script src="perftest.js"></script>')
  [IO.File]::WriteAllText($idx, $html, (New-Object Text.UTF8Encoding $false))
  [IO.File]::WriteAllText("$ui\perftest.css", $Css, (New-Object Text.UTF8Encoding $false))
  [IO.File]::WriteAllText("$ui\perftest.js", $Js, (New-Object Text.UTF8Encoding $false))
  [ExpKeys]::Reload(); Start-Sleep -Seconds 22
  & "$PSScriptRoot\perf.ps1" -Scenario $Scenario -Seconds $Seconds
} finally {
  Copy-Item $bak $idx -Force; Remove-Item "$ui\perftest.css", "$ui\perftest.js" -ErrorAction SilentlyContinue
}
