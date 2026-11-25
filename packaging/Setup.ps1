# Installs Cascade from a folder of built files (the release zip's "app" folder, or .\bin after a build)
# to %LOCALAPPDATA%\Cascade, starts it at every sign-in and launches it. No admin rights needed.
param([string]$Source = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'app'))
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Cascade'
if (-not (Test-Path "$Source\Cascade.exe")) { throw "Cascade.exe not found in $Source." }

# Cascade shows its UI with Microsoft Edge WebView2, which is built into Windows 11.
$wv = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$hasWebView = $wv | Where-Object { (Get-ItemProperty "HKLM:\$_" -ErrorAction SilentlyContinue).pv -or (Get-ItemProperty "HKCU:\$_" -ErrorAction SilentlyContinue).pv }
if (-not $hasWebView) {
  Write-Warning 'The Microsoft Edge WebView2 Runtime was not found. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and run this again.'
  exit 1
}

$running = Get-Process Cascade -ErrorAction SilentlyContinue
if ($running) {
  if (Test-Path $running[0].Path) { & $running[0].Path --quit }
  $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
  Get-Process Cascade -ErrorAction SilentlyContinue | Stop-Process -Force
}

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$Source\Cascade.exe", "$Source\*.dll" $dest -Force
Remove-Item "$dest\ui" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$Source\ui" "$dest\ui" -Recurse -Force
# Keep an existing config (and data\: settings, added wallpapers, sign-ins); only seed config on first install.
if (-not (Test-Path "$dest\config.json")) { Copy-Item "$Source\config.json" "$dest\config.json" }
# Files from a downloaded zip carry Windows' "from the internet" mark; the person chose to install, so clear it
# on the installed copy rather than have Windows ask again every time Cascade starts.
Get-ChildItem $dest -Recurse -File -Exclude *.json | Where-Object { $_.FullName -notlike "$dest\data\*" } | Unblock-File -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Programs')) {
  $lnk = $shell.CreateShortcut((Join-Path $dir 'Cascade.lnk'))
  $lnk.TargetPath = "$dest\Cascade.exe"
  $lnk.WorkingDirectory = $dest
  $lnk.Description = 'Cascade desktop'
  $lnk.Save()
}

Start-Process "$dest\Cascade.exe"
Write-Host "Cascade installed to $dest and set to start when you sign in."
