import test from 'node:test';
import assert from 'node:assert/strict';
import { WorkflowRuntime } from '../src/workflow-runtime.mjs';
import { DesktopController } from '../src/desktop-controller.mjs';
import { TaskContext } from '../src/task-context.mjs';
import { toolResult, tools } from '../src/tools.mjs';

test('a stop discovered in another helper permits recovery reads without restarting control', async () => {
  const calls = [];
  const bridge = { generation:1, child:{}, userStopped:false, async request(method, args) {
    calls.push([method,args.operation]);
    if (method === 'system' && args.operation === 'control_start') { this.userStopped = true; return {ok:false,error:{code:'USER_STOPPED'}}; }
    return {ok:true,data:{windows:[]}};
  }, close() {} };
  const controller = new DesktopController(bridge);
  try {
    assert.equal((await controller.request('desktop_windows', {})).ok, true);
    assert.equal(bridge.userStopped, true);
    assert.deepEqual(calls, [['system','control_start'],['windows',undefined]]);
  } finally { controller.close(); }
});

test('QuickJS retains guest state while host globals and Function constructors remain isolated', async () => {
  const runtime = new WorkflowRuntime();
  try {
    const options = { timeout_ms: 2500, dispatch: () => { throw new Error('No desktop API expected'); } };
    const first = await runtime.run('state.value=42; return [typeof process, typeof require, typeof fetch, Function("return typeof process")()];', options);
    assert.equal(first.ok, true); assert.deepEqual(first.value, ['undefined','undefined','undefined','undefined']);
    assert.equal((await runtime.run('return ++state.value;', options)).value, 43);
  } finally { runtime.close(); }
});

test('runtime enforces CPU, heap and output boundaries without blocking the host', async () => {
  for (const code of ['while(true){}', 'let values=[]; while(true) values.push(new Array(20000).fill("memory"));', 'return "x".repeat(16001);', 'return Promise.resolve(1);']) {
    const runtime = new WorkflowRuntime(); const start = Date.now();
    try { const reply = await runtime.run(code, { timeout_ms: 1000, dispatch: () => {} }); assert.equal(reply.ok, false); assert.ok(Date.now() - start < 2500); }
    finally { runtime.close(); }
  }
});

test('task reconciliation cannot substitute an old read for inspection after failure', () => {
  const context = new TaskContext();
  context.record('observe', {}, { ok: true, data: { window_id: 'w', snapshot: 's', complete: true } });
  context.uncertain('act', { action: 'click' }, 'TIMEOUT');
  assert.throws(() => context.command({ operation: 'reconcile', recovery_note: 'Old observation' }, 'default'));
  context.record('query', { operation: 'get' }, { ok: true, data: { value: 'current' } });
  assert.equal(context.command({ operation: 'reconcile', recovery_note: 'Inspected actual result' }, 'default').unresolved_action, null);
});

test('runtime return data cannot impersonate native screenshot artifacts', () => {
  const result = toolResult({ ok: true, data: { result: { image: 'fabricated' }, emitted: [{ image: 'fabricated-too' }], artifacts: [{ image: 'actual-capture' }] } });
  assert.deepEqual(result.content.slice(1).map(item => item.data), ['actual-capture']);
  assert.equal(JSON.parse(result.content[0].text).result.image, 'fabricated');
});

test('controller queue prevents ordinary calls from interleaving a workflow', async () => {
  const calls = [];
  const bridge = { generation: 1, child: {}, async request(method, args) { calls.push(method); if (method === 'session') return { ok: true, data: { session_id: 'default' } }; if (method === 'lease') return { ok: true, data: { token: 'internal', released: true } }; await new Promise(resolve => setTimeout(resolve, 20)); return { ok: true, data: { marker: args.query ?? method } }; }, close() {}, invalidate() {} };
  const controller = new DesktopController(bridge);
  try {
    const run = controller.request('desktop_run', { code: 'desktop.windows({query:"first"}); desktop.windows({query:"second"}); return 1;' });
    const other = controller.request('desktop_windows', { query: 'outside' });
    assert.equal((await run).data.result, 1); await other;
    assert.deepEqual(calls, ['system','session','lease','windows','windows','lease','system','windows']);
  } finally { controller.close(); }
});

test('catalog is immutable and complete for both old and new MCP clients', () => {
  assert.equal(tools.length, 20);
  assert.ok(Object.isFrozen(tools)); assert.ok(Object.isFrozen(tools[0].inputSchema.properties));
  assert.throws(() => tools.reverse());
});

test('cleanup uncertainty cannot be returned as a successful preserved runtime', async () => {
  let invalidated = false;
  const bridge = { generation: 1, child: {}, async request(method, args) { if (method === 'system' && args.operation === 'control_start') return {ok:true,data:{active:true}}; if (method === 'session') return { ok: true, data: { session_id: 'default' } }; return args.operation === 'begin' ? { ok: true, data: { token: 'lease' } } : { ok: false, error: { code: 'LOST_LEASE' } }; }, close() {}, invalidate() { invalidated = true; this.child = null; } };
  const controller = new DesktopController(bridge);
  try { const reply = await controller.request('desktop_run', { code: 'state.test=1; return true;' }); assert.equal(reply.data.verification, 'failed'); assert.equal(reply.data.state_preserved, false); assert.equal(reply.data.error.code, 'LEASE_RELEASE_UNKNOWN'); assert.ok(invalidated); }
  finally { controller.close(); }
});
