@echo off
setlocal
pushd "%~dp0"

echo Stopping IDLE_RPG local servers (AuthServer, GameServer, MonitorServer)...
echo.

call :KillOne AuthServer.exe
call :KillOne GameServer.exe
call :KillOne MonitorServer.exe

echo.
echo Done.
popd
pause
exit /b 0

:KillOne
taskkill /F /IM "%~1" >nul 2>&1
if errorlevel 1 (
    echo   %~1: not running
) else (
    echo   %~1: stopped
)
exit /b 0
