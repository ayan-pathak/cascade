# Builds Cascade and makes the download for other people: dist\Cascade-<version>.zip, with Install.cmd and
# Uninstall.cmd at the top, the built app in app\ and the install scripts in setup\. Nothing from your own
# Cascade (settings, wallpapers, sign-ins in bin\data) goes in.
#   .\package.ps1 -Version 0.1.0
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $here 'build.ps1')

$name = "Cascade-$Version"
$stage = Join-Path $here "dist\$name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\app", "$stage\setup" | Out-Null

Copy-Item "$here\bin\Cascade.exe", "$here\bin\*.dll" "$stage\app"
Copy-Item "$here\bin\ui" "$stage\app\ui" -Recurse
Copy-Item "$here\config.json" "$stage\app\config.json"
Copy-Item "$here\packaging\Setup.ps1", "$here\uninstall.ps1" "$stage\setup"
Copy-Item "$here\LICENSE" "$stage\LICENSE.txt"
# cmd needs Windows line endings
foreach ($f in 'Install.cmd', 'Uninstall.cmd', 'README.txt') {
  $text = [IO.File]::ReadAllText("$here\packaging\$f") -replace "`r?`n", "`r`n"
  [IO.File]::WriteAllText("$stage\$f", $text, (New-Object Text.UTF8Encoding $false))
}

$zip = Join-Path $here "dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path $stage -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host ("Packaged {0} ({1:N1} MB)  SHA-256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash)
