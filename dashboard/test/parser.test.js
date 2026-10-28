import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseLine } from '../src/server/parser.js';

const S = 'SERIAL123';

test('parses ENDPOINT-UP', () => {
  const ev = parseLine(S, '10-28 21:00:00.123 D/MeshFlow: ENDPOINT-UP ep=ble-1-xxxx dev=46b3a5 known=false');
  assert.equal(ev.kind, 'endpoint-up');
  assert.equal(ev.ep, 'ble-1-xxxx');
  assert.equal(ev.dev, '46b3a5');
  assert.equal(ev.known, false);
});

test('parses SEND with via=On P2P', () => {
  const ev = parseLine(S, '10-28 21:00:01.000 D/MeshFlow: SEND enc seq=7 to=46b3a5 via=On P2P');
  assert.equal(ev.kind, 'send');
  assert.equal(ev.seq, 7);
  assert.equal(ev.to, '46b3a5');
  assert.equal(ev.via, 'On P2P');
});

test('parses SEND with via=On Mesh', () => {
  const ev = parseLine(S, '10-28 21:00:01.000 D/MeshFlow: SEND enc seq=9 to=abc via=On Mesh');
  assert.equal(ev.via, 'On Mesh');
});

test('parses RECV and DELIVERED', () => {
  const r = parseLine(S, '10-28 21:00:02.000 D/MeshFlow: RECV enc seq=7 from=46b3a5');
  assert.equal(r.kind, 'recv');
  assert.equal(r.from, '46b3a5');
  const d = parseLine(S, '10-28 21:00:03.000 D/MeshFlow: DELIVERED seq=7 to=46b3a5 (ACK received)');
  assert.equal(d.kind, 'delivered');
  assert.equal(d.seq, 7);
});

test('parses DEDUP-DROP and RELAY', () => {
  assert.equal(parseLine(S, 'D/MeshFlow: DEDUP-DROP enc seq=3').kind, 'dedup-drop');
  assert.equal(parseLine(S, 'D/MeshFlow: RELAY enc seq=3').kind, 'relay');
});

test('parses BleTransport status code', () => {
  const ev = parseLine(S, '10-28 21:00:04.000 D/BleTransport: CLIENT-DISC status=62');
  assert.equal(ev.kind, 'client-disc');
  assert.equal(ev.status, 62);
});

test('parses MeshManager Bluetooth state', () => {
  assert.equal(parseLine(S, 'D/MeshManager: Bluetooth ON').kind, 'bt-on');
  assert.equal(parseLine(S, 'D/MeshManager: Bluetooth OFF').kind, 'bt-off');
});

test('parses MeshAdmin EXCEPTION with json payload', () => {
  const ev = parseLine(S, 'D/MeshAdmin: EXCEPTION {"type":"NPE","message":"boom","thread":"main"}');
  assert.equal(ev.kind, 'exception');
  assert.equal(ev.payload.type, 'NPE');
  assert.equal(ev.payload.message, 'boom');
});

test('parses MeshAdmin PONG', () => {
  const ev = parseLine(S, 'D/MeshAdmin: PONG {"dev":"46b3a5","name":"Alice"}');
  assert.equal(ev.kind, 'pong');
  assert.equal(ev.payload.dev, '46b3a5');
});

test('unrecognized line becomes raw, not dropped', () => {
  const ev = parseLine(S, 'some random line');
  assert.equal(ev.kind, 'raw');
});

test('empty line returns null', () => {
  assert.equal(parseLine(S, ''), null);
});
