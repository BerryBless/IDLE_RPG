@echo off
setlocal
pushd "%~dp0"

if not exist ".env" (
    echo [ERROR] .env file not found.
    echo   Copy .env.example to .env and set IDLERPG_AUTH_HMAC_SECRET to a 32+ char value.
    echo   ^(copy .env.example .env^)
    pause
    popd
    exit /b 1
)

echo Building and starting the IDLE_RPG server stack...
docker compose up -d --build
if errorlevel 1 (
    echo [ERROR] docker compose up failed. Check the log above.
    pause
    popd
    exit /b 1
)

echo.
docker compose ps
echo.
echo Dashboard: http://localhost:8080
popd
pause
