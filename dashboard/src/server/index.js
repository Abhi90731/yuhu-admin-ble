/**
 * index.js — YUHU dashboard server.
 *
 * Express serves the static frontend + a small REST API; a WebSocket pushes
 * live events (devices, logs, links, exceptions) to the browser.
 *
 * Admin mode: gated by a flag (env YUHU_ADMIN=1 or config). When OFF the
 * dashboard is strictly read-only (original kickoff behavior). When ON it
 * unlocks the control-panel endpoints (ping-sweep discovery, etc). Phase 3
 * remote-control actions are intentionally NOT implemented yet.
 */
import express from 'express';
import { WebSocketServer } from 'ws';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { MeshState } from './state.js';
import { DeviceManager } from './deviceManager.js';
import { BleBridge } from './bleBridge.js';
import { adbPath } from './adb.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT || 4317);
const ADMIN_ENABLED = process.env.YUHU_ADMIN === '1' || process.env.YUHU_ADMIN === 'true';

// Dashboard login gate (just keeps the UI from being wide open; separate from
// the BLE admin cert trust). Override via env; defaults to admin/admin.
const UI_USER = process.env.YUHU_UI_USER || 'admin';
const UI_PASS = process.env.YUHU_UI_PASS || 'admin';

const app = express();
app.use(express.json());
app.use(express.static(join(__dirname, '..', 'client')));

const server = createServer(app);
const wss = new WebSocketServer({ server });

const state = new MeshState();

/** Broadcast a JSON message to all connected dashboard clients. */
function broadcast(msg) {
  const data = JSON.stringify(msg);
  for (const client of wss.clients) {
    if (client.readyState === 1) client.send(data);
  }
}

const deviceManager = new DeviceManager(state, broadcast);

// BLE admin sidecar bridge: forwards BLE admin events to the browser and
// relays browser commands to the C# sidecar (if running).
const bleBridge = new BleBridge(broadcast);

// ---- REST API ----
app.post('/api/login', (req, res) => {
  const { username, password } = req.body || {};
  if (username === UI_USER && password === UI_PASS) res.json({ ok: true });
  else res.status(401).json({ ok: false, error: 'invalid credentials' });
});

app.get('/api/config', (_req, res) => {
  res.json({ adminEnabled: ADMIN_ENABLED, adbPath, port: PORT });
});

app.get('/api/snapshot', (_req, res) => {
  res.json(state.snapshot());
});

app.get('/api/devices/:serial/logs', async (req, res) => {
  const events = await deviceManager.dumpLogs(req.params.serial);
  res.json({ serial: req.params.serial, events });
});

// ---- Admin-gated endpoints (Phase 1/2: discovery only) ----
app.post('/api/admin/discover', (_req, res) => {
  if (!ADMIN_ENABLED) {
    res.status(403).json({ error: 'admin mode disabled' });
    return;
  }
  // Ping-sweep over the mesh needs the app-side `adm` message type via a
  // cabled gateway device. Placeholder until the app change lands; cabled
  // devices are already discovered automatically by the DeviceManager poll.
  res.json({
    ok: true,
    note: 'Cabled devices auto-discovered. Mesh-only ping-sweep requires the app-side adm message type (in the ProjectX-admin worktree).',
    pending: true,
  });
});

// ---- WebSocket: push snapshot on connect, then live deltas ----
wss.on('connection', (ws) => {
  ws.send(JSON.stringify({ type: 'snapshot', data: state.snapshot(), adminEnabled: ADMIN_ENABLED }));

  // Browser → server commands (BLE admin control from the UI).
  ws.on('message', (raw) => {
    let msg;
    try { msg = JSON.parse(raw.toString()); } catch { return; }
    if (msg.type === 'ble-cmd' && msg.cmd) {
      bleBridge.send(msg.cmd); // relay to the C# sidecar
    }
  });
});

server.listen(PORT, () => {
  deviceManager.start();
  bleBridge.start();
  console.log(`\n  YUHU dashboard → http://localhost:${PORT}`);
  console.log(`  adb:   ${adbPath}`);
  console.log(`  admin: ${ADMIN_ENABLED ? 'ENABLED' : 'disabled (read-only)'}`);
  console.log(`  ble:   bridging to C# sidecar (ws://127.0.0.1:4318) if running\n`);
});

process.on('SIGINT', () => {
  deviceManager.stop();
  server.close(() => process.exit(0));
});
