/**
 * cert.js — canonical admin-certificate serialization + sign/verify.
 *
 * The canonical form is the EXACT byte sequence that gets signed by the root
 * and later re-created + verified on the phone. It MUST be deterministic and
 * reproduced byte-for-byte by the Kotlin side (AdminTrust.kt). Rules:
 *   - fields in a FIXED order (not alphabetical, not JSON object order)
 *   - joined by '\n', UTF-8
 *   - rootSig is NOT part of the signed bytes (it's the signature OF them)
 */
import { signRaw, verifyRaw } from './keyutil.js';

/**
 * Canonical signable string for a cert (everything except rootSig).
 * FIXED ORDER — Kotlin must match exactly.
 */
export function canonicalCertBytes(cert) {
  const fields = [
    `v=${cert.v}`,
    `adminPub=${cert.adminPub}`,
    `label=${cert.label}`,
    `notBefore=${cert.notBefore}`,
    `notAfter=${cert.notAfter}`,
    `serial=${cert.serial}`,
  ];
  return Buffer.from(fields.join('\n'), 'utf8');
}

/** Sign a cert (adds rootSig) with the root private seed. */
export function signCert(cert, rootPrivSeedB64) {
  const sig = signRaw(rootPrivSeedB64, canonicalCertBytes(cert));
  return { ...cert, rootSig: sig.toString('base64') };
}

/** Verify a cert's rootSig against the root public key. */
export function verifyCert(cert, rootPubB64) {
  if (!cert.rootSig) return false;
  const sig = Buffer.from(cert.rootSig, 'base64');
  return verifyRaw(rootPubB64, canonicalCertBytes(cert), sig);
}
