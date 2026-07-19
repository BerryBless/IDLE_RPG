@echo off
setlocal
pushd "%~dp0"

docker compose logs -f

popd
