@echo off
setlocal
pushd "%~dp0"

if not exist ".env" (
    echo [ERROR] .env 파일이 없습니다.
    echo   .env.example을 .env로 복사한 뒤 IDLERPG_AUTH_HMAC_SECRET 값을 32바이트 이상으로 설정하세요.
    echo   ^(copy .env.example .env^)
    pause
    popd
    exit /b 1
)

echo IDLE_RPG 서버 스택을 빌드하고 새로 올립니다...
docker compose up -d --build
if errorlevel 1 (
    echo [ERROR] docker compose up 실패. 위 로그를 확인하세요.
    pause
    popd
    exit /b 1
)

echo.
docker compose ps
echo.
echo 대시보드: http://localhost:8080
popd
pause
