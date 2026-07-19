@echo off
setlocal
pushd "%~dp0"

if not exist ".env" (
    echo [ERROR] .env 파일이 없습니다. up.bat을 먼저 실행하거나 .env.example을 .env로 복사하세요.
    pause
    popd
    exit /b 1
)

echo MongoDB에 결정적 더미 계정 3000개를 시딩합니다 (user0000..user2999, 이미 있으면 건너뜁니다)...
docker compose run --rm authserver --seed

popd
pause
