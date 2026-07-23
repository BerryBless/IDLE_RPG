@echo off
setlocal
pushd "%~dp0"

rem ============================================================================
rem  loadtest.bat [clients] [duration] [report-interval]
rem    One-click load test: builds Release, starts GameServer in its own
rem    window, then runs LoadTester against it and cleans up afterwards.
rem    Defaults: 10000 clients, 10m duration, 15s report interval.
rem  Examples:
rem    loadtest.bat                       (10k clients, 10 minutes)
rem    loadtest.bat 500 60s 5s            (quick smoke)
rem    loadtest.bat 10000 8h 30s          (overnight rehearsal)
rem    loadtest.bat 10000 72h 60s         (full 72h soak)
rem  Exit code: 0 PASS / 1 FAIL / 2 usage error / 3 aborted (Ctrl+C)
rem  NOTE: ASCII only in this file - see plan/docker_compose_0719.md 7-2.
rem ============================================================================

set "CLIENTS=%~1"
if "%CLIENTS%"=="" set "CLIENTS=10000"
set "DURATION=%~2"
if "%DURATION%"=="" set "DURATION=10m"
set "REPORT=%~3"
if "%REPORT%"=="" set "REPORT=15s"

rem --- Resolve HMAC secret (Release builds fail fast without it). ------------
rem Priority: existing env var > .env file > local-only insecure fallback.
if defined IDLERPG_AUTH_HMAC_SECRET goto :secret_ready
if exist ".env" (
    for /f "usebackq tokens=1,* delims==" %%a in (".env") do (
        if /i "%%a"=="IDLERPG_AUTH_HMAC_SECRET" set "IDLERPG_AUTH_HMAC_SECRET=%%b"
    )
)
if defined IDLERPG_AUTH_HMAC_SECRET goto :secret_ready
echo [WARN] IDLERPG_AUTH_HMAC_SECRET not set and no .env found.
echo        Using an INSECURE local-only load-test secret (fine for localhost testing).
set "IDLERPG_AUTH_HMAC_SECRET=loadtest-local-insecure-secret-do-not-use-in-production"
:secret_ready

echo.
echo === IDLE_RPG load test ===
echo   clients         : %CLIENTS%
echo   duration        : %DURATION%
echo   report interval : %REPORT%
echo   target          : 127.0.0.1:7777 (GameServer Release, auto-started)
echo.

rem --- Build once up front (same reason as run-local.bat: parallel dotnet run
rem     builds race on shared ServerLib obj output and fail with CS2012). ------
echo Building IDLE_RPG.sln (Release)...
dotnet build IDLE_RPG.sln -c Release
if errorlevel 1 (
    echo [ERROR] Build failed. Check the log above.
    pause
    popd
    exit /b 2
)

rem --- Start GameServer in its own window (inherits the secret env var). -----
echo Starting GameServer (Release) in a separate window...
start "IDLE_RPG - GameServer (Release, load test)" cmd /c "dotnet run -c Release --no-build --project GameServer"

rem Give the listener a moment to come up before hammering it.
timeout /t 8 /nobreak >nul

rem --- Run the load test in this window. -------------------------------------
echo Starting LoadTester... (Ctrl+C aborts with a final report)
echo.
dotnet run -c Release --no-build --project tools\LoadTester -- ^
    --mode game --clients %CLIENTS% --duration %DURATION% --report-interval %REPORT% ^
    --ramp-up 500 --server-process GameServer
set "RESULT=%ERRORLEVEL%"

rem --- Cleanup: stop the GameServer we started. -------------------------------
echo.
echo Stopping GameServer...
taskkill /f /im GameServer.exe >nul 2>&1

if "%RESULT%"=="0" (
    echo Result: PASS
) else if "%RESULT%"=="1" (
    echo Result: FAIL - see the report above and logs\loadtest-*.ndjson
) else if "%RESULT%"=="3" (
    echo Result: ABORTED before the configured duration
) else (
    echo Result: ERROR - exit code %RESULT%
)

pause
popd
exit /b %RESULT%
