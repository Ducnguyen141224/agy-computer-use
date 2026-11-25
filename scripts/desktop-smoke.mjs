import { desktopTestEnv } from '../tests/mcp-env.mjs';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const root = fileURLToPath(new URL('../', import.meta.url));
if (process.platform !== 'win32') throw new Error('The real-desktop smoke test requires Windows.');
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, stdio: 'pipe', windowsHide: true });
const title = `AGY Computer Use Smoke ${Date.now()}`;
const fixture = spawn(fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url)), [title], { windowsHide: false, stdio: 'ignore' });
const transport = new StdioClientTransport({ env: desktopTestEnv(), command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
const client = new Client({ name: 'agy-desktop-smoke', version: '0.1.0' });
const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
let checks = 0;
const check = (condition, description) => { assert.ok(condition, description); checks++; process.stdout.write(`PASS ${description}\n`); };
async function call(name, args = {}, expectError = false) {
  const result = await client.callTool({ name, arguments: args });
  const data = JSON.parse(result.content.find(block => block.type === 'text').text);
  if (!expectError) assert.equal(result.isError, false, JSON.stringify(data));
  return { result, data };
}
try {
  await client.connect(transport);
  const listed = await client.listTools();
  check(listed.tools.length === 20 && ['desktop_windows','desktop_observe','desktop_find','desktop_act','desktop_system','desktop_query','desktop_batch','desktop_session','desktop_capture','desktop_run','desktop_context','desktop_help','desktop_read'].every(name => listed.tools.some(tool => tool.name === name)), 'MCP preserves the nine native tool groups and adds runtime, task context and on-demand help');
  let window;
  for (let attempt = 0; attempt < 30; attempt++) {
    const { data } = await call('desktop_windows', { query: title });
    window = data.windows.find(entry => entry.title === title && entry.pid === fixture.pid);
    if (window) break;
    await pause(150);
  }
  assert.ok(window, 'Fixture window is visible');
  const window_id = window.id;
  const { data: first } = await call('desktop_observe', { window_id, depth: 8, limit: 80, budget_ms: 5000, interactive_only: false });
  check(first.complete && first.items.some(item => item.name === 'Message'), 'reads the rendered Windows accessibility tree');
  const message = first.items.find(item => item.name === 'Message' && item.role === 'edit');
  const option = first.items.find(item => item.name === 'Enabled option');
  const save = first.items.find(item => item.name === 'Save');
  const status = first.items.find(item => item.name === 'Status');
  const secret = first.items.find(item => item.name === 'Secret');
  check(secret.password && !('value' in secret), 'password value is excluded from observations');
  const { data: duplicates } = await call('desktop_find', { window_id, role: 'button', name: 'Duplicate' });
  check(duplicates.items.length === 2 && duplicates.needs_narrowing && duplicates.items[0].context !== duplicates.items[1].context, 'duplicate labels retain group context and are reported as ambiguous');
  const { data: invalid } = await call('desktop_act', { action: 'set_value', ref: message.ref, text: 'should-not-be-written', expect: { property: 'checked', equals: 'wrong-type' } }, true);
  check(invalid.error?.code === 'INVALID_ARGUMENT', 'malformed postcondition is rejected before changing the UI');
  const { data: afterInvalid } = await call('desktop_find', { window_id, name: 'Message', role: 'edit' });
  check(afterInvalid.items[0].value === 'initial', 'rejected action produced no text mutation');
  const text = 'Xin chào Antigravity';
  const { data: set } = await call('desktop_act', { action: 'set_value', ref: message.ref, text, expect: { property: 'value', equals: text } });
  check(set.verification === 'passed', 'semantic text replacement reads back Unicode text');
  const { data: checked } = await call('desktop_act', { action: 'check', ref: option.ref, expect: { property: 'checked', equals: true } });
  check(checked.verification === 'passed', 'checkbox action verifies the actual checked state');
  const { data: again } = await call('desktop_act', { action: 'check', ref: option.ref, expect: { property: 'checked', equals: true } });
  check(again.delivery === 'already_satisfied', 'repeating check does not toggle it off');
  const { data: saved } = await call('desktop_act', { action: 'click', ref: save.ref, expect: { ref: status.ref, property: 'value', equals: `Saved: ${text}` } });
  check(saved.verification === 'passed', 'button click waits for the asynchronous UI result');
  const { data: failed } = await call('desktop_act', { action: 'check', ref: option.ref, timeout_ms: 0, expect: { property: 'checked', equals: false } }, true);
  check(failed.verification === 'failed' && failed.delivery === 'already_satisfied', 'incorrect postcondition is not reported as success');
  const { data: full } = await call('desktop_observe', { window_id, depth: 8, limit: 80, budget_ms: 5000, interactive_only: false });
  const { data: diff } = await call('desktop_observe', { window_id, depth: 8, limit: 80, budget_ms: 5000, interactive_only: false, since: full.snapshot });
  check(diff.mode === 'diff' && diff.items.length === 0 && diff.unchanged > 0, 'unchanged UI returns a small diff instead of the full tree');
  check(JSON.stringify(diff).length < JSON.stringify(full).length / 2, 'unchanged diff payload is less than half of the full fixture observation');
  await call('desktop_act', { action: 'set_value', ref: message.ref, text: 'diff-change', expect: { property: 'value', equals: 'diff-change' } });
  const { data: changedDiff } = await call('desktop_observe', { window_id, depth: 8, limit: 80, budget_ms: 5000, interactive_only: false, since: diff.snapshot });
  check(changedDiff.mode === 'diff' && changedDiff.items.some(item => item.value === 'diff-change'), 'changed diff includes fresh refs and updated text');
  const baselineMessage = full.items.find(item => item.role === 'edit' && item.name === 'Message');
  check(changedDiff.removed.includes(baselineMessage.ref), 'changed diff removes the old ref so clients do not retain duplicate controls');
  const reorder = first.items.find(item => item.name === 'Reorder');
  await call('desktop_act', { action: 'click', ref: reorder.ref });
  const { data: reordered } = await call('desktop_observe', { window_id, depth: 8, limit: 80, budget_ms: 5000, interactive_only: false, since: changedDiff.snapshot });
  check(reordered.mode === 'full' && reordered.diff_reset?.includes('order changed'), 'structural reordering resets the diff baseline instead of keeping stale control order');
  const { data: passwordAction } = await call('desktop_act', { action: 'set_value', ref: secret.ref, text: 'ignored' }, true);
  check(passwordAction.error?.code === 'PASSWORD_FIELD', 'password input is refused');
  await call('desktop_act', { action: 'focus_window', window_id });
  await call('desktop_act', { action: 'set_value', ref: message.ref, text: 'physical' });
  await call('desktop_act', { action: 'focus', ref: message.ref, expect: { property: 'focused', equals: true } });
  await call('desktop_act', { action: 'press_key', ref: message.ref, key: 'End' });
  const { data: typed } = await call('desktop_act', { action: 'type_text', ref: message.ref, text: '-input', expect: { property: 'value', equals: 'physical-input' } });
  check(typed.verification === 'passed', 'physical text input reaches only the verified focused field');
  const { data: wrongFocus } = await call('desktop_act', { action: 'type_text', ref: status.ref, text: 'must-not-type' }, true);
  check(wrongFocus.error?.code === 'FOCUS_MISMATCH', 'physical typing refuses a different focused target');
  const { data: visualTarget } = await call('desktop_find', { window_id, role: 'button', name: 'Save', include_bounds: true });
  const { result: capture, data: captureData } = await call('desktop_capture', { window_id, max_dimension: 800 });
  check(capture.content.some(block => block.type === 'image' && block.data.length > 0) && !capture.content[0].text.includes('image/png'), 'screenshot is returned in memory as an image block');
  const image = capture.content.find(block => block.type === 'image');
  check(Buffer.from(image.data, 'base64').subarray(1, 4).toString() === 'PNG' && captureData.width <= 800, 'capture contains a real bounded PNG');
  const bounds = visualTarget.items[0].bounds;
  const point = {
    x: Math.floor((bounds.x + bounds.width / 2 - captureData.source_region.x) * captureData.width / captureData.source_region.width),
    y: Math.floor((bounds.y + bounds.height / 2 - captureData.source_region.y) * captureData.height / captureData.source_region.height)
  };
  await call('desktop_act', { action: 'focus_window', window_id });
  const { data: pointClick } = await call('desktop_act', { action: 'click_point', window_id, capture_id: captureData.capture_id, ...point, expect: { ref: status.ref, property: 'value', equals: 'Saved: physical-input' } });
  check(pointClick.verification === 'passed', 'fresh image coordinates map to a real button click with verified effect');
  const { data: reusedCapture } = await call('desktop_act', { action: 'click_point', window_id, capture_id: captureData.capture_id, ...point }, true);
  check(reusedCapture.error?.code === 'STALE_CAPTURE', 'capture token is consumed by one point click');
  const { data: oldCapture } = await call('desktop_act', { action: 'click_point', window_id, capture_id: 'missing', x: 0, y: 0 }, true);
  check(oldCapture.error?.code === 'STALE_CAPTURE', 'point input refuses an unobserved capture');
  const { data: remove } = await call('desktop_find', { window_id, role: 'button', name: 'Remove field' });
  await call('desktop_act', { action: 'click', ref: remove.items[0].ref, expect: { ref: status.ref, property: 'value', equals: 'Removed' } });
  const { data: stale } = await call('desktop_act', { action: 'set_value', ref: message.ref, text: 'must-not-type' }, true);
  check(stale.error?.code === 'STALE_REF', 'removed control cannot be driven with a stale ref');
  process.stdout.write(`Desktop smoke passed: ${checks} checks. No screenshots or evidence files saved.\n`);
} finally {
  await client.close().catch(() => {});
  fixture.kill();
}
