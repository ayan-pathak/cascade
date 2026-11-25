# Builds Cascade from source and installs it (same install as the release download's Install.cmd).
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$running = Get-Process Cascade -ErrorAction SilentlyContinue
if ($running) {
  if (Test-Path $running[0].Path) { & $running[0].Path --quit }
  $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
  Get-Process Cascade -ErrorAction SilentlyContinue | Stop-Process -Force
}

& (Join-Path $here 'build.ps1')
& (Join-Path $here 'packaging\Setup.ps1') -Source (Join-Path $here 'bin')
