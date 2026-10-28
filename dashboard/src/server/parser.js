/**
 * parser.js — turn raw logcat lines into structured mesh events.
 *
 * The log string formats are taken from the kickoff doc's authoritative set.
 * We do NOT invent fields. Unrecognized lines are emitted as {kind:"raw"} so
 * nothing is silently lost (useful while the log vocabulary evolves).
 *
 * Privacy: these logs are metadata-only. There is no message CONTENT in them;
 * we never attempt to reconstruct content.
 */

// logcat "-v time" prefix: "MM-DD HH:MM:SS.mmm  PID  TID LEVEL TAG: message"
const TIME_PREFIX =
  /^(\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+(\w)\/?([\w.]+)?\s*(?:\(\s*\d+\))?:?\s*/;

// Fallback tag matcher: "... TAG: rest"
const TAG_RE = /\b(MeshFlow|MeshManager|BleTransport|RelayService|MeshAdmin)\b:\s*(.*)$/;

/** Extract "key=value" tokens into an object. */
function kv(s) {
  const out = {};
  const re = /(\w+)=([^\s]+)/g;
  let m;
  while ((m = re.exec(s))) out[m[1]] = m[2];
  return out;
}

/**
 * Parse a single logcat line into a structured event.
 * @param {string} serial the device serial this line came from
 * @param {string} line raw logcat line
 * @returns {object|null}
 */
export function parseLine(serial, line) {
  if (!line || !line.trim()) return null;

  let ts = null;
  const tmatch = line.match(TIME_PREFIX);
  if (tmatch) ts = tmatch[1];

  const tagMatch = line.match(TAG_RE);
  if (!tagMatch) {
    return { serial, ts, kind: 'raw', tag: null, text: line.trim(), receivedAt: Date.now() };
  }

  const tag = tagMatch[1];
  const body = tagMatch[2].trim();
  const base = { serial, ts, tag, receivedAt: Date.now(), text: body };

  // ---- MeshFlow events (the primary data source) ----
  if (tag === 'MeshFlow') {
    const [verb] = body.split(/\s+/, 1);
    const fields = kv(body);
    switch (verb) {
      case 'ENDPOINT-UP':
        return { ...base, kind: 'endpoint-up', ep: fields.ep, dev: fields.dev, known: fields.known === 'true' };
      case 'ENDPOINT-DOWN':
        return { ...base, kind: 'endpoint-down', ep: fields.ep, dev: fields.dev };
      case 'SEND':
        return { ...base, kind: 'send', seq: num(fields.seq), to: fields.to, via: viaOf(body) };
      case 'RECV':
        return { ...base, kind: 'recv', seq: num(fields.seq), from: fields.from };
      case 'DELIVERED':
        return { ...base, kind: 'delivered', seq: num(fields.seq), to: fields.to };
      case 'RELAY':
        return { ...base, kind: 'relay', seq: num(fields.seq) };
      case 'DEDUP-DROP':
        return { ...base, kind: 'dedup-drop', seq: num(fields.seq) };
      default:
        return { ...base, kind: 'meshflow-other', verb, fields };
    }
  }

  // ---- BleTransport events (connection health) ----
  if (tag === 'BleTransport') {
    const [verb] = body.split(/\s+/, 1);
    const fields = kv(body);
    const kindMap = {
      'CONNECT-OUT': 'connect-out',
      'SERVER-CONN': 'server-conn',
      'CLIENT-DISC': 'client-disc',
      'ADVERTISE-OK': 'advertise-ok',
    };
    return {
      ...base,
      kind: kindMap[verb] || 'ble-other',
      verb,
      status: num(fields.status),
      fields,
    };
  }

  // ---- MeshManager events (lifecycle) ----
  if (tag === 'MeshManager') {
    let kind = 'mesh-manager-other';
    if (/finalizeContact/.test(body)) kind = 'finalize-contact';
    else if (/PIN set/i.test(body)) kind = 'pin-set';
    else if (/Bluetooth ON/i.test(body)) kind = 'bt-on';
    else if (/Bluetooth OFF/i.test(body)) kind = 'bt-off';
    return { ...base, kind };
  }

  // ---- MeshAdmin events (Part B: device-reported exceptions + admin replies) ----
  if (tag === 'MeshAdmin') {
    // Convention: "EXCEPTION {json}" / "PONG {json}" / "LOG {json}"
    const sp = body.indexOf(' ');
    const verb = sp === -1 ? body : body.slice(0, sp);
    const rest = sp === -1 ? '' : body.slice(sp + 1).trim();
    let payload = null;
    if (rest.startsWith('{')) {
      try { payload = JSON.parse(rest); } catch { payload = null; }
    }
    if (verb === 'EXCEPTION') return { ...base, kind: 'exception', payload, raw: rest };
    if (verb === 'PONG') return { ...base, kind: 'pong', payload };
    return { ...base, kind: 'admin-other', verb, payload, raw: rest };
  }

  if (tag === 'RelayService') {
    return { ...base, kind: 'relay-service' };
  }

  return { ...base, kind: 'raw' };
}

function num(v) {
  if (v == null) return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

/** "via=On P2P" / "via=On Mesh" — the value has a space so kv() can't catch it. */
function viaOf(body) {
  const m = body.match(/via=On\s+(P2P|Mesh)/i);
  return m ? `On ${m[1]}` : null;
}
