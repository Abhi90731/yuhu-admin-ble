/**
 * bleBridge.js — connects the dashboard server to the C# BLE admin sidecar.
 *
 * The C# app (run with `--bridge`) exposes a local WebSocket at
 * ws://127.0.0.1:4318/. This module connects to it, forwards admin events to
 * the dashboard's browser clients, and relays browser commands back to the
 * sidecar. If the sidecar isn't running, it retries quietly — the dashboard
 * still works for adb logs without it.
 */
import { WebSocket } from 'ws';

const SIDECAR_URL = process.env.YUHU_BLE_SIDECAR || 'ws://127.0.0.1:4318/';

export class BleBridge {
  /**
   * @param {(msg: object) => void} toBrowser  broadcast an event to dashboard clients
   */
  constructor(toBrowser) {
    this.toBrowser = toBrowser;
    this.ws = null;
    this.connected = false;
    this.retryTimer = null;
  }

  start() {
    this._connect();
  }

  _connect() {
    try {
      this.ws = new WebSocket(SIDECAR_URL);
    } catch {
      this._scheduleRetry();
      return;
    }

    this.ws.on('open', () => {
      this.connected = true;
      this.toBrowser({ type: 'ble-sidecar', connected: true });
      this.send({ cmd: 'scan' });
    });

    this.ws.on('message', (data) => {
      let ev;
      try { ev = JSON.parse(data.toString()); } catch { return; }
      // forward every sidecar event to the browser under a ble- namespace
      this.toBrowser({ type: 'ble', event: ev });
    });

    this.ws.on('close', () => {
      this.connected = false;
      this.toBrowser({ type: 'ble-sidecar', connected: false });
      this._scheduleRetry();
    });

    this.ws.on('error', () => {
      // handled by close
    });
  }

  _scheduleRetry() {
    if (this.retryTimer) return;
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this._connect();
    }, 2000);
  }

  /** Relay a command from the browser to the sidecar. */
  send(cmd) {
    if (this.ws && this.connected) {
      try { this.ws.send(JSON.stringify(cmd)); } catch { /* noop */ }
    }
  }
}
