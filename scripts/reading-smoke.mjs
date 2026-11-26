import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { NativeBridge } from '../src/native-bridge.mjs';
import { DesktopController } from '../src/desktop-controller.mjs';

// Only the nonce-named fixture is read/scrolled. Stop signals cannot affect live plugin helpers.
process.env.AGY_DESKTOP_TEST_SCOPE = randomUUID().replaceAll('-', '');
const root = fileURLToPath(new URL('../', import.meta.url));
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, stdio: 'pipe', windowsHide: true });
const title = `AGY reader ${randomUUID()}`;
const fixture = spawn(fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url)), [title, '--reader'], { stdio: 'ignore', windowsHide: true });
const bridge = new NativeBridge(); const desktop = new DesktopController(bridge);
let checks = 0;
const check = (value, message) => { assert.ok(value, message); checks++; process.stdout.write(`PASS ${message}\n`); };
const call = async (name, args) => { const reply = await desktop.request(name, args); assert.equal(reply.ok, true, JSON.stringify(reply.error)); return reply.data; };
try {
  const started = Date.now();
  const window_id = (await call('desktop_query', { operation: 'window_wait', query: title, timeout_ms: 5000 })).windows.find(item => item.pid === fixture.pid).id;
  const scope = await call('desktop_system', { operation: 'control_status' });
  check(scope.armed && scope.task_active && !scope.active && !scope.rendered && scope.idle_hide_ms === 120, 'read-only desktop work arms Escape without showing overlays');
  await new Promise(resolve => setTimeout(resolve, 2200));
  check(!(await call('desktop_system', { operation: 'control_status' })).rendered, 'border stays hidden across model idle time');
  const matches = await call('desktop_find', { window_id, name: 'Reader log', exact: true, depth: 5 });
  assert.equal(matches.items.length, 1);
  const root_ref = matches.items[0].ref;
  const snapshot = await call('desktop_read', { operation: 'snapshot', root_ref });
  check(snapshot.source === 'uia_text' && snapshot.image_count === 0 && snapshot.text.includes('Dòng 000'), 'snapshot reads actual Unicode UIA text without a screenshot');
  const runtime = await call('desktop_run', { code: `return desktop.read({operation:'snapshot',root_ref:${JSON.stringify(root_ref)}}).text.includes('Dòng 000');` });
  check(runtime.execution_status === 'completed' && runtime.result === true, 'QuickJS exposes the direct reader with guarded native refs');
  const batch = await call('desktop_batch', { commands: [{ tool: 'desktop_read', arguments: { operation: 'snapshot', root_ref } }] });
  check(batch.results[0].ok && batch.results[0].data.source === 'uia_text', 'prevalidated native batches support direct text reads');
  let part = await call('desktop_read', { operation: 'collect', root_ref, max_pages: 1, max_chars: 100, budget_ms: 3000, pause_ms: 50 });
  check(!part.complete && part.reader_id && part.stop_reason === 'character_limit', 'a character-limited chunk is successful and returns a continuation');
  let full = part.text, chunks = 1, pages = part.pages;
  while (!part.complete && chunks < 100) {
    assert.ok(part.reader_id, JSON.stringify({reason: part.stop_reason, chunks}));
    part = await call('desktop_read', { operation: 'collect', reader_id: part.reader_id, max_pages: 8, max_chars: 900, budget_ms: 5000, pause_ms: 50 });
    assert.notEqual(part.stop_reason, 'scroll_stalled');
    full += part.text; pages += part.pages; chunks++;
  }
  check(part.complete && !part.reader_id && chunks > 1, 'repeated chunks continue to the actual provider scroll boundary');
  const expected = Array.from({ length: 120 }, (_, i) => [40,41].includes(i) ? 'Dòng lặp hợp lệ' : `Dòng ${String(i).padStart(3, '0')} — nội dung thử nghiệm 🙂`);
  assert.deepEqual(full.trimEnd().split('\n'), expected);
  check(true, 'scroll collection preserves all 120 rendered lines in order without gaps or duplicate overlap');
  check(pages < 30 && full.split('Dòng lặp hợp lệ').length === 3, 'viewport-relative scrolling bounds page reads while retaining legitimate repeated lines');
  check(!full.includes('reader-secret-sentinel'), 'password content is excluded even at the final viewport');
  const up = await call('desktop_read', { operation:'collect', root_ref:part.root_ref, direction:'up', max_pages:30, max_chars:10000, budget_ms:8000, pause_ms:50 });
  let upPart=up, upText=up.text, upChunks=1;
  while (!upPart.complete && upPart.reader_id && upChunks++ < 10) {
    upPart=await call('desktop_read', {operation:'collect',reader_id:upPart.reader_id,max_pages:30,max_chars:10000,budget_ms:8000,pause_ms:50});
    assert.notEqual(upPart.stop_reason,'scroll_stalled'); upText+=upPart.text;
  }
  assert.deepEqual(upText.trimEnd().split('\n').sort(), [...expected].sort());
  check(upPart.complete, 'reverse collection also reads every line exactly once, including legitimate duplicates');
  const ended = await call('desktop_system', { operation: 'control_end' });
  check(!ended.task_active && !ended.active && !ended.rendered, 'explicit task completion immediately hides the persistent presentation');
  const stale = await call('desktop_read', { operation: 'collect', root_ref: upPart.root_ref, max_pages: 1, max_chars:100 });
  assert.ok(stale.reader_id, 'Reset is tested with a real active reader continuation.');
  await call('desktop_session', { operation: 'reset' });
  const lost = await desktop.request('desktop_read', { operation: 'collect', reader_id: stale.reader_id });
  check(!lost.ok && lost.error.code === 'STALE_READER', 'desktop session reset discards in-memory reader continuations');
  const stopped = await desktop.request('desktop_system', { operation: 'control_stop' });
  assert.ok(stopped.ok || stopped.error?.code === 'USER_STOPPED');
  await new Promise(resolve => setTimeout(resolve, 150));
  const paused = await call('desktop_system', { operation: 'control_status' });
  check(paused.paused && !paused.active && !paused.task_active, 'stop cancels the persistent scope and preserves the input latch after helper reset');
  const recovery = await call('desktop_windows', { query: title });
  const afterRead = await call('desktop_system', {operation:'control_status'});
  check(recovery.windows.some(item => item.pid === fixture.pid) && afterRead.paused && !afterRead.active && !afterRead.rendered, 'read-only inspection stays available after stop without restarting control');
  await call('desktop_system', {operation:'control_end'});
  check((await call('desktop_system', {operation:'control_status'})).paused, 'ending the task never clears a user stop latch');
  process.stdout.write(`Reading smoke passed ${checks} checks; chunks=${chunks}, UIA pages=${pages}, images=0, elapsed_ms=${Date.now()-started}.\n`);
} finally { desktop.close(); fixture.kill(); }
