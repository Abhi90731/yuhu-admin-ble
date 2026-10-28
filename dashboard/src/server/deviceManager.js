/**
 * deviceManager.js — owns the per-device logcat stream lifecycle.
 *
 * - Polls `adb devices` to discover cabled devices.
 * - For each usable device, spawns a live `adb logcat` stream (mesh tags only).
 * - Parses each line and feeds the shared MeshState.
 * - Emits structured events to a sink callback (the server broadcasts them).
 *
 * READ-ONLY: only `adb devices` and `adb logcat` are ever spawned.
 */
import { createInterface } from 'node:readline';
import { listDevices, spawnLogcat } from './adb.js';
import { parseLine } from './parser.js';

const POLL_MS = 4000;

export class DeviceManager {
  /**
   * @param {import('./state.js').MeshState} state
   * @param {(event: object) => void} sink  called with {type, ...} for broadcast
   */
  constructor(state, sink) {
    this.state = state;
    this.sink = sink;
    /** serial -> ChildProcess */
    this.streams = new Map();
    this.pollTimer = null;
    this.adbError = null;
  }

  start() {
    this._poll();
    this.pollTimer = setInterval(() => this._poll(), POLL_MS);
  }

  stop() {
    if (this.pollTimer) clearInterval(this.pollTimer);
    for (const proc of this.streams.values()) {
      try { proc.kill(); } catch { /* noop */ }
    }
    this.streams.clear();
  }

  async _poll() {
    const res = await listDevices();
    if (!res.ok) {
      this.adbError = res.error;
      this.sink({ type: 'adb-error', error: res.error });
      return;
    }
    this.adbError = null;
    const usable = res.devices.filter((d) => d.state === 'device');
    this.state.setCabledDevices(res.devices);
    this.sink({ type: 'devices', devices: this.state.snapshot().devices });

    // start streams for newly-usable devices
    for (const d of usable) {
      if (!this.streams.has(d.serial)) this._startStream(d.serial);
    }
    // stop streams for devices that went away
    const live = new Set(usable.map((d) => d.serial));
    for (const serial of [...this.streams.keys()]) {
      if (!live.has(serial)) this._stopStream(serial);
    }
  }

  _startStream(serial) {
    const proc = spawnLogcat(serial, { dump: false });
    this.streams.set(serial, proc);

    const rl = createInterface({ input: proc.stdout });
    rl.on('line', (line) => {
      const ev = parseLine(serial, line);
      if (!ev) return;
      const deltas = this.state.apply(ev);
      // stream every parsed event to clients for the live-log view
      this.sink({ type: 'log', event: ev });
      for (const d of deltas) this.sink(d);
    });

    proc.stderr.on('data', (buf) => {
      this.sink({ type: 'stream-warning', serial, message: buf.toString().trim() });
    });
    proc.on('exit', (code) => {
      this.streams.delete(serial);
      this.sink({ type: 'stream-exit', serial, code });
    });
  }

  _stopStream(serial) {
    const proc = this.streams.get(serial);
    if (proc) {
      try { proc.kill(); } catch { /* noop */ }
    }
    this.streams.delete(serial);
  }

  /** One-shot: dump the recent logcat buffer for a device (for "view logs"). */
  dumpLogs(serial) {
    return new Promise((resolve) => {
      const proc = spawnLogcat(serial, { dump: true });
      const events = [];
      const rl = createInterface({ input: proc.stdout });
      rl.on('line', (line) => {
        const ev = parseLine(serial, line);
        if (ev) events.push(ev);
      });
      proc.on('exit', () => resolve(events));
      setTimeout(() => { try { proc.kill(); } catch { /* noop */ } }, 8000);
    });
  }
}
