@echo off
rem Double-click to install Cascade for this Windows account (no admin rights needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\Setup.ps1" -Source "%~dp0app"
echo.
pause
