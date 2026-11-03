# Stops Cascade, gives back the Windows taskbar and your old wallpaper, and removes the startup shortcut.
# Pass -RemoveFiles to also delete %LOCALAPPDATA%\Cascade (your added wallpapers and settings).
param([switch]$RemoveFiles)
$dest = Join-Path $env:LOCALAPPDATA 'Cascade'
$exe = "$dest\Cascade.exe"

if (Test-Path $exe) {
  & $exe --quit
  Get-Process Cascade -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
  Get-Process Cascade -ErrorAction SilentlyContinue | Stop-Process -Force
  & $exe --restore --wallpaper
  Start-Sleep 1
}

foreach ($dir in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Programs'))) {
  Remove-Item (Join-Path $dir 'Cascade.lnk') -ErrorAction SilentlyContinue
}

if ($RemoveFiles) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'Cascade removed. The Windows taskbar and your previous wallpaper are back.'
