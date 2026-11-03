# Compiles Cascade.exe into .\bin and copies the UI, libraries and default config next to it.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $here 'bin'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

$csc = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe", "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $csc) { throw 'Could not find the Roslyn C# compiler (Visual Studio Build Tools).' }

New-Item -ItemType Directory -Force $bin | Out-Null
$refs = @(
  "$fw\WPF\PresentationFramework.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\WindowsBase.dll",
  "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll",
  "$fw\Microsoft.CSharp.dll", "$fw\System.Core.dll", "$fw\System.dll", "$fw\System.Security.dll",
  "$here\lib\Microsoft.Web.WebView2.Core.dll", "$here\lib\Microsoft.Web.WebView2.Wpf.dll"
) | ForEach-Object { "-r:$_" }

& $csc.FullName -nologo -langversion:latest -target:winexe -platform:x64 -optimize+ -nowarn:CS0618 `
  "-out:$bin\Cascade.exe" @refs (Get-ChildItem "$here\src\*.cs" | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Copy-Item "$here\lib\*.dll" $bin -Force
Remove-Item "$bin\ui" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$here\ui" "$bin\ui" -Recurse -Force
if (-not (Test-Path "$bin\config.json")) { Copy-Item "$here\config.json" "$bin\config.json" }
Write-Host "Built $bin\Cascade.exe"
