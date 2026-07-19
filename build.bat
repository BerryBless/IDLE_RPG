@echo off
setlocal
pushd "%~dp0"

echo Building IDLE_RPG.sln (Debug)...
dotnet build IDLE_RPG.sln
if errorlevel 1 (
    echo [ERROR] Build failed. Check the log above.
    pause
    popd
    exit /b 1
)

echo.
echo Build succeeded.
popd
pause
