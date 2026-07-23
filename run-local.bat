@echo off
setlocal
pushd "%~dp0"

echo Starting IDLE_RPG servers locally (no Docker)...
echo Prerequisite: a local MongoDB (mongod) must already be running on port 27017 - AuthServer needs it.
echo   AuthServer    : 127.0.0.1:7778
echo   GameServer    : 127.0.0.1:7777 (telemetry 7779)
echo   MonitorServer : http://127.0.0.1:8080
echo   WebClient     : http://127.0.0.1:8081 (guest play page)
echo.

rem Build once up front (single dotnet build). ServerLib is shared by all three servers;
rem letting each "dotnet run" build it in parallel races on the same obj\Debug\net10.0\ServerLib.dll
rem and fails with CS2012 (file locked by another process).
echo Building IDLE_RPG.sln (Debug)...
dotnet build IDLE_RPG.sln
if errorlevel 1 (
    echo [ERROR] Build failed. Check the log above.
    pause
    popd
    exit /b 1
)

echo.
echo Each server opens in its own window. Close a window (or Ctrl+C inside it) to stop that server.
echo.

start "IDLE_RPG - AuthServer" cmd /k "dotnet run --no-build --project AuthServer"
start "IDLE_RPG - GameServer" cmd /k "dotnet run --no-build --project GameServer"
start "IDLE_RPG - MonitorServer" cmd /k "dotnet run --no-build --project MonitorServer"
start "IDLE_RPG - WebClient" cmd /k "dotnet run --no-build --project WebClient"

popd
