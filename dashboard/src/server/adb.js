/**
 * adb.js — cross-platform adb discovery + process spawning.
 *
 * READ-ONLY w.r.t. devices (Phase 1/2 constraint): this module only runs
 * `adb devices` and `adb logcat`. It MUST NOT install, force-stop, or otherwise
 * mutate phones — other sessions drive the devices and mutating would disrupt
 * their tests. Remote control (Phase 3) is deliberately not implemented here.
 */
import { spawn, execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { homedir, platform } from 'node:os';
import { join } from 'node:path';

/**
 * Resolve the adb executable path. Order of precedence:
 *   1. YUHU_ADB env var (explicit override)
 *   2. OS-specific SDK default location
 *   3. bare "adb" (assume it's on PATH)
 */
export function resolveAdbPath() {
  if (process.env.YUHU_ADB && existsSync(process.env.YUHU_ADB)) {
    return process.env.YUHU_ADB;
  }

  const os = platform();
  const candidates = [];

  if (os === 'darwin') {
    candidates.push(join(homedir(), 'Library/Android/sdk/platform-tools/adb'));
  } else if (os === 'win32') {
    const local = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
    candidates.push(join(local, 'Android', 'Sdk', 'platform-tools', 'adb.exe'));
  } else {
    // linux
    candidates.push(join(homedir(), 'Android/Sdk/platform-tools/adb'));
  }

  for (const c of candidates) {
    if (existsSync(c)) return c;
  }

  // Last resort: rely on PATH.
  return os === 'win32' ? 'adb.exe' : 'adb';
}

const ADB = resolveAdbPath();

/**
 * List attached devices via `adb devices -l`.
 * Returns [{ serial, state, model }]. Only state === "device" are usable.
 */
export function listDevices() {
  return new Promise((resolve) => {
    execFile(ADB, ['devices', '-l'], { timeout: 8000 }, (err, stdout) => {
      if (err) {
        resolve({ ok: false, error: String(err.message || err), devices: [] });
        return;
      }
      const devices = [];
      const lines = stdout.split('\n').slice(1); // drop "List of devices attached"
      for (const raw of lines) {
        const line = raw.trim();
        if (!line) continue;
        const parts = line.split(/\s+/);
        const serial = parts[0];
        const state = parts[1] || 'unknown';
        // parse "model:Pixel_6" style tokens from -l output
        let model = '';
        for (const p of parts.slice(2)) {
          if (p.startsWith('model:')) model = p.slice('model:'.length);
        }
        devices.push({ serial, state, model });
      }
      resolve({ ok: true, devices });
    });
  });
}

/**
 * Spawn a streaming `adb -s <serial> logcat` for the YUHU mesh tags.
 * Returns the ChildProcess; caller wires stdout line parsing + lifecycle.
 *
 * We filter to the authoritative mesh tags only (metadata-only logs) plus a
 * dedicated crash/exception tag used by Part B.
 *
 * @param {string} serial
 * @param {object} opts
 * @param {boolean} opts.dump   if true, dump existing buffer then exit (-d)
 */
export function spawnLogcat(serial, opts = {}) {
  const tagArgs = [
    'MeshFlow:D',
    'MeshManager:D',
    'BleTransport:D',
    'RelayService:D',
    'MeshAdmin:D', // Part B: device-reported exceptions / admin replies
    '*:S', // silence everything else
  ];
  const args = ['-s', serial, 'logcat', '-v', 'time'];
  if (opts.dump) args.push('-d');
  args.push(...tagArgs);
  return spawn(ADB, args, { stdio: ['ignore', 'pipe', 'pipe'] });
}

export const adbPath = ADB;
