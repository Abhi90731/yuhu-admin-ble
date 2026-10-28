/**
 * state.js — in-memory mesh state aggregator.
 *
 * Consumes structured events (from parser.js) and maintains a live model:
 *   - devices: cabled (from adb) + mesh-only (seen in logs / via ping)
 *   - links:   device <-> device edges, labeled On P2P / On Mesh
 *   - health:  per-device connection health snapshot
 *   - timeline: recent message-flow events (bounded ring buffer)
 *   - exceptions: device-reported crashes/exceptions (Part B)
 *
 * This is metadata only. No message content is ever stored.
 */

const TIMELINE_MAX = 500;
const EXCEPTIONS_MAX = 200;
const LINK_STALE_MS = 15000; // link considered dead if no activity for this long

export class MeshState {
  constructor() {
    /** serial -> { serial, state, model, dev, name } (cabled devices) */
    this.devices = new Map();
    /** short dev id -> { dev, name, lastSeen, serial? } (mesh-visible nodes) */
    this.nodes = new Map();
    /** "a~b" -> { a, b, via, lastSeen } */
    this.links = new Map();
    /** serial -> health snapshot */
    this.health = new Map();
    this.timeline = [];
    this.exceptions = [];
  }

  setCabledDevices(list) {
    const seen = new Set();
    for (const d of list) {
      seen.add(d.serial);
      const prev = this.devices.get(d.serial) || {};
      this.devices.set(d.serial, { ...prev, ...d });
      if (!this.health.has(d.serial)) {
        this.health.set(d.serial, {
          serial: d.serial,
          advertising: null,
          btOn: null,
          lastStatus: null,
          links: 0,
          lastEventAt: null,
        });
      }
    }
    // mark removed (unplugged) devices
    for (const serial of this.devices.keys()) {
      if (!seen.has(serial)) {
        const d = this.devices.get(serial);
        this.devices.set(serial, { ...d, state: 'offline' });
      }
    }
  }

  /** Apply a parsed event; returns a list of "delta" notifications to broadcast. */
  apply(ev) {
    if (!ev) return [];
    const deltas = [];
    const h = this.health.get(ev.serial);
    if (h) h.lastEventAt = ev.receivedAt;

    switch (ev.kind) {
      case 'endpoint-up': {
        if (ev.dev) {
          this._touchNode(ev.dev, null, ev.serial);
          const key = this._linkKey(ev.serial, ev.dev);
          this.links.set(key, { a: ev.serial, b: ev.dev, via: null, lastSeen: ev.receivedAt });
          if (h) h.links = this._countLinks(ev.serial);
        }
        break;
      }
      case 'endpoint-down': {
        if (ev.dev) {
          const key = this._linkKey(ev.serial, ev.dev);
          this.links.delete(key);
          if (h) h.links = this._countLinks(ev.serial);
        }
        break;
      }
      case 'send': {
        if (ev.to) {
          this._touchNode(ev.to);
          const key = this._linkKey(ev.serial, ev.to);
          const link = this.links.get(key) || { a: ev.serial, b: ev.to };
          link.via = ev.via || link.via;
          link.lastSeen = ev.receivedAt;
          this.links.set(key, link);
        }
        this._pushTimeline(ev);
        break;
      }
      case 'recv':
        if (ev.from) this._touchNode(ev.from);
        this._pushTimeline(ev);
        break;
      case 'delivered':
      case 'relay':
      case 'dedup-drop':
        this._pushTimeline(ev);
        break;
      case 'advertise-ok':
        if (h) h.advertising = true;
        break;
      case 'connect-out':
      case 'server-conn':
        if (h) h.lastStatus = ev.status ?? h.lastStatus;
        break;
      case 'client-disc':
        if (h) h.lastStatus = ev.status ?? h.lastStatus;
        break;
      case 'bt-on':
        if (h) h.btOn = true;
        break;
      case 'bt-off':
        if (h) h.btOn = false;
        break;
      case 'exception':
        this._pushException(ev);
        deltas.push({ type: 'exception', data: this.exceptions[0] });
        break;
      case 'pong':
        if (ev.payload?.dev) this._touchNode(ev.payload.dev, ev.payload.name);
        break;
      default:
        break;
    }
    return deltas;
  }

  _touchNode(dev, name = null, serial = null) {
    const prev = this.nodes.get(dev) || { dev };
    this.nodes.set(dev, {
      ...prev,
      dev,
      name: name || prev.name || null,
      serial: serial || prev.serial || null,
      lastSeen: Date.now(),
    });
  }

  _linkKey(a, b) {
    return [a, b].sort().join('~');
  }

  _countLinks(serial) {
    let n = 0;
    for (const l of this.links.values()) if (l.a === serial || l.b === serial) n++;
    return n;
  }

  _pushTimeline(ev) {
    this.timeline.unshift({
      serial: ev.serial,
      ts: ev.ts,
      kind: ev.kind,
      seq: ev.seq ?? null,
      to: ev.to ?? null,
      from: ev.from ?? null,
      via: ev.via ?? null,
      receivedAt: ev.receivedAt,
    });
    if (this.timeline.length > TIMELINE_MAX) this.timeline.length = TIMELINE_MAX;
  }

  _pushException(ev) {
    this.exceptions.unshift({
      serial: ev.serial,
      ts: ev.ts,
      receivedAt: ev.receivedAt,
      payload: ev.payload,
      raw: ev.raw,
    });
    if (this.exceptions.length > EXCEPTIONS_MAX) this.exceptions.length = EXCEPTIONS_MAX;
  }

  /** Full snapshot for a newly-connected dashboard client. */
  snapshot() {
    const now = Date.now();
    const liveLinks = [...this.links.values()].filter(
      (l) => now - (l.lastSeen || 0) < LINK_STALE_MS
    );
    return {
      devices: [...this.devices.values()],
      nodes: [...this.nodes.values()],
      links: liveLinks,
      health: [...this.health.values()],
      timeline: this.timeline.slice(0, 100),
      exceptions: this.exceptions.slice(0, 50),
    };
  }
}
