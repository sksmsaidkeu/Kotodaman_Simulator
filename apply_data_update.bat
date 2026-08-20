@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PACKAGE_PATH=%~1"
if not defined PACKAGE_PATH (
    echo Drag a Kotodaman data update ZIP onto this BAT file,
    echo or paste the full ZIP path below.
    echo.
    set /p "PACKAGE_PATH=Data update ZIP: "
)

set "PACKAGE_PATH=%PACKAGE_PATH:"=%"
if not exist "%PACKAGE_PATH%" (
    echo.
    echo [ERROR] The ZIP file was not found.
    pause
    exit /b 1
)

if not exist "%~dp0KotodamanWordFinder.exe" (
    echo.
    echo [ERROR] KotodamanWordFinder.exe was not found in this folder.
    pause
    exit /b 2
)

start /wait "" "%~dp0KotodamanWordFinder.exe" --apply-data-update "%PACKAGE_PATH%"
exit /b %ERRORLEVEL%
