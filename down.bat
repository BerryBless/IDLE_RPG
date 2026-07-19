@echo off
setlocal
pushd "%~dp0"

echo IDLE_RPG 서버 스택을 정지합니다 (mongo-data 볼륨은 유지됩니다)...
docker compose down

popd
pause
