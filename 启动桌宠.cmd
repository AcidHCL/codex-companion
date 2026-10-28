@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0desktop-pet\launch.ps1"
if errorlevel 1 pause