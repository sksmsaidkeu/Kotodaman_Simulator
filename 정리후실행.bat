@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
call clean_run.bat
