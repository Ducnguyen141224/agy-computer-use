import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { NativeBridge } from '../src/native-bridge.mjs';

const helper = fileURLToPath(new URL('./helper-fixture.mjs', import.meta.url));
const makeBridge = () => new NativeBridge({ command: process.execPath, args: [helper], timeoutMs: 500 });

test('Escape event stops delivery, rejects queued input across helper restart, and needs explicit resume', async () => {
  const bridge = makeBridge();
  try {
    let stopped = false; bridge.once('controlStop', () => { stopped = true; });
    const stopping = bridge.request('escape', {});
    const queued = bridge.request('act', { action: 'click', ref: '@old' });
    await assert.rejects(stopping, { code: 'USER_STOPPED' });
    await assert.rejects(queued, error => error.code === 'USER_STOPPED' && error.details.delivery === 'not_sent');
    assert.equal(stopped, true);
    assert.equal((await bridge.request('system', { operation: 'control_status' })).data.paused, true);
    await assert.rejects(bridge.request('lease', { operation: 'begin' }), { code: 'USER_STOPPED' });
    await bridge.request('system', { operation: 'control_resume' });
    assert.equal((await bridge.request('act', {})).ok, true);
  } finally { bridge.close(); }
});

test('serializes concurrent requests so desktop observations and actions do not interleave', async () => {
  const bridge = makeBridge();
  try {
    const result = await Promise.all([bridge.request('delay', {}), bridge.request('second', {})]);
    assert.deepEqual(result.map(reply => reply.data.method), ['delay', 'second']);
  } finally { bridge.close(); }
});

test('timeout kills the stuck helper, invalidates its state, and allows a fresh request without retrying the old action', async () => {
  const bridge = makeBridge();
  try {
    await assert.rejects(bridge.request('hang', {}), { code: 'HELPER_TIMEOUT' });
    assert.equal((await bridge.request('fresh', {})).data.method, 'fresh');
  } finally { bridge.close(); }
});

test('cancellation interrupts an in-flight request and restarts cleanly', async () => {
  const bridge = makeBridge();
  try {
    const controller = new AbortController();
    const request = bridge.request('hang', {}, { signal: controller.signal });
    setTimeout(() => controller.abort(), 60);
    await assert.rejects(request, { code: 'CANCELLED' });
    assert.equal((await bridge.request('fresh', {})).ok, true);
  } finally { bridge.close(); }
});

test('crashed helper does not strand the queue', async () => {
  const bridge = makeBridge();
  try {
    await assert.rejects(bridge.request('crash', {}), { code: 'HELPER_EXITED' });
    assert.equal((await bridge.request('fresh', {})).ok, true);
  } finally { bridge.close(); }
});

test('system/batch timeouts are conservatively marked as unknown delivery', async () => {
  const bridge = makeBridge();
  try {
    await assert.rejects(bridge.request('system', { hang: true }), error => error.code === 'HELPER_TIMEOUT' && error.details.delivery === 'unknown');
    await assert.rejects(bridge.request('batch', { hang: true }), error => error.code === 'HELPER_TIMEOUT' && error.details.delivery === 'unknown');
  } finally { bridge.close(); }
});
