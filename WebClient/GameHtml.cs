namespace WebClient;

/// <summary>
/// 게스트 플레이 단일 페이지 HTML입니다(외부 CDN 의존 0 — 인라인 CSS/JS만, MonitorServer
/// <c>DashboardHtml</c>과 동일한 배포 방식). 화면 1(입장: 닉네임 입력)과 화면 2(전투: 보스 HP바·
/// 처치 피드)로 구성되며, <c>/ws</c> WebSocket으로 <see cref="BridgeMessages"/> JSON을 주고받습니다.
/// </summary>
/// <remarks>
/// <b>Thread Safety:</b> 불변 상수 문자열 — Thread-safe. <b>Memory:</b> 프로세스당 1개 인터닝.
/// <b>Blocking:</b> 해당 없음.
/// </remarks>
public static class GameHtml
{
    /// <summary>GET /가 반환하는 페이지 전문입니다.</summary>
    public const string Page = """
<!DOCTYPE html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>IDLE RPG — 레이드</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: 'Segoe UI', 'Malgun Gothic', sans-serif;
    background: #12141a; color: #e7e9ee;
    min-height: 100vh; display: flex; align-items: center; justify-content: center;
  }
  .card {
    width: min(560px, 92vw); background: #1b1e27; border: 1px solid #2a2e3b;
    border-radius: 14px; padding: 28px; box-shadow: 0 8px 32px rgba(0,0,0,.45);
  }
  h1 { font-size: 20px; margin-bottom: 4px; }
  .sub { color: #8b93a7; font-size: 13px; margin-bottom: 20px; }
  /* ---- 화면 1: 입장 ---- */
  #join input {
    width: 100%; padding: 12px 14px; font-size: 15px; border-radius: 8px;
    border: 1px solid #333949; background: #12141a; color: #e7e9ee; outline: none;
  }
  #join input:focus { border-color: #5b8cff; }
  #join button {
    width: 100%; margin-top: 12px; padding: 12px; font-size: 15px; font-weight: 600;
    border: 0; border-radius: 8px; background: #5b8cff; color: #fff; cursor: pointer;
  }
  #join button:hover { background: #6d99ff; }
  /* ---- 화면 2: 전투 ---- */
  #game { display: none; }
  .topbar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 18px; }
  .me { font-weight: 600; }
  .badge { font-size: 12px; padding: 4px 10px; border-radius: 999px; background: #333949; color: #aeb6c8; }
  .badge.fighting { background: #14432a; color: #5fd38d; }
  .badge.dead { background: #4a1f24; color: #ff8a94; }
  .bossname { font-size: 14px; color: #aeb6c8; margin-bottom: 6px; display: flex; justify-content: space-between; }
  .hpbar {
    height: 26px; background: #12141a; border: 1px solid #333949; border-radius: 8px; overflow: hidden;
  }
  .hpfill {
    height: 100%; width: 100%;
    background: linear-gradient(90deg, #e6455a, #ff7448);
    transition: width .18s linear; /* MobHp 150ms 스로틀과 비슷한 속도로 미끄러지게 */
  }
  .hptext { text-align: center; font-size: 13px; color: #8b93a7; margin-top: 6px; font-variant-numeric: tabular-nums; }
  .feedtitle { font-size: 13px; color: #8b93a7; margin: 20px 0 8px; }
  #feed { list-style: none; font-size: 13px; max-height: 180px; overflow-y: auto; }
  #feed li { padding: 7px 10px; border-radius: 6px; margin-bottom: 4px; background: #171a22; color: #aeb6c8; }
  #feed li.me { background: #1d2c1f; color: #8fe0a4; font-weight: 600; }
  #retry {
    display: none; width: 100%; margin-top: 16px; padding: 11px; font-size: 14px; font-weight: 600;
    border: 0; border-radius: 8px; background: #5b8cff; color: #fff; cursor: pointer;
  }
</style>
</head>
<body>
<div class="card">
  <!-- 화면 1: 게스트 입장 -->
  <div id="join">
    <h1>IDLE RPG 레이드 참전</h1>
    <div class="sub">게스트로 입장해 공유 보스를 함께 공격합니다(전투는 서버가 자동 진행).</div>
    <input id="nick" maxlength="16" placeholder="닉네임 (비우면 Guest-XXXX 자동 생성)" autofocus>
    <button id="enter">입장</button>
  </div>
  <!-- 화면 2: 전투 -->
  <div id="game">
    <div class="topbar">
      <span class="me" id="myname">-</span>
      <span class="badge" id="status">연결 중…</span>
    </div>
    <div class="bossname"><span>레이드 보스 (7001)</span><span id="gen">Gen -</span></div>
    <div class="hpbar"><div class="hpfill" id="hpfill"></div></div>
    <div class="hptext" id="hptext">전투 대기 중… (첫 HP 브로드캐스트를 기다립니다)</div>
    <div class="feedtitle">처치 기록</div>
    <ul id="feed"></ul>
    <button id="retry">다시 입장</button>
  </div>
</div>
<script>
"use strict";
const $ = id => document.getElementById(id);
let ws = null;

function setStatus(text, cls) {
  const el = $("status");
  el.textContent = text;
  el.className = "badge" + (cls ? " " + cls : "");
}

function fmt(n) { return Number(n).toLocaleString("ko-KR"); }

function enter() {
  $("join").style.display = "none";
  $("game").style.display = "block";
  $("retry").style.display = "none";
  $("feed").innerHTML = "";
  setStatus("연결 중…");

  // 페이지를 서빙한 것과 동일한 호스트/포트의 /ws로 접속(http→ws, https→wss).
  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  ws = new WebSocket(proto + "//" + location.host + "/ws");

  ws.onopen = () => ws.send(JSON.stringify({ type: "join", nickname: $("nick").value }));

  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    switch (m.type) {
      case "joined":
        $("myname").textContent = m.nickname + " (#" + m.accountId + ")";
        break;
      case "auth":
        if (m.success) setStatus("전투 중", "fighting");
        else setStatus("인증 실패", "dead");
        break;
      case "bossHp": {
        const pct = m.maxHp > 0 ? (m.hp / m.maxHp) * 100 : 0;
        $("hpfill").style.width = pct + "%";
        $("hptext").textContent = fmt(m.hp) + " / " + fmt(m.maxHp) + " (" + pct.toFixed(1) + "%)";
        $("gen").textContent = "Gen " + m.generation;
        break;
      }
      case "bossDeath": {
        const li = document.createElement("li");
        li.textContent = "Gen " + m.generation + " 처치! MVP " + m.mvpName + " — " + fmt(m.topDamage) + " 딜";
        if (m.mvpIsMe) { li.classList.add("me"); li.textContent += " (나!)"; }
        $("feed").prepend(li);
        while ($("feed").children.length > 20) $("feed").lastChild.remove();
        break;
      }
      case "status":
        if (m.state === "fighting") setStatus("전투 중", "fighting");
        else if (m.state === "disconnected") onClosed("서버 연결 끊김");
        break;
      case "error":
        setStatus(m.message, "dead");
        break;
    }
  };

  ws.onclose = () => onClosed("연결 종료");
  ws.onerror = () => onClosed("연결 오류");
}

function onClosed(reason) {
  if (!ws) return;
  ws = null;
  setStatus(reason, "dead");
  $("retry").style.display = "block";
}

$("enter").addEventListener("click", enter);
$("nick").addEventListener("keydown", e => { if (e.key === "Enter") enter(); });
$("retry").addEventListener("click", enter);
</script>
</body>
</html>
""";
}
