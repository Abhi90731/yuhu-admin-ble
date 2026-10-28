#!/usr/bin/env node
/**
 * gen-admin-key.js — generate an admin laptop's Ed25519 keypair (test/dev helper).
 * In production the admin private key is created ON the laptop and stored in the
 * Windows keystore (DPAPI); this helper is for the mac-side test chain.
 */
import { generateKeyPairSync } from 'node:crypto';
import { writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { rawFromEd25519 } from './keyutil.js';

const outDir = process.argv[2] || '.';
if (!existsSync(outDir)) mkdirSync(outDir, { recursive: true });

const { publicKey, privateKey } = generateKeyPairSync('ed25519');
const { rawPublic, rawPrivateSeed } = rawFromEd25519(publicKey, privateKey);

writeFileSync(join(outDir, 'admin-private.key'), rawPrivateSeed.toString('base64') + '\n', { mode: 0o600 });
writeFileSync(join(outDir, 'admin-public.key'), rawPublic.toString('base64') + '\n');
console.log('admin public:', rawPublic.toString('base64'));
