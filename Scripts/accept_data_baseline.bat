@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo This accepts the current Data folder as the next update baseline.
echo Run this only after the generated data update ZIP has been tested and distributed.
echo.
choice /C YN /N /M "Accept current Data as baseline? [Y/N]: "
if errorlevel 2 exit /b 0

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0accept_data_baseline.ps1"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" pause
exit /b %EXIT_CODE%
