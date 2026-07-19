@echo off
setlocal
pushd "%~dp0"

if not exist ".env" (
    echo [ERROR] .env file not found. Run up.bat first, or copy .env.example to .env.
    pause
    popd
    exit /b 1
)

echo Seeding MongoDB with 3000 deterministic dummy accounts (user0000..user2999, skips if already seeded)...
docker compose run --rm authserver --seed

popd
pause
