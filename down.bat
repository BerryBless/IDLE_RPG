@echo off
setlocal
pushd "%~dp0"

echo Stopping the IDLE_RPG server stack (mongo-data volume is kept)...
docker compose down

popd
pause
