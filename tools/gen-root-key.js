#!/usr/bin/env node
/**
 * gen-root-key.js — OFFLINE root keypair generation (run once).
 *
 * Generates an Ed25519 ROOT keypair. The root PRIVATE key is the master trust
 * anchor and MUST be kept offline — never commit it, never put it on the admin
 * laptop, never in either repo.
 *
 * Output:
 *   - root-private.key   : 32-byte raw Ed25519 seed, base64  (SECRET — move offline)
 *   - root-public.key    : 32-byte raw Ed25519 public, base64 (safe — hardcode in app)
 *
 * Usage:
 *   node tools/gen-root-key.js /secure/offline/dir
 */
import { generateKeyPairSync } from 'node:crypto';
import { writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { rawFromEd25519 } from './keyutil.js';

const outDir = process.argv[2] || '.';
if (!existsSync(outDir)) mkdirSync(outDir, { recursive: true });

const privPath = join(outDir, 'root-private.key');
const pubPath = join(outDir, 'root-public.key');

if (existsSync(privPath)) {
  console.error(`REFUSING to overwrite existing ${privPath}. Delete it manually if intended.`);
  process.exit(1);
}

const { publicKey, privateKey } = generateKeyPairSync('ed25519');
const { rawPublic, rawPrivateSeed } = rawFromEd25519(publicKey, privateKey);

writeFileSync(privPath, rawPrivateSeed.toString('base64') + '\n', { mode: 0o600 });
writeFileSync(pubPath, rawPublic.toString('base64') + '\n');

console.log('ROOT keypair generated.');
console.log(`  private (SECRET): ${privPath}`);
console.log(`  public  (hardcode in app): ${pubPath}`);
console.log('');
console.log('Root public key (base64) — copy into AdminTrust.kt ROOT_PUBLIC_B64:');
console.log('  ' + rawPublic.toString('base64'));
console.log('');
console.log('⚠  MOVE root-private.key TO OFFLINE STORAGE NOW. Do NOT commit it.');
