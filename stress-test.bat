@echo off
setlocal
pushd "%~dp0"

rem ============================================================================
rem  stress-test.bat [scenario] [probe] [stress] [duration]
rem    Stress test: pushes the server past normal limits and checks it survives
rem    and recovers, with a legit "control probe" measured before/during/after.
rem    Builds Release, starts GameServer in lean capacity mode, runs the
rem    scenario, then cleans up. MEASURE-ONLY - server code is not changed.
rem
rem    scenario : burst | churn | malformed | slowloris   (default malformed)
rem    probe    : legit control-probe clients             (default 100)
rem    stress   : in-process adversarial/stalled pool      (default 4000)
rem               (burst/churn use --stress-target for scale instead)
rem    duration : the During (stress) phase length         (default 60s)
rem  Examples:
rem    stress-test.bat malformed 100 4000 30s
rem    stress-test.bat slowloris 100 5000 60s
rem    stress-test.bat burst 200 0 30s        (uses --stress-target 60000)
rem    stress-test.bat churn 200 0 30s
rem  Exit: 0 PASS / 1 FAIL / 2 usage / 3 aborted. ASCII only (repo rule).
rem  cmd pitfall: no unescaped ) inside echo lines within an if(...) block.
rem ============================================================================

set "SCENARIO=%~1"
if "%SCENARIO%"=="" set "SCENARIO=malformed"
set "PROBE=%~2"
if "%PROBE%"=="" set "PROBE=100"
set "STRESS=%~3"
if "%STRESS%"=="" set "STRESS=4000"
set "DURATION=%~4"
if "%DURATION%"=="" set "DURATION=60s"

set "GAMEPORT=20000"
set "TELEPORT=19999"
rem burst/churn need multiple listener ports for source-port reuse; in-process
rem scenarios use a single port. Default to 8 ports (harmless for single-port use).
set "PORTS=8"
set "STRESSTARGET=60000"

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
echo === IDLE_RPG stress test ===
echo   scenario : %SCENARIO%
echo   probe    : %PROBE%
echo   stress   : %STRESS%  (burst/churn use target %STRESSTARGET%)
echo   during   : %DURATION%
echo.

echo Building IDLE_RPG.sln (Release)...
dotnet build IDLE_RPG.sln -c Release
if errorlevel 1 (
    echo [ERROR] Build failed.
    pause
    popd
    exit /b 2
)

echo Starting GameServer (Release, CAPACITY MODE, %PORTS% ports)...
start "IDLE_RPG - GameServer (stress)" cmd /c "set IDLERPG_GAME_CAPACITY_MODE=1&& set IDLERPG_GAME_PORT_COUNT=%PORTS%&& set IDLERPG_GAME_PORT=%GAMEPORT%&& set IDLERPG_GAME_TELEMETRY_PORT=%TELEPORT%&& set IDLERPG_GAME_CONSOLE_INTERVAL_SECONDS=15&& set DOTNET_gcServer=1&& dotnet run -c Release --no-build --project GameServer"
timeout /t 8 /nobreak >nul

echo Starting stress scenario...
echo.
dotnet run -c Release --no-build --project tools\LoadTester -- ^
    --stress %SCENARIO% --probe-clients %PROBE% --stress-clients %STRESS% ^
    --stress-target %STRESSTARGET% --workers 4 --port-count %PORTS% --source-port-base 25000 ^
    --baseline-duration 20s --stress-duration %DURATION% --recovery-duration 40s ^
    --game-port %GAMEPORT% --telemetry-port %TELEPORT% ^
    --server-process GameServer --out logs\stress
set "RESULT=%ERRORLEVEL%"

echo.
echo Stopping GameServer...
taskkill /f /im GameServer.exe >nul 2>&1

if "%RESULT%"=="0" (
    echo Result: PASS - server survived and recovered
) else if "%RESULT%"=="1" (
    echo Result: FAIL - see report above and logs\stress\stress-%SCENARIO%.ndjson
) else if "%RESULT%"=="3" (
    echo Result: ABORTED
) else (
    echo Result: ERROR - exit code %RESULT%
)

pause
popd
exit /b %RESULT%
