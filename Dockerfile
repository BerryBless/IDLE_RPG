# syntax=docker/dockerfile:1
#
# 단일 파라미터화 멀티스테이지 Dockerfile — GameServer/AuthServer/MonitorServer 3개 서비스가
# 모두 이 하나의 Dockerfile을 ARG PROJECT/RUNTIME_IMAGE로 재사용한다(docker-compose.yml 참고).
# 설계: plan/docker_compose_0719.md. 서버를 새로 추가할 때는 이 파일에 COPY 한 줄만 추가하면
# 되고, 새 Dockerfile은 필요 없다(§6 "서버 추가" 패턴).

# RUNTIME_IMAGE는 첫 FROM보다 먼저(글로벌 스코프) 선언해야 뒤쪽 FROM ${RUNTIME_IMAGE}에서 쓸 수
# 있다 — FROM 이후에 선언한 ARG는 그 스테이지 안에서만 유효해 다음 FROM 줄에서는 보이지 않는다
# (Docker ARG 스코프 규칙). 기본값은 ASP.NET Core가 필요 없는 콘솔 서버(GameServer/AuthServer)용
# 순수 runtime 이미지. MonitorServer(Microsoft.NET.Sdk.Web)는 docker-compose.yml에서
# mcr.microsoft.com/dotnet/aspnet:10.0으로 override한다.
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0

# ---- build stage: SDK 이미지로 퍼블리시까지 수행, 최종 이미지에는 SDK를 포함시키지 않는다 ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src

# .sln + 각 프로젝트의 .csproj만 먼저 복사해 dotnet restore 레이어를 캐시한다 — 소스 코드만
# 바뀌고 의존성이 그대로면 restore를 다시 돌리지 않아 빌드가 빨라진다(NuGet 캐시 레이어 재사용).
COPY IDLE_RPG.sln ./
COPY ServerLib/ServerLib.csproj ServerLib/
COPY GameServer/GameServer.csproj GameServer/
COPY AuthServer/AuthServer.csproj AuthServer/
COPY MonitorServer/MonitorServer.csproj MonitorServer/
# 새 서버를 추가하면 여기에 COPY <NewServer>/<NewServer>.csproj <NewServer>/ 한 줄을 더한다.
RUN dotnet restore "${PROJECT}/${PROJECT}.csproj"

# 나머지 소스 전체 복사 후 퍼블리시. Release 빌드이므로 GameServer/AuthServer는
# IDLERPG_AUTH_HMAC_SECRET 미설정 시 기동 시점에 fail-fast한다(코드 자체는 컴파일에 영향 없음).
COPY . .
RUN dotnet publish "${PROJECT}/${PROJECT}.csproj" -c Release -o /app/publish --no-restore

# ---- runtime stage: 서비스별로 필요한 최소 런타임만 담는다 ----
FROM ${RUNTIME_IMAGE} AS final
ARG PROJECT
ENV PROJECT=${PROJECT}
WORKDIR /app
COPY --from=build /app/publish ./

# exec-form ENTRYPOINT는 ${PROJECT} 같은 변수를 전개하지 않는다(문자 그대로 "${PROJECT}.dll"이라는
# 파일을 찾다가 실패) — 그래서 /bin/sh -c로 감싸 변수를 전개한다. "exec dotnet ..."으로 실행해야
# dotnet 프로세스가 PID 1이 되어 docker compose down이 보내는 SIGTERM을 직접 받는다(그렇지 않으면
# sh가 PID 1이 되어 신호가 dotnet까지 전달되지 않고, GameServer/AuthServer/MonitorServer에 새로
# 추가한 PosixSignalRegistration(SIGTERM) 핸들러가 무의미해진다). "$@"/"--"는 `docker compose run
# authserver --seed`처럼 뒤에 붙는 인자를 dotnet에 그대로 전달하기 위함이다.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet ${PROJECT}.dll \"$@\"", "--"]
