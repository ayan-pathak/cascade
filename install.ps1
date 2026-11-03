# Builds Cascade, installs it to %LOCALAPPDATA%\Cascade and starts it at every sign-in.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'Cascade'

$running = Get-Process Cascade -ErrorAction SilentlyContinue
if ($running) {
  if (Test-Path $running[0].Path) { & $running[0].Path --quit }
  $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
  Get-Process Cascade -ErrorAction SilentlyContinue | Stop-Process -Force
}

& (Join-Path $here 'build.ps1')

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$here\bin\Cascade.exe", "$here\bin\*.dll" $dest -Force
Remove-Item "$dest\ui" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$here\bin\ui" "$dest\ui" -Recurse -Force
# Keep an existing config and your wallpapers/settings; only seed them on first install.
if (-not (Test-Path "$dest\config.json")) { Copy-Item "$here\config.json" "$dest\config.json" }
if ((Test-Path "$here\bin\data") -and -not (Test-Path "$dest\data")) {
  New-Item -ItemType Directory -Force "$dest\data" | Out-Null
  Copy-Item "$here\bin\data\settings.json", "$here\bin\data\wallpapers" "$dest\data" -Recurse -ErrorAction SilentlyContinue
}

$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Startup'), (Join-Path ([Environment]::GetFolderPath('Programs')) ''))) {
  $lnk = $shell.CreateShortcut((Join-Path $dir 'Cascade.lnk'))
  $lnk.TargetPath = "$dest\Cascade.exe"
  $lnk.WorkingDirectory = $dest
  $lnk.Description = 'Cascade desktop'
  $lnk.Save()
}

Start-Process "$dest\Cascade.exe"
Write-Host "Cascade installed to $dest and set to start when you sign in."
