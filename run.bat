@echo off
chcp 65001 >nul
cd /d "%~dp0"
dotnet run
if errorlevel 1 pause
