# Docker Compose 컨테이너화

## 1. 배경 및 목적

GameServer·AuthServer·MonitorServer·MongoDB를 각각 `dotnet run`으로 따로 띄워야 했고, 모든
서버가 `127.0.0.1`(loopback)에만 바인딩하며 일부 호스트/포트가 소스 코드에 하드코딩되어 있었다.
목표는 세 가지였다: ① 핵심 3서버 + MongoDB를 Docker로 올리고, ② 스크립트 더블클릭 한 번으로 전체를
새로 빌드·재기동하며, ③ 서버를 자유롭게 추가/삭제할 수 있게 하는 것.

핵심 제약: 컨테이너끼리는 `127.0.0.1`로 서로 접근할 수 없다. 하지만 telemetry 리스너(7779)는 인증
게이트가 없고, game/auth 리스너는 TLS 미도입으로 토큰/비밀번호가 평문으로 오간다 — 그래서 loopback
바인딩은 우연이 아니라 의도된 보안 선택이었다(`GameServer/Main.cs`, `AuthServer/Program.cs` 기존
주석 참고). 따라서 무조건 `IPAddress.Any`로 바꾸는 대신, **바인드 주소를 환경변수로 빼되 기본값은
loopback을 유지**하고 Docker에서만 `0.0.0.0`으로 명시적으로 덮어쓰는 방식을 택했다. 컨테이너 외부
노출은 compose가 publish하는 포트로만 통제한다(telemetry·mongo는 미publish).

## 2. 설계 결정

| 항목 | 채택 | 대안 | 채택 사유 |
|------|------|------|-----------|
| 바인드 전략 | 환경변수 + loopback 기본값 (`IPAddress.Parse(env ?? "127.0.0.1")`) | 무조건 `IPAddress.Any`로 전환 | 로컬 `dotnet run` 시 텔레메트리(무인증)·평문 토큰이 LAN에 노출되는 회귀를 방지 |
| Dockerfile 구조 | 단일 파라미터화 멀티스테이지(`ARG PROJECT`/`RUNTIME_IMAGE`) | 서버별 개별 Dockerfile | "서버 추가"가 compose 블록 + COPY 한 줄로 끝나도록 — 반복 패턴 최소화 |
| 컨테이너 외부 노출 | 8080(대시보드)·7777(게임)·7778(로그인)만 publish | 전체 publish 또는 `network_mode: host` | telemetry(무인증)·mongo가 호스트/LAN에 노출되지 않도록 방어, Windows Docker에서 host 모드는 제약이 많음 |
| 서비스 간 탐색 | Docker Compose 서비스명 DNS(`gameserver`, `mongo`) | 고정 IP, 환경변수로 IP 하드코딩 | `SocketPipelineClient.ConnectAsync(host,port)`가 이미 DNS 해석을 수행(기존 코드 변경 불필요) |
| 시딩 | `docker compose run --rm authserver --seed` (1회성) | 별도 상시 시딩 서비스 | `up`에 포함되지 않아 재기동 시 재실행되지 않음, entrypoint가 인자를 그대로 전달하므로 신규 서비스 정의 불필요 |
| 원클릭 UX | Windows `.bat` 스크립트 | `docker compose` 명령 직접 사용, Makefile | Windows 환경에서 더블클릭만으로 실행, `make` 설치 불필요 |
| 우아한 종료 | `PosixSignalRegistration(SIGTERM)` 추가 | 방치(SIGKILL 대기) | `docker compose down`은 SIGTERM을 보내는데 기존 종료 로직은 SIGINT(`Console.CancelKeyPress`)만 처리 — 없으면 재기동마다 10초 stop-timeout 대기 후 강제 종료되어 NDJSON 로그 flush 유실 |

## 3. 컴포넌트 구조

```
docker-compose.yml (idlerpg-net, bridge)
├── mongo (mongo:7, volume: mongo-data, 미publish)
├── authserver (Dockerfile ARG PROJECT=AuthServer, runtime:10.0)
│     env: IDLERPG_AUTH_BIND=0.0.0.0, IDLERPG_MONGO_CONN=mongodb://mongo:27017,
│          IDLERPG_AUTH_HMAC_SECRET=${...:?} ── gameserver와 반드시 동일 값
│     depends_on: mongo(healthy) │ publish 7778
├── gameserver (Dockerfile ARG PROJECT=GameServer, runtime:10.0)
│     env: IDLERPG_GAME_BIND=0.0.0.0, IDLERPG_AUTH_HMAC_SECRET=${...:?}
│     publish 7777 (텔레메트리 7779는 미publish — 인증 없는 읽기 전용 리스너)
└── monitorserver (Dockerfile ARG PROJECT=MonitorServer, RUNTIME_IMAGE override=aspnet:10.0)
      env: IDLERPG_MONITOR_WEB_BIND=0.0.0.0, IDLERPG_MONITOR_GAME_HOST=gameserver
      depends_on: gameserver(soft, 자동 재접속) │ publish 8080

Dockerfile (레포 루트, 단일 파일 3서비스 공용)
├── build stage: mcr.microsoft.com/dotnet/sdk:10.0 → dotnet publish -c Release
└── final stage: ARG RUNTIME_IMAGE (기본 runtime:10.0, MonitorServer만 aspnet:10.0)
      ENTRYPOINT ["/bin/sh","-c","exec dotnet ${PROJECT}.dll \"$@\"","--"]
      (exec-form ENTRYPOINT는 ${PROJECT}를 전개하지 않아 sh -c로 감쌈;
       exec로 dotnet을 PID 1로 만들어 SIGTERM이 직접 전달되게 함)
```

### 3.1 ARG 스코프 함정(구현 중 발견)

Dockerfile에서 `ARG RUNTIME_IMAGE=...`를 `FROM build AS build` **다음**에 선언했더니
`base name (${RUNTIME_IMAGE}) should not be blank` 빌드 오류가 났다. Docker는 첫 `FROM` 이전에
선언한 전역 ARG만 다음 `FROM ${...}` 줄에서 참조할 수 있고, `FROM` 이후에 선언한 ARG는 그 스테이지
안에서만 유효하다. `ARG RUNTIME_IMAGE=...`를 파일 최상단(첫 `FROM`보다 먼저)으로 옮겨 해결했다.

## 4. 핵심 API / 사용 패턴

```
copy .env.example .env        # IDLERPG_AUTH_HMAC_SECRET을 32바이트 이상 값으로 교체
up.bat                          # 빌드 + 전체 스택 기동 (.env 없으면 안내 후 중단)
seed.bat                        # MongoDB에 더미 계정 3000개 시딩 (1회, 멱등)
logs.bat                        # 전체 컨테이너 로그 실시간 추적
down.bat                        # 전체 정지 (mongo-data 볼륨은 유지 — 재기동 시 재시딩 불필요)
```

서버 추가 시 반복 패턴(신규 Dockerfile 불필요):
1. `Dockerfile`의 restore 블록에 `COPY <NewServer>/<NewServer>.csproj <NewServer>/` 한 줄 추가
2. `docker-compose.yml`에 서비스 블록 복붙 → `PROJECT`/`RUNTIME_IMAGE`(콘솔=runtime, 웹=aspnet)/env/publish 포트 설정
3. 신규 공유 시크릿이 있으면 `.env.example`에 추가
4. `up.bat` 재실행

## 5. 변경 파일 목록

**수정:**
- `GameServer/Main.cs` — `Port`/`TelemetryPort` const → `IDLERPG_GAME_PORT`/`IDLERPG_GAME_TELEMETRY_PORT`
  환경변수 지역변수, 신규 `IDLERPG_GAME_BIND`로 바인드 주소 구성화, SIGTERM 핸들러 추가
- `AuthServer/Configuration/AuthServerConfig.cs` — `BindAddress` 신규 (`IDLERPG_AUTH_BIND`)
- `AuthServer/Program.cs` — `BindAddress` 사용, 로그 메시지 하드코딩 제거, SIGTERM 핸들러 추가
- `MonitorServer/Program.cs` — `GameServerHost`/`TelemetryPort` const → 환경변수, 신규
  `IDLERPG_MONITOR_WEB_BIND`로 웹 바인드 구성화, SIGTERM 핸들러 추가
- `.gitignore` — `.env` 추가(실제 비밀키 커밋 방지)

**신규:**
- `Dockerfile` — 단일 파라미터화 멀티스테이지 빌드(3서비스 공용)
- `.dockerignore`
- `docker-compose.yml` — mongo/authserver/gameserver/monitorserver 4서비스
- `.env.example` — `IDLERPG_AUTH_HMAC_SECRET` 템플릿(커밋 대상, 실제 `.env`는 gitignore)
- `up.bat` / `down.bat` / `logs.bat` / `seed.bat`

## 6. 빌드 검증

```
dotnet build IDLE_RPG.sln -c Debug      # 0 경고 신규 발생, 0 오류
dotnet build IDLE_RPG.sln -c Release    # 동일 (HMAC 환경변수 설정 시)
dotnet test IDLE_RPG.sln -c Debug       # GameServer.Tests 176/176, AuthServer.Tests 36/36,
                                         # EchoExample.Tests 13/13 — 전부 회귀 없음
                                         # (IdleRpg.HarnessTests의 3건 실패는 무관한 기존 결함,
                                         #  변경 전 스택으로도 동일하게 재현되어 사전 확인함)

copy .env.example .env
docker compose config                    # 문법 검증
docker compose up -d --build             # 4개 컨테이너 전부 Up/healthy 확인
curl http://localhost:8080               # HTTP 200
curl http://localhost:8080/events        # connected:true (MonitorServer→GameServer:7779 텔레메트리 링크 실증)
curl http://localhost:7779               # Connection refused (텔레메트리 미노출 확인)
docker compose run --rm authserver --seed         # "3000개 더미 계정 시딩 완료"
docker compose run --rm authserver --seed         # 재실행 시 중복 키 오류로 안전하게 스킵(멱등)
time docker compose down                 # 1.3초 내 종료(SIGTERM 처리 확인, 10초 SIGKILL 대기 없음)
docker compose up -d                     # 재기동
docker compose exec mongo mongosh --eval "db.getSiblingDB('idlerpg').accounts.countDocuments()"
                                          # 3000 — mongo-data 볼륨으로 계정 유지 확인
```

모든 항목 실측 확인 완료(2026-07-19).

## 7-1. 2026-07-19 추가: 재시작 정책 결정 경위 (`unless-stopped` → `always` → `"no"`)

"서버를 끄지 말고 무한으로 유지되게" 요청에 따라 처음엔 4서비스 전부 `restart: unless-stopped` →
`restart: always`로 바꿨다. 검증 중 `.bat` 인코딩을 진단하던 `cmd.exe` 호출 인자가 깨지면서 의도치
않게 `.env`의 `IDLERPG_AUTH_HMAC_SECRET` 값이 2바이트(`LO`)로 손상되는 사고가 발생했고,
`restart: always`가 이 손상된 값으로 무한 크래시 루프(`RestartCount=8`, exit 139)를 계속 재시도하고
있는 것을 발견했다 — 정책 자체는 정직하게 작동했지만, 설정 오류가 있으면 문제를 감춘 채 CPU/로그만
계속 소모하는 부작용이 드러났다.

이 부작용을 겪은 뒤 "크래시는 자동복구 하지마" 요청에 따라 최종적으로 4서비스 전부
**`restart: "no"`**로 변경했다(YAML에서 `no`는 따옴표 없이 쓰면 boolean으로 파싱될 수 있어 명시적으로
문자열 인용). 크래시가 나면 컨테이너는 `Exited` 상태로 멈춘 채 남고, 원인을 고친 뒤 `up.bat`으로
수동으로 다시 올려야 한다. 대신 이 정책은 호스트/Docker 재부팅 후에도 자동으로 다시 뜨지 않는다 —
애초 "무한 유지" 요청보다 "크래시는 조용히 재시도하지 말고 드러나게"가 최종 우선순위였다.

**최종 정책 실측 검증:** `docker compose run` 원샷 컨테이너에 `IDLERPG_AUTH_HMAC_SECRET=short`(고의
오류값)를 주입해 실제 fail-fast 크래시(exit 139)를 재현 — `RestartPolicy={"Name":"no",...}`인 상태로
8초 이상 대기해도 `RestartCount=0`, `Status=exited`를 계속 유지함을 확인(자동 재시작 없음).
(참고: 이 과정에서 `docker exec ... kill -9 1`로 컨테이너 내부 PID 1을 직접 죽이는 방식은 이
샌드박스 환경에서 신호가 제대로 전달되지 않아 신뢰할 수 없는 테스트 방법이었다 — 대신 실제 앱
레벨 예외로 크래시를 재현하는 방식으로 검증했다.)

## 7-2. 2026-07-19 추가: `.bat` 스크립트는 반드시 ASCII만 사용

최초 버전은 `up.bat`/`down.bat`/`logs.bat`/`seed.bat`의 안내 메시지를 한글(UTF-8)로 작성했는데,
사용자가 더블클릭하자 `docker compose build`가 `uild`/`ho`/`compose` 같은 조각으로 쪼개져 "명령을
찾을 수 없다"는 오류가 났다. 원인: `cmd.exe`는 콘솔 코드페이지가 기본적으로 한국어 Windows에서도
CP949(EUC-KR 계열)이고, 파일이 UTF-8(비 BOM)로 저장돼 있으면 멀티바이트 한글 시퀀스를 CP949로
잘못 해석해 바이트 정렬이 깨지고, 그 여파로 뒤따르는 영문 명령어 토큰까지 쪼개진다. `chcp 65001`로
콘솔 코드페이지를 UTF-8로 바꾸는 방법도 있지만 신뢰성이 낮아, 대신 4개 `.bat` 파일의 모든 텍스트를
순수 ASCII(영문)로 다시 작성해 코드페이지 문제 자체를 원천 차단했다(바이트 단위로 non-ASCII 0개
확인). **이 프로젝트에서 `.bat` 파일을 새로 만들거나 수정할 때는 절대 한글을 넣지 말 것.**

## 7. 향후 확장 포인트

- TLS 미도입 상태이므로 telemetry·mongo 미publish로 방어하고 있다. TLS 도입 후에는 바인드
  기본값을 `Any`로 바꾸는 재검토가 가능하다.
- Dockerfile의 build 스테이지가 서비스별로 재컴파일된다(레이어 캐시는 재사용되지만 컴파일 자체는
  중복). 서버 수가 늘어나 빌드 시간이 부담되면 공유 `publish-all` 베이스 스테이지 도입을 고려한다.
- 로그인 포트(7778)를 호스트에 publish했다 — 외부 클라이언트가 필요 없어지면 제거해 노출 표면을
  더 줄일 수 있다.
