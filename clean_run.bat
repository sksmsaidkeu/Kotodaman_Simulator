@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [1/3] 이전 빌드 캐시 정리...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
echo [2/3] NuGet 패키지 복원...
dotnet restore
echo [3/3] 실행...
dotnet run
pause
