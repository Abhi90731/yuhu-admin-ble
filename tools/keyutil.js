/**
 * keyutil.js — convert Node's Ed25519 KeyObjects to/from the RAW 32-byte
 * format used by BouncyCastle (Android) and the C# side, and sign/verify
 * using raw keys. Keeps the cross-platform key representation identical.
 */
import { createPublicKey, createPrivateKey, sign as nodeSign, verify as nodeVerify } from 'node:crypto';

// DER prefixes for Ed25519 (RFC 8410).
const PUB_DER_PREFIX = Buffer.from('302a300506032b6570032100', 'hex');   // 12 bytes, then 32-byte key
const PRIV_DER_PREFIX = Buffer.from('302e020100300506032b657004220420', 'hex'); // then 32-byte seed

/** Extract raw 32-byte public + 32-byte private seed from Node KeyObjects. */
export function rawFromEd25519(publicKey, privateKey) {
  const pubDer = publicKey.export({ type: 'spki', format: 'der' });
  const rawPublic = pubDer.subarray(pubDer.length - 32);

  const privDer = privateKey.export({ type: 'pkcs8', format: 'der' });
  const rawPrivateSeed = privDer.subarray(privDer.length - 32);

  return { rawPublic: Buffer.from(rawPublic), rawPrivateSeed: Buffer.from(rawPrivateSeed) };
}

/** Rebuild a Node private KeyObject from a raw 32-byte seed. */
export function privFromRawSeed(rawSeedB64) {
  const seed = Buffer.from(rawSeedB64, 'base64');
  if (seed.length !== 32) throw new Error(`root private seed must be 32 bytes, got ${seed.length}`);
  const der = Buffer.concat([PRIV_DER_PREFIX, seed]);
  return createPrivateKey({ key: der, format: 'der', type: 'pkcs8' });
}

/** Rebuild a Node public KeyObject from a raw 32-byte key. */
export function pubFromRaw(rawB64) {
  const raw = Buffer.from(rawB64, 'base64');
  if (raw.length !== 32) throw new Error(`public key must be 32 bytes, got ${raw.length}`);
  const der = Buffer.concat([PUB_DER_PREFIX, raw]);
  return createPublicKey({ key: der, format: 'der', type: 'spki' });
}

/** Ed25519 sign raw bytes with a raw private seed. Returns 64-byte signature. */
export function signRaw(rawSeedB64, message) {
  const key = privFromRawSeed(rawSeedB64);
  return nodeSign(null, message, key);
}

/** Ed25519 verify. */
export function verifyRaw(rawPubB64, message, signature) {
  const key = pubFromRaw(rawPubB64);
  return nodeVerify(null, message, key, signature);
}
