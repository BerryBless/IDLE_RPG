namespace MonitorServer;

/// <summary>
/// 브라우저에 서빙하는 단일 페이지 대시보드의 HTML을 보관합니다. 외부 CDN 의존 없이 인라인
/// CSS/JS만 사용합니다 — 이 프로젝트만 웹 의존성을 갖는다는 설계 결정(<c>plan/web_monitoring_0718.md</c>)에
/// 따라, 웹 계층 자체도 별도 정적 파일 서빙 인프라 없이 완전히 자체 완결적으로 동작하게 한다.
/// </summary>
/// <remarks>
/// <b>Thread Safety:</b> 불변 <see langword="const"/> 문자열이라 여러 요청 스레드가 동시에 읽어도 안전합니다.
/// <b>Memory Allocation:</b> 프로세스 시작 시 문자열 리터럴로 1회 인터닝되며, 요청마다 추가 할당이
/// 발생하지 않습니다(매 요청 동일한 <see langword="string"/> 참조를 그대로 응답 본문으로 사용).
/// </remarks>
public static class DashboardHtml
{
    /// <summary>대시보드 HTML 전체입니다. <c>GET /</c>가 그대로 반환합니다.</summary>
    public const string Page = """
    <!doctype html>
    <html lang="ko">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>IDLE_RPG 모니터링</title>
    <style>
      :root {
        color-scheme: light dark;
        --bg: #0b0d12;
        --panel: #151922;
        --panel-border: #262c3a;
        --text: #e6e9f0;
        --muted: #8b93a7;
        --accent: #5b8cff;
        --danger: #ef5a6f;
        --ok: #3ecf8e;
        --hp: #ef5a6f;
      }
      @media (prefers-color-scheme: light) {
        :root {
          --bg: #f4f6fb;
          --panel: #ffffff;
          --panel-border: #dde3ef;
          --text: #1a1e29;
          --muted: #5b6272;
          --accent: #3868e0;
          --danger: #d43a52;
          --ok: #1f9d63;
          --hp: #d43a52;
        }
      }
      * { box-sizing: border-box; }
      body {
        margin: 0;
        min-height: 100vh;
        background: var(--bg);
        color: var(--text);
        font-family: -apple-system, "Segoe UI", Pretendard, sans-serif;
        padding: 24px;
      }
      .wrap { max-width: 880px; margin: 0 auto; display: flex; flex-direction: column; gap: 16px; }
      header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; }
      h1 { font-size: 1.25rem; margin: 0; }
      .badge {
        display: inline-flex; align-items: center; gap: 6px;
        padding: 4px 10px; border-radius: 999px; font-size: 0.8rem; font-weight: 600;
        background: var(--panel); border: 1px solid var(--panel-border);
      }
      .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--muted); }
      .badge.on .dot { background: var(--ok); }
      .badge.off .dot { background: var(--danger); }
      .panel {
        background: var(--panel); border: 1px solid var(--panel-border);
        border-radius: 14px; padding: 20px;
      }
      .boss-head { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 10px; }
      .boss-head .label { color: var(--muted); font-size: 0.85rem; }
      .boss-hp-num { font-variant-numeric: tabular-nums; font-size: 1.05rem; }
      .hp-track { height: 18px; border-radius: 999px; background: var(--panel-border); overflow: hidden; }
      .hp-fill { height: 100%; background: var(--hp); width: 0%; transition: width 0.3s ease; }
      .hp-pct { text-align: right; color: var(--muted); font-size: 0.8rem; margin-top: 6px; }
      .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; }
      .stat { background: var(--panel); border: 1px solid var(--panel-border); border-radius: 12px; padding: 14px 16px; }
      .stat .k { color: var(--muted); font-size: 0.78rem; margin-bottom: 4px; }
      .stat .v { font-size: 1.3rem; font-weight: 700; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
      footer { color: var(--muted); font-size: 0.78rem; text-align: center; padding: 8px 0; }
      code { color: var(--accent); }
    </style>
    </head>
    <body>
    <div class="wrap">
      <header>
        <h1>IDLE_RPG 실시간 서버 모니터링</h1>
        <span id="conn-badge" class="badge off"><span class="dot"></span><span id="conn-label">연결 대기 중</span></span>
      </header>

      <section class="panel">
        <div class="boss-head">
          <span class="label">공유 레이드 보스 · 세대 <span id="generation">-</span></span>
          <span class="boss-hp-num"><span id="hp-current">-</span> / <span id="hp-max">-</span></span>
        </div>
        <div class="hp-track"><div id="hp-fill" class="hp-fill"></div></div>
        <div class="hp-pct" id="hp-pct">-</div>
      </section>

      <section class="grid">
        <div class="stat"><div class="k">접속자 수</div><div class="v" id="connected-count">-</div></div>
        <div class="stat"><div class="k">게임 리스너</div><div class="v" id="is-running">-</div></div>
        <div class="stat"><div class="k">거부된 연결</div><div class="v" id="rejected">-</div></div>
        <div class="stat"><div class="k">마지막 이벤트</div><div class="v" id="last-event">-</div></div>
        <div class="stat"><div class="k">MVP</div><div class="v" id="mvp-name">-</div></div>
        <div class="stat"><div class="k">MVP 누적 피해</div><div class="v" id="top-damage">-</div></div>
      </section>

      <footer>마지막 갱신: <span id="updated-at">-</span> · GameServer 텔레메트리(포트 <code>7779</code>) 1초 주기 구독</footer>
    </div>

    <script>
      const EVENT_LABELS = ["대기", "피해 발생", "보스 처치", "레이드 실패"];

      const el = (id) => document.getElementById(id);
      const fmt = (n) => Number(n).toLocaleString("ko-KR");

      function applySnapshot(s) {
        const badge = el("conn-badge");
        badge.classList.toggle("on", !!s.connected);
        badge.classList.toggle("off", !s.connected);
        el("conn-label").textContent = s.connected ? "GameServer 연결됨" : "GameServer 연결 끊김";

        el("generation").textContent = s.generation;
        el("hp-current").textContent = fmt(s.bossCurrentHp);
        el("hp-max").textContent = fmt(s.bossMaxHp);
        const pct = s.bossMaxHp > 0 ? (100 * s.bossCurrentHp / s.bossMaxHp) : 0;
        el("hp-fill").style.width = Math.max(0, Math.min(100, pct)) + "%";
        el("hp-pct").textContent = pct.toFixed(1) + "%";

        el("connected-count").textContent = fmt(s.connectedCount);
        el("is-running").textContent = s.isRunning ? "가동 중" : "정지";
        el("rejected").textContent = fmt(s.rejectedConnections);
        el("last-event").textContent = EVENT_LABELS[s.lastEvent] ?? String(s.lastEvent);
        el("mvp-name").textContent = s.mvpName && s.mvpName.length > 0 ? s.mvpName : "-";
        el("top-damage").textContent = fmt(s.topDamage);

        el("updated-at").textContent = s.connected
          ? new Date(s.updatedAtUtc).toLocaleTimeString("ko-KR")
          : "연결 끊김";
      }

      // EventSource: 브라우저가 연결 끊김 시 자체적으로 자동 재연결한다(스펙 동작) — 별도 재시도 로직 불필요.
      const source = new EventSource("/events");
      source.onmessage = (ev) => applySnapshot(JSON.parse(ev.data));
    </script>
    </body>
    </html>
    """;
}
