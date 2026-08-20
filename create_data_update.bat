@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo Create a character data update package.
echo Example version: 2026.08.20.1
echo.
set /p "DATA_VERSION=New data version: "
if not defined DATA_VERSION (
    echo [ERROR] Data version is required.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0create_data_update.ps1" -DataVersion "%DATA_VERSION%"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" pause
exit /b %EXIT_CODE%
