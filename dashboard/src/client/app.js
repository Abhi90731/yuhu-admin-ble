/* app.js — YUHU Mesh Admin dashboard (login + 3 tabs + BLE admin). */

const el = (id) => document.getElementById(id);
const escapeHtml = (s) => String(s).replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// ---------- Login gate (simple client gate; server also enforces) ----------
const LOGIN_KEY = 'yuhu_admin_authed';
function showApp() { el('login').classList.add('hidden'); el('app').classList.remove('hidden'); connect(); }
if (sessionStorage.getItem(LOGIN_KEY) === '1') showApp();

el('login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const u = el('login-user').value.trim();
  const p = el('login-pass').value;
  try {
    const res = await fetch('/api/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: u, password: p }),
    });
    if (res.ok) { sessionStorage.setItem(LOGIN_KEY, '1'); showApp(); }
    else el('login-error').textContent = 'Invalid username or password';
  } catch { el('login-error').textContent = 'Server unreachable'; }
});
el('logout-btn').addEventListener('click', () => { sessionStorage.removeItem(LOGIN_KEY); location.reload(); });

// ---------- Sidebar / tabs ----------
el('sidebar-toggle').addEventListener('click', () => el('app').classList.toggle('collapsed'));
document.querySelectorAll('.nav-item').forEach((btn) => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('.nav-item').forEach((b) => b.classList.remove('active'));
    document.querySelectorAll('.tab').forEach((t) => t.classList.remove('active'));
    btn.classList.add('active');
    el('tab-' + btn.dataset.tab).classList.add('active');
  });
});

// ---------- State ----------
const S = {
  peers: [], connected: false, authed: false, connectedPeer: null,
  timeline: [], ops: {}, opsDetail: {}, exceptions: [],
  logBuf: { state: [], detail: [], live: [], system: [], exceptions: [] },
  activeLog: 'state', pendingBatch: { state: [], detail: [], exceptions: [] },
};

// ---------- WebSocket ----------
let ws;
function connect() {
  ws = new WebSocket(`ws://${location.host}`);
  ws.onopen = () => setWs(true);
  ws.onclose = () => { setWs(false); setTimeout(connect, 1500); };
  ws.onmessage = (e) => handle(JSON.parse(e.data));
}
function setWs(ok) { const b = el('ws-status'); b.textContent = ok ? 'connected' : 'disconnected'; b.className = 'badge ' + (ok ? 'badge-on' : 'badge-off'); }
function sendBle(cmd) { if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'ble-cmd', cmd })); }

function handle(msg) {
  switch (msg.type) {
    case 'ble-sidecar': setSidecar(msg.connected); break;
    case 'ble': onBle(msg.event); break;
    default: break;
  }
}
function setSidecar(c) { const b = el('sidecar-status'); b.textContent = c ? 'sidecar online' : 'sidecar offline'; b.className = 'badge ' + (c ? 'badge-on' : 'badge-off'); }

// ---------- BLE events ----------
function onBle(ev) {
  switch (ev.ev) {
    case 'peers': S.peers = ev.peers || []; renderPeers(); break;
    case 'connected':
      S.connected = true; S.authed = false; S.connectedPeer = { name: ev.name, id: ev.id };
      setConn(`connected: ${ev.name || ev.id || '?'} — authenticating…`, 'warn');
      break;
    case 'auth':
      S.authed = true;
      setConn(`authenticated: ${S.connectedPeer?.name || 'device'}`, 'ok');
      el('disconnect-btn').classList.remove('hidden');
      break;
    case 'disconnected':
      S.connected = false; S.authed = false; S.connectedPeer = null;
      setConn('not connected', 'unknown');
      el('disconnect-btn').classList.add('hidden');
      break;
    case 'log': onLogBatch(ev); break;
    case 'live': ingestLogLine('live', ev.line); ingestForAnalytics(ev.line); break;
    case 'sys': ingestLogLine('system', ev.line); break;
    case 'status': ingestLogLine('status', `[${ev.text}]`); break;
    default: break;
  }
}

// Batched LOG responses accumulate until done=true.
function onLogBatch(ev) {
  const tier = ev.tier === 'batch' ? S.activeLog : ev.tier; // bridge may tag 'batch'
  const dest = (tier === 'detail' || tier === 'state' || tier === 'exceptions') ? tier : 'detail';
  if (!S.pendingBatch[dest]) S.pendingBatch[dest] = [];
  (ev.lines || []).forEach((l) => S.pendingBatch[dest].push(l));
  if (ev.done === undefined || ev.done === true) {
    S.logBuf[dest] = S.pendingBatch[dest].slice();
    S.pendingBatch[dest] = [];
    // feed analytics + timeline from detail/state pulls
    S.logBuf[dest].forEach((l) => ingestForAnalytics(l));
    if (S.activeLog === dest) renderLogView();
    if (dest === 'exceptions') renderExceptions();
  }
}

// ---------- Device list + signal bars ----------
function signalBars(rssi) {
  // rssi dBm: -50 strong .. -100 weak -> 0..5 bars
  const level = rssi == null ? 0 : Math.max(0, Math.min(5, Math.round((rssi + 100) / 10)));
  let bars = '';
  for (let i = 1; i <= 5; i++) bars += `<span class="bar ${i <= level ? 'on' : ''}"></span>`;
  return `<span class="signal">${bars}</span><span class="signal-dbm">${rssi != null ? rssi + ' dBm' : ''}</span>`;
}
function renderPeers() {
  const host = el('ble-peers');
  if (!S.peers.length) { host.innerHTML = '<div class="empty">Scanning for devices… (start the sidecar: <code>dotnet run -- --bridge</code>)</div>'; return; }
  host.innerHTML = S.peers.map((p) => {
    const isConn = S.connected && S.connectedPeer && (S.connectedPeer.id === p.id);
    return `<div class="device-card">
      <div class="device-top">
        <div><div class="device-name">${escapeHtml(p.name || '(no name)')}</div>
        <div class="device-id">${p.id || p.addr}</div></div>
        ${signalBars(p.rssi)}
      </div>
      <div>
        ${isConn
          ? '<span class="pill pill-ok">connected</span>'
          : `<button class="btn-small" onclick="bleConnect('${p.addr}')">connect</button>`}
      </div>
    </div>`;
  }).join('');
}
window.bleConnect = (addr) => { ingestLogLine('status', `[connecting ${addr}…]`); sendBle({ cmd: 'connect', addr }); };
el('disconnect-btn').addEventListener('click', () => sendBle({ cmd: 'disconnect' }));

function setConn(text, kind) { const e = el('conn-state'); e.textContent = text; e.className = 'pill pill-' + kind; }

// ---------- Timeline + Analytics (parsed from log lines) ----------
const FLOW_RE = /\b(SEND|RECV|DELIVERED|RELAY|DEDUP-DROP)\b/;
function ingestForAnalytics(line) {
  // line format: "<ts> <TAG>: <verb> ..."
  const m = line.match(/^(\d+)\s+(\w+):\s*(.*)$/);
  if (!m) return;
  const [, ts, , body] = m;
  const verbMatch = body.match(/^([A-Z][A-Z-]+)/);
  if (!verbMatch) return;
  const op = verbMatch[1];
  // ops count
  S.ops[op] = (S.ops[op] || 0) + 1;
  if (!S.opsDetail[op]) S.opsDetail[op] = [];
  if (S.opsDetail[op].length < 200) S.opsDetail[op].push({ ts: Number(ts), body });
  // timeline (flow ops only)
  if (FLOW_RE.test(body)) {
    const to = (body.match(/to=([^\s]+)/) || [])[1];
    const from = (body.match(/from=([^\s]+)/) || [])[1];
    const seq = (body.match(/seq=(\d+)/) || [])[1];
    const via = (body.match(/via=On\s+(P2P|Mesh)/) || [])[0];
    S.timeline.unshift({ ts: Number(ts), kind: op.toLowerCase(), to, from, seq, via });
    if (S.timeline.length > 300) S.timeline.length = 300;
    renderTimeline();
  }
  renderOpsChart();
}

function renderTimeline() {
  const host = el('timeline');
  if (!S.timeline.length) { host.innerHTML = '<div class="empty">No message-flow events yet. Connect to a device.</div>'; return; }
  host.innerHTML = S.timeline.slice(0, 200).map((t) => {
    const tgt = t.to ? `→ ${t.to}` : t.from ? `← ${t.from}` : '';
    const seq = t.seq ? `seq=${t.seq}` : '';
    return `<div class="tl-row">
      <span class="tl-ts">${fmtTs(t.ts)}</span>
      <span class="tl-kind k-${t.kind}">${t.kind}</span>
      <span>${seq} ${tgt} ${t.via || ''}</span>
    </div>`;
  }).join('');
}

function renderOpsChart() {
  const host = el('ops-chart');
  const entries = Object.entries(S.ops).sort((a, b) => b[1] - a[1]);
  if (!entries.length) { host.innerHTML = '<div class="empty">No operations recorded yet.</div>'; return; }
  const max = entries[0][1];
  host.innerHTML = entries.map(([op, n]) => `
    <div class="bar-row" onclick="drillOp('${op}')">
      <span class="bar-label">${op}</span>
      <span class="bar-track"><span class="bar-fill" style="width:${(n / max * 100).toFixed(1)}%"></span></span>
      <span class="bar-count">${n}</span>
    </div>`).join('');
}
window.drillOp = (op) => {
  el('ops-drill-title').textContent = `Operation: ${op}`;
  const rows = (S.opsDetail[op] || []).slice().reverse();
  el('ops-drill').innerHTML = rows.length
    ? rows.map((r) => `<div class="drill-row"><span>${fmtTs(r.ts)}</span><span>${escapeHtml(r.body)}</span></div>`).join('')
    : '<div class="empty">No detail.</div>';
};

function renderExceptions() {
  const list = S.logBuf.exceptions || [];
  el('exc-summary').innerHTML = `<span class="exc-count">${list.length}</span> <span class="muted">exception record(s)</span>`;
  el('exc-list').innerHTML = list.length
    ? list.map((l) => `<div class="exc-row"><div class="exc-msg">${escapeHtml(l)}</div></div>`).join('')
    : '<div class="empty">No exceptions reported.</div>';
}

// ---------- Log viewer ----------
document.querySelectorAll('#log-switcher .seg').forEach((seg) => {
  seg.addEventListener('click', () => {
    document.querySelectorAll('#log-switcher .seg').forEach((s) => s.classList.remove('active'));
    seg.classList.add('active');
    S.activeLog = seg.dataset.log;
    requestLog(S.activeLog);
    renderLogView();
  });
});
el('log-refresh').addEventListener('click', () => requestLog(S.activeLog));
el('log-clear').addEventListener('click', () => { S.logBuf[S.activeLog] = []; renderLogView(); });

function requestLog(type) {
  if (!S.authed) { ingestLogLine('status', '[not authenticated — connect to a device first]'); return; }
  switch (type) {
    case 'state': sendBle({ cmd: 'state' }); break;
    case 'detail': sendBle({ cmd: 'detail', window: 5 }); break;
    case 'exceptions': sendBle({ cmd: 'exc' }); break;
    case 'live': sendBle({ cmd: 'live', on: true }); break;      // start streaming
    case 'system': sendBle({ cmd: 'system', on: true }); break;
  }
}
function ingestLogLine(tier, line) {
  if (!S.logBuf[tier]) S.logBuf[tier] = [];
  S.logBuf[tier].push(line);
  if (S.logBuf[tier].length > 2000) S.logBuf[tier].shift();
  if (S.activeLog === tier) renderLogView();
}
function renderLogView() {
  const host = el('log-view');
  const lines = S.logBuf[S.activeLog] || [];
  if (!lines.length) { host.innerHTML = '<div class="empty">No ' + S.activeLog + ' logs yet.</div>'; return; }
  host.innerHTML = lines.map((l) => `<div class="log-line lt-${S.activeLog}">${escapeHtml(l)}</div>`).join('');
  host.scrollTop = host.scrollHeight;
}

function fmtTs(ms) {
  if (!ms) return '';
  const d = new Date(Number(ms));
  return d.toLocaleTimeString('en-GB', { hour12: false }) + '.' + String(d.getMilliseconds()).padStart(3, '0');
}
