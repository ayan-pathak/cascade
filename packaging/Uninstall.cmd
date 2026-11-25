@echo off
rem Removes Cascade and gives back the Windows taskbar and your previous wallpaper.
rem Your Cascade settings and added wallpapers stay in %LOCALAPPDATA%\Cascade unless you choose to remove them.
choice /m "Also delete your Cascade settings and added wallpapers"
if errorlevel 2 (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\uninstall.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\uninstall.ps1" -RemoveFiles
)
echo.
pause
