@echo off
setlocal
pushd "%~dp0"

rem ============================================================================
rem  capacity-test.bat [clients] [workers] [ports] [duration]
rem    Multi-process / multi-port CONNECTION CAPACITY test on a single box.
rem    Builds Release, starts GameServer in lean capacity mode (raid broadcast
rem    OFF, P listener ports), runs the coordinator (K worker processes), then
rem    cleans up. Pure connect+auth+hold - measures max sustained concurrency.
rem
rem    Defaults: 300000 clients, 8 workers, 8 ports, 10m.
rem  Examples:
rem    capacity-test.bat                    (300k / 8 / 8 / 10m)
rem    capacity-test.bat 400 2 2 60s        (smoke)
rem    capacity-test.bat 150000 8 8 10m     (mid ramp)
rem
rem  PREREQUISITE: run capacity-tune.bat (as admin) ONCE first, or the server
rem  listener ports may collide with the ephemeral range (WSAEACCES on bind).
rem  Exit code: 0 PASS / 1 FAIL / 2 usage / 3 aborted. ASCII only (repo rule).
rem ============================================================================

set "CLIENTS=%~1"
if "%CLIENTS%"=="" set "CLIENTS=300000"
set "WORKERS=%~2"
if "%WORKERS%"=="" set "WORKERS=8"
set "PORTS=%~3"
if "%PORTS%"=="" set "PORTS=12"
set "DURATION=%~4"
if "%DURATION%"=="" set "DURATION=10m"

rem Game listener base port and telemetry port, both ABOVE the default ephemeral
rem range (~1024..15000) so listeners never collide with an ephemeral allocation
rem (WSAEACCES on bind). This lets small/mid runs work WITHOUT capacity-tune.bat.
rem For very large runs (clients/ports > ~14000) you still need capacity-tune.bat
rem to expand the ephemeral (source) port pool.
set "GAMEPORT=20000"
set "TELEPORT=19999"

rem --- HMAC secret: env > .env file > local-only fallback (same as loadtest.bat).
if defined IDLERPG_AUTH_HMAC_SECRET goto :secret_ready
if exist ".env" (
    for /f "usebackq tokens=1,* delims==" %%a in (".env") do (
        if /i "%%a"=="IDLERPG_AUTH_HMAC_SECRET" set "IDLERPG_AUTH_HMAC_SECRET=%%b"
    )
)
if defined IDLERPG_AUTH_HMAC_SECRET goto :secret_ready
echo [WARN] IDLERPG_AUTH_HMAC_SECRET not set; using an INSECURE local-only secret.
set "IDLERPG_AUTH_HMAC_SECRET=loadtest-local-insecure-secret-do-not-use-in-production"
:secret_ready

echo.
echo === IDLE_RPG capacity test ===
echo   clients   : %CLIENTS%
echo   workers   : %WORKERS%
echo   ports     : %PORTS%  (game %GAMEPORT%..)
echo   duration  : %DURATION%
echo.

rem --- Source-port band sizing check. The client binds explicit source ports
rem     (SO_REUSEADDR) and reuses each across all %PORTS% destination ports, so
rem     capacity = (source ports) x (ports). Source ports run from base 25000
rem     upward; the clean band 25000..49999 (avoiding the 50000+ reserved range)
rem     holds ~25000 ports. So clients-per-port must stay under ~24000.
rem NOTE: keep parentheses out of echo lines inside this if-block - an unescaped
rem ")" would close the block early and cmd errors with "xxx was unexpected".
set /a CLIENTS_PER_PORT=%CLIENTS% / %PORTS%
if %CLIENTS_PER_PORT% GTR 25000 (
    echo [WARN] clients-per-port %CLIENTS_PER_PORT% ^> 25000 source-port band.
    echo        Source ports would cross the reserved 50000+ range.
    echo        Raise --ports so clients/ports stays under 25000. 300k needs 12+ ports.
)

echo Building IDLE_RPG.sln (Release)...
dotnet build IDLE_RPG.sln -c Release
if errorlevel 1 (
    echo [ERROR] Build failed.
    pause
    popd
    exit /b 2
)

echo Starting GameServer (Release, CAPACITY MODE, %PORTS% ports)...
start "IDLE_RPG - GameServer (capacity)" cmd /c "set IDLERPG_GAME_CAPACITY_MODE=1&& set IDLERPG_GAME_PORT_COUNT=%PORTS%&& set IDLERPG_GAME_PORT=%GAMEPORT%&& set IDLERPG_GAME_TELEMETRY_PORT=%TELEPORT%&& set IDLERPG_GAME_CONSOLE_INTERVAL_SECONDS=15&& set DOTNET_gcServer=1&& dotnet run -c Release --no-build --project GameServer"
timeout /t 8 /nobreak >nul

echo Starting coordinator (%WORKERS% workers)...
echo.
dotnet run -c Release --no-build --project tools\LoadTester -- ^
    --mode game --capacity --clients %CLIENTS% --workers %WORKERS% --port-count %PORTS% ^
    --target-concurrent %CLIENTS% --game-port %GAMEPORT% --telemetry-port %TELEPORT% ^
    --source-port-base 25000 ^
    --duration %DURATION% --ramp-up 5000 --ping-interval 30s --auth-timeout 30s ^
    --report-interval 15s --stall-timeout 300s --accounts %CLIENTS% ^
    --server-process GameServer --server-max-ws-mb 24000 --out logs\capacity
set "RESULT=%ERRORLEVEL%"

echo.
echo Stopping GameServer...
taskkill /f /im GameServer.exe >nul 2>&1

if "%RESULT%"=="0" (
    echo Result: PASS
) else if "%RESULT%"=="1" (
    echo Result: FAIL - see report above and logs\capacity\combined.ndjson
) else if "%RESULT%"=="3" (
    echo Result: ABORTED before the configured duration
) else (
    echo Result: ERROR - exit code %RESULT%
)

pause
popd
exit /b %RESULT%
