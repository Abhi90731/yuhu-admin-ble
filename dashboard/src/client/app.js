/* app.js — YUHU dashboard frontend (plain JS, no build step). */

const state = {
  devices: [],
  nodes: [],
  links: [],
  health: [],
  timeline: [],
  exceptions: [],
  adminEnabled: false,
  live: true,
  deviceFilter: '',
};

const el = (id) => document.getElementById(id);

// ---- WebSocket ----
let ws;
function connect() {
  ws = new WebSocket(`ws://${location.host}`);
  ws.onopen = () => setWsStatus(true);
  ws.onclose = () => { setWsStatus(false); setTimeout(connect, 1500); };
  ws.onmessage = (e) => handleMessage(JSON.parse(e.data));
}

function setWsStatus(ok) {
  const b = el('ws-status');
  b.textContent = ok ? 'connected' : 'disconnected';
  b.className = 'badge ' + (ok ? 'badge-on' : 'badge-off');
}

function handleMessage(msg) {
  switch (msg.type) {
    case 'snapshot':
      Object.assign(state, msg.data);
      state.adminEnabled = msg.adminEnabled;
      applyAdminUi();
      renderAll();
      break;
    case 'devices':
      state.devices = msg.devices;
      renderDevices();
      renderDeviceFilter();
      break;
    case 'log':
      onLogEvent(msg.event);
      break;
    case 'exception':
      state.exceptions.unshift(msg.data);
      renderExceptions();
      break;
    case 'ble-sidecar':
      setSidecarStatus(msg.connected);
      break;
    case 'ble':
      handleBleEvent(msg.event);
      break;
    case 'adb-error':
      setWsStatus(false);
      el('ws-status').textContent = 'adb error';
      break;
    default:
      break;
  }
}

// Incrementally fold live log events into state (keeps UI responsive).
function onLogEvent(ev) {
  const flowKinds = ['send', 'recv', 'delivered', 'relay', 'dedup-drop'];
  if (flowKinds.includes(ev.kind)) {
    state.timeline.unshift({
      serial: ev.serial, ts: ev.ts, kind: ev.kind,
      seq: ev.seq ?? null, to: ev.to ?? null, from: ev.from ?? null, via: ev.via ?? null,
    });
    if (state.timeline.length > 500) state.timeline.length = 500;
    if (state.live) renderTimeline();
  }
  if (ev.kind === 'endpoint-up' && ev.dev) {
    upsertLink(ev.serial, ev.dev, null);
    renderGraph();
  }
  if (ev.kind === 'endpoint-down' && ev.dev) {
    state.links = state.links.filter((l) => !sameLink(l, ev.serial, ev.dev));
    renderGraph();
  }
  if (ev.kind === 'send' && ev.to) {
    upsertLink(ev.serial, ev.to, ev.via);
    renderGraph();
  }
}

function sameLink(l, a, b) {
  return (l.a === a && l.b === b) || (l.a === b && l.b === a);
}
function upsertLink(a, b, via) {
  const found = state.links.find((l) => sameLink(l, a, b));
  if (found) { if (via) found.via = via; }
  else state.links.push({ a, b, via });
}

// ---- Rendering ----
function renderAll() {
  renderDevices();
  renderHealth();
  renderGraph();
  renderTimeline();
  renderExceptions();
  renderDeviceFilter();
}

function renderDevices() {
  el('device-count').textContent = state.devices.length;
  el('devices').innerHTML = state.devices.map((d) => {
    const on = d.state === 'device';
    return `<div class="device-card">
      <div>
        <span class="dot ${on ? 'dot-on' : 'dot-off'}"></span>
        <strong>${d.model || d.serial}</strong>
        <div class="meta">${d.serial} · ${d.state}</div>
      </div>
      <button class="btn-small" onclick="viewLogs('${d.serial}')">view logs</button>
    </div>`;
  }).join('') || '<div class="empty">No devices attached.</div>';
}

function renderHealth() {
  el('health').innerHTML = state.health.map((h) => {
    const bt = pill(h.btOn, 'BT on', 'BT off');
    const adv = pill(h.advertising, 'advertising', 'not adv');
    const status = h.lastStatus != null ? `<span class="pill">status ${h.lastStatus}</span>` : '<span class="pill pill-unknown">status ?</span>';
    return `<div class="health-row">
      <span>${h.serial}</span>${bt}${adv}${status}
    </div>`;
  }).join('') || '<div class="empty">No health data yet.</div>';
}

function pill(val, onText, offText) {
  if (val === true) return `<span class="pill pill-ok">${onText}</span>`;
  if (val === false) return `<span class="pill pill-bad">${offText}</span>`;
  return `<span class="pill pill-unknown">?</span>`;
}

function renderGraph() {
  const svg = el('graph');
  const W = 800, H = 360;
  // collect node ids from devices + mesh nodes + link endpoints
  const ids = new Set();
  state.devices.forEach((d) => ids.add(d.serial));
  state.nodes.forEach((n) => ids.add(n.dev));
  state.links.forEach((l) => { ids.add(l.a); ids.add(l.b); });
  const nodeIds = [...ids];
  const n = nodeIds.length || 1;

  // layout nodes on a circle
  const cx = W / 2, cy = H / 2, r = Math.min(W, H) / 2 - 50;
  const pos = {};
  nodeIds.forEach((id, i) => {
    const a = (2 * Math.PI * i) / n - Math.PI / 2;
    pos[id] = { x: cx + r * Math.cos(a), y: cy + r * Math.sin(a) };
  });

  const edges = state.links.map((l) => {
    const p1 = pos[l.a], p2 = pos[l.b];
    if (!p1 || !p2) return '';
    const color = l.via === 'On Mesh' ? 'var(--mesh)' : 'var(--p2p)';
    return `<line x1="${p1.x}" y1="${p1.y}" x2="${p2.x}" y2="${p2.y}" stroke="${color}" stroke-width="2"/>`;
  }).join('');

  const nodes = nodeIds.map((id) => {
    const p = pos[id];
    const label = labelFor(id);
    return `<g>
      <circle cx="${p.x}" cy="${p.y}" r="10" fill="var(--accent)"/>
      <text x="${p.x}" y="${p.y - 16}" fill="var(--text)" font-size="11" text-anchor="middle">${label}</text>
    </g>`;
  }).join('');

  svg.innerHTML = edges + nodes || '';
  if (!nodeIds.length) svg.innerHTML = '<text x="400" y="180" fill="#9d99b5" font-size="13" text-anchor="middle">No mesh activity yet</text>';
}

function labelFor(id) {
  const dev = state.nodes.find((nd) => nd.dev === id);
  if (dev && dev.name) return dev.name;
  const d = state.devices.find((x) => x.serial === id);
  if (d && d.model) return d.model;
  return id.length > 10 ? id.slice(0, 8) + '…' : id;
}

function renderTimeline() {
  const rows = state.timeline
    .filter((t) => !state.deviceFilter || t.serial === state.deviceFilter)
    .slice(0, 200)
    .map((t) => {
      const target = t.to ? `→ ${t.to}` : t.from ? `← ${t.from}` : '';
      const via = t.via ? ` (${t.via})` : '';
      const seq = t.seq != null ? `seq=${t.seq}` : '';
      return `<div class="tl-row">
        <span class="tl-ts">${t.ts || ''}</span>
        <span class="tl-kind k-${t.kind}">${t.kind}</span>
        <span>${seq} ${target}${via}</span>
        <span class="tl-serial">${t.serial}</span>
      </div>`;
    }).join('');
  el('timeline').innerHTML = rows || '<div class="empty">No message-flow events yet.</div>';
}

function renderExceptions() {
  el('exception-count').textContent = state.exceptions.length;
  el('exceptions').innerHTML = state.exceptions.map((e) => {
    const p = e.payload || {};
    const title = p.type || p.exception || 'Exception';
    const msg = p.message || e.raw || '(no detail)';
    const where = p.thread || p.where || '';
    return `<div class="exc-row">
      <div class="exc-head"><span>${title} · ${e.serial}</span><span>${e.ts || ''} ${where}</span></div>
      <div class="exc-msg">${escapeHtml(msg)}</div>
    </div>`;
  }).join('') || '<div class="empty">No exceptions reported.</div>';
}

function renderDeviceFilter() {
  const sel = el('device-filter');
  const cur = sel.value;
  sel.innerHTML = '<option value="">all devices</option>' +
    state.devices.map((d) => `<option value="${d.serial}">${d.model || d.serial}</option>`).join('');
  sel.value = cur;
}

function applyAdminUi() {
  const b = el('admin-status');
  b.textContent = state.adminEnabled ? 'ADMIN' : 'read-only';
  b.className = 'badge ' + (state.adminEnabled ? 'badge-admin' : '');
  el('discover-btn').classList.toggle('hidden', !state.adminEnabled);
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ---- Actions ----
window.viewLogs = async function (serial) {
  const res = await fetch(`/api/devices/${serial}/logs`);
  const data = await res.json();
  const w = window.open('', '_blank');
  const text = data.events.map((e) => `[${e.ts || ''}] ${e.tag || ''}: ${e.text || ''}`).join('\n');
  w.document.write(`<pre style="font:12px monospace;padding:16px;background:#0f0e17;color:#e8e6f0">${escapeHtml(text)}</pre>`);
  w.document.title = `logs ${serial}`;
};

el('live-toggle').addEventListener('change', (e) => { state.live = e.target.checked; });
el('device-filter').addEventListener('change', (e) => { state.deviceFilter = e.target.value; renderTimeline(); });
el('discover-btn').addEventListener('click', async () => {
  const res = await fetch('/api/admin/discover', { method: 'POST' });
  const data = await res.json();
  alert(data.note || JSON.stringify(data));
});

// ---- BLE Admin (sidecar) ----
const ble = { peers: [], connected: false, authed: false };

function sendBle(cmd) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'ble-cmd', cmd }));
}

function setSidecarStatus(connected) {
  const b = el('sidecar-status');
  b.textContent = connected ? 'sidecar online' : 'sidecar offline';
  b.className = 'badge ' + (connected ? 'badge-on' : 'badge-off');
}

function handleBleEvent(ev) {
  switch (ev.ev) {
    case 'peers':
      ble.peers = ev.peers || [];
      renderBlePeers();
      break;
    case 'connected':
      ble.connected = true;
      ble.authed = false;
      setBleConnState(`connected: ${ev.name || ev.id || '?'} — authenticating…`, false);
      break;
    case 'auth':
      ble.authed = true;
      setBleConnState(`authenticated: ${ble.peers.find(p => p.id)?.name || 'device'}`, true);
      appendBleLog('system', '>>> ADMIN AUTHENTICATED');
      break;
    case 'disconnected':
      ble.connected = false; ble.authed = false;
      setBleConnState('not connected', false);
      break;
    case 'log':
      appendBleLog(ev.tier || 'log', `--- ${(ev.lines || []).length} lines ---`);
      (ev.lines || []).forEach((l) => appendBleLog(ev.tier || 'log', l));
      break;
    case 'live':
      appendBleLog('live', ev.line);
      break;
    case 'sys':
      appendBleLog('sys', ev.line);
      break;
    case 'status':
      appendBleLog('status', `[${ev.text}]`);
      break;
    default:
      break;
  }
}

function renderBlePeers() {
  const host = el('ble-peers');
  if (!ble.peers.length) {
    host.innerHTML = '<div class="empty">No BLE devices discovered. Start the C# sidecar (dotnet run --bridge).</div>';
    return;
  }
  host.innerHTML = ble.peers.map((p) => {
    const label = p.name || '(no name)';
    const rssi = p.rssi != null ? `${p.rssi} dBm` : '';
    return `<div class="ble-peer">
      <span><strong>${escapeHtml(label)}</strong> <span class="meta">${p.id || p.addr} · ${rssi}</span></span>
      <button class="btn-small" onclick="bleConnect('${p.addr}')">connect</button>
    </div>`;
  }).join('');
}

function setBleConnState(text, authed) {
  const el2 = el('ble-conn-state');
  el2.textContent = text;
  el2.className = 'pill ' + (authed ? 'pill-ok' : 'pill-unknown');
  document.querySelectorAll('#ble-controls button[data-ble]').forEach((b) => {
    if (b.dataset.ble !== 'clear') b.disabled = !authed;
  });
}

function appendBleLog(tier, line) {
  const host = el('ble-logs');
  const div = document.createElement('div');
  div.className = `ble-line ble-${tier}`;
  div.textContent = line;
  host.appendChild(div);
  // cap DOM size
  while (host.children.length > 1000) host.removeChild(host.firstChild);
  host.scrollTop = host.scrollHeight;
}

window.bleConnect = function (addr) {
  appendBleLog('status', `[connecting ${addr}…]`);
  sendBle({ cmd: 'connect', addr });
};

document.querySelectorAll('#ble-controls button[data-ble]').forEach((btn) => {
  btn.addEventListener('click', () => {
    switch (btn.dataset.ble) {
      case 'state': sendBle({ cmd: 'state' }); break;
      case 'detail': sendBle({ cmd: 'detail', window: 5 }); break;
      case 'exc': sendBle({ cmd: 'exc' }); break;
      case 'live-on': sendBle({ cmd: 'live', on: true }); break;
      case 'live-off': sendBle({ cmd: 'live', on: false }); break;
      case 'sys-on': sendBle({ cmd: 'system', on: true }); break;
      case 'sys-off': sendBle({ cmd: 'system', on: false }); break;
      case 'clear': el('ble-logs').innerHTML = ''; break;
    }
  });
});

connect();
