#!/usr/bin/env node
/**
 * mint-admin-cert.js — OFFLINE: mint a root-signed admin certificate.
 *
 * Requires the root PRIVATE seed (kept offline). Given an admin PUBLIC key,
 * produces an admin cert JSON the laptop presents to phones during ADMIN_HELLO.
 *
 * Usage:
 *   node tools/mint-admin-cert.js \
 *     --rootPriv /offline/root-private.key \
 *     --adminPub <base64 admin public key> \
 *     --label admin-laptop-1 \
 *     --days 365 \
 *     --out admin-cert.json
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { randomUUID, createPublicKey } from 'node:crypto';
import { signCert, verifyCert } from './cert.js';
import { privFromRawSeed, rawFromEd25519 } from './keyutil.js';

function arg(name, def) {
  const i = process.argv.indexOf(`--${name}`);
  return i !== -1 ? process.argv[i + 1] : def;
}

const rootPrivPath = arg('rootPriv');
const adminPub = arg('adminPub');
const label = arg('label', 'admin-laptop-1');
const days = Number(arg('days', '365'));
const out = arg('out', 'admin-cert.json');

if (!rootPrivPath || !adminPub) {
  console.error('Usage: --rootPriv <file> --adminPub <b64> [--label L] [--days N] [--out file]');
  process.exit(1);
}

const rootPrivSeedB64 = readFileSync(rootPrivPath, 'utf8').trim();
const now = Date.now();

const cert = {
  v: 1,
  adminPub,
  label,
  notBefore: now,
  notAfter: now + days * 24 * 60 * 60 * 1000,
  serial: randomUUID(),
};

const signed = signCert(cert, rootPrivSeedB64);

// Self-verify against the root public derived from the private seed.
const privKey = privFromRawSeed(rootPrivSeedB64);
const pubKey = createPublicKey(privKey);
const { rawPublic } = rawFromEd25519(pubKey, privKey);
const ok = verifyCert(signed, rawPublic.toString('base64'));
if (!ok) {
  console.error('Self-verification FAILED — not writing cert.');
  process.exit(1);
}
writeFileSync(out, JSON.stringify(signed, null, 2) + '\n');
console.log(`Admin cert written to ${out} (serial ${signed.serial}, valid ${days}d).`);
