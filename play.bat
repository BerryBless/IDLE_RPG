@echo off
setlocal
pushd "%~dp0"

rem ============================================================================
rem  play.bat
rem    One-click guest play: builds Debug, starts GameServer and WebClient in
rem    their own windows, then opens the browser at the guest play page.
rem    Debug build = both servers auto-agree on the dev fallback HMAC secret,
rem    so no .env / IDLERPG_AUTH_HMAC_SECRET setup is needed for local play.
rem    Close the two server windows (or Ctrl+C inside them) to stop.
rem  NOTE: ASCII only in this file - see plan/docker_compose_0719.md 7-2.
rem ============================================================================

echo.
echo === IDLE_RPG guest play ===
echo   GameServer : 127.0.0.1:7777 (auto-started)
echo   WebClient  : http://127.0.0.1:8081 (guest play page, auto-started)
echo.

rem --- Build once up front (same reason as run-local.bat: parallel dotnet run
rem     builds race on shared ServerLib obj output and fail with CS2012). ------
echo Building GameServer + WebClient (Debug)...
dotnet build WebClient\WebClient.csproj
if errorlevel 1 goto :build_failed
dotnet build GameServer\GameServer.csproj
if errorlevel 1 goto :build_failed

rem --- Start both servers, each in its own window. ----------------------------
echo Starting GameServer in a separate window...
start "IDLE_RPG - GameServer (guest play)" cmd /k "dotnet run --no-build --project GameServer"

echo Starting WebClient in a separate window...
start "IDLE_RPG - WebClient (guest play)" cmd /k "dotnet run --no-build --project WebClient"

rem Give the listeners a moment to come up before opening the browser.
timeout /t 5 /nobreak >nul

echo Opening browser at http://127.0.0.1:8081 ...
start http://127.0.0.1:8081

echo.
echo Done. Enter a nickname in the browser (empty = auto Guest-XXXX) and join.
echo Open more tabs to raid the same boss together.
echo To stop: close the two server windows (or Ctrl+C inside them).
popd
exit /b 0

:build_failed
echo [ERROR] Build failed. Check the log above.
pause
popd
exit /b 1
