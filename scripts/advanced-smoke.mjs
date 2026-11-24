import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const root = fileURLToPath(new URL('../', import.meta.url));
const fixturePath = fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url));
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, stdio: 'pipe', windowsHide: true });
const title = `AGY Advanced ${Date.now()}`;
const fixture = spawn(fixturePath, [title], { stdio: 'ignore', windowsHide: false });
const client = new Client({ name: 'agy-advanced-smoke', version: '0.2.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
let clipboardToken;
let checks = 0;
const check = (condition, description) => { assert.ok(condition, description); checks++; process.stdout.write(`PASS ${description}\n`); };
async function call(name, args, expectError = false) {
  const reply = await client.callTool({ name, arguments: args });
  const data = JSON.parse(reply.content.find(block => block.type === 'text').text);
  if (!expectError) assert.equal(reply.isError, false, JSON.stringify(data));
  return { ...data, image: reply.content.find(block => block.type === 'image') };
}
try {
  await client.connect(transport);
  const appeared = await call('desktop_query', { operation: 'window_wait', query: title, timeout_ms: 5000 });
  const window = appeared.windows.find(item => item.title === title && item.pid === fixture.pid);
  assert.ok(window);
  const window_id = window.id;
  const find = async (name, role) => {
    const data = await call('desktop_find', { window_id, name, ...(role ? { role } : {}), budget_ms: 5000 });
    assert.equal(data.items.length, 1, `Expected one fixture control: ${name}`);
    return data.items[0].ref;
  };
  const message = await find('Message', 'edit');
  check((await call('desktop_query', { operation: 'is', ref: message, property: 'process_id', equals: fixture.pid })).matches, 'numeric property checks compare the actual provider process id');
  const pad = await find('Gesture pad');
  const log = await find('Gesture log', 'edit');
  const reset = await find('Reset gestures', 'button');
  const drop = await find('Drop zone');
  const system = args => call('desktop_system', args);
  const query = args => call('desktop_query', args);
  const act = args => call('desktop_act', args);
  await system({ operation: 'focus_window', window_id });
  check((await system({ operation: 'list_displays' })).displays.length > 0, 'lists real display bounds and work areas');
  check((await system({ operation: 'list_apps' })).apps.some(app => app.pid === fixture.pid), 'lists the fixture as a running app');
  const original = (await system({ operation: 'window_info', window_id })).bounds;
  await system({ operation: 'resize', window_id, width: original.width + 30, height: original.height + 20 });
  check((await system({ operation: 'window_info', window_id })).bounds.width === original.width + 30, 'resizes a real window without stealing another app focus');
  await system({ operation: 'move', window_id, x: original.x + 15, y: original.y + 10 });
  check((await system({ operation: 'window_info', window_id })).bounds.x === original.x + 15, 'moves the selected real window');
  await system({ operation: 'minimize', window_id });
  check((await system({ operation: 'window_info', window_id })).minimized, 'minimizes the selected window');
  await system({ operation: 'restore', window_id });
  await system({ operation: 'maximize', window_id });
  check((await system({ operation: 'window_info', window_id })).maximized, 'maximizes the selected window');
  await system({ operation: 'restore', window_id });
  await system({ operation: 'resize', window_id, width: original.width, height: original.height });
  await system({ operation: 'move', window_id, x: original.x, y: original.y });
  await system({ operation: 'focus_window', window_id });

  await act({ action: 'set_value', ref: message, text: 'replace this' });
  await act({ action: 'focus', ref: message, expect: { property: 'focused', equals: true } });
  await act({ action: 'press_key', ref: message, key: 'Ctrl+A' });
  await act({ action: 'type_text', ref: message, text: 'Phím tắt đúng', expect: { property: 'value', equals: 'Phím tắt đúng' } });
  check((await query({ operation: 'get', ref: message, property: 'value' })).value === 'Phím tắt đúng', 'Ctrl+A and Unicode input replace the selection in the exact focused field');
  await act({ action: 'clear', ref: message, expect: { property: 'value', equals: '' } });
  check((await query({ operation: 'is', ref: message, property: 'value', equals: '' })).matches, 'clear and independent is/get property queries work');
  await act({ action: 'focus', ref: message, expect: { property: 'focused', equals: true } });
  await act({ action: 'type_text', ref: message, text: 'Chậm 😀', interval_ms: 25, expect: { property: 'value', equals: 'Chậm 😀' } });
  check((await query({ operation: 'get', ref: message, property: 'value' })).value === 'Chậm 😀', 'paced physical typing preserves Unicode scalar boundaries and verifies exact text');
  const missing = await call('desktop_query', { operation: 'get', ref: '@unknown', property: 'exists' }, true);
  check(missing.error?.code === 'STALE_REF', 'an unknown ref cannot be misreported as proof of absence');

  await act({ action: 'click', ref: reset });
  await act({ action: 'double_click', ref: pad });
  check((await query({ operation: 'wait', ref: log, property: 'value', predicate: 'contains', equals: 'clicks=2', timeout_ms: 1500 })).matches, 'double click reaches the real mouse handler');
  await act({ action: 'click', ref: reset });
  await act({ action: 'triple_click', ref: pad });
  const triple = await query({ operation: 'get', ref: log, property: 'value' });
  check((triple.value.match(/down:Left/g) ?? []).length === 3, 'triple click delivers three down/up pairs');
  await act({ action: 'right_click', ref: pad });
  check((await query({ operation: 'get', ref: log, property: 'value' })).value.includes('down:Right'), 'right click reaches the requested control');
  const surfaces = await query({ operation: 'surface_wait', surface: 'menu', window_id, timeout_ms: 2000 });
  check(surfaces.surfaces.some(surface => surface.kind === 'menu'), 'discovers a live context-menu surface');
  // Escape closes only the fixture context menu; its owning control still has focus.
  await act({ action: 'press_key', ref: pad, key: 'Escape' });
  await act({ action: 'click', ref: reset });
  await act({ action: 'drag', ref: pad, to_ref: drop, duration_ms: 250 });
  check((await query({ operation: 'get', ref: log, property: 'value' })).value.includes('dragging'), 'drag moves with the left button held and releases it');
  await act({ action: 'click', ref: reset });
  await act({ action: 'hover', ref: pad });
  check((await query({ operation: 'wait', ref: log, property: 'value', predicate: 'contains', equals: 'hover', timeout_ms: 2000 })).matches, 'hover produces the real delayed mouse-hover event');

  clipboardToken = (await system({ operation: 'clipboard_save' })).token;
  await system({ operation: 'clipboard_set', text: 'AGY clipboard tiếng Việt' });
  check((await system({ operation: 'clipboard_get' })).text === 'AGY clipboard tiếng Việt', 'clipboard text round trip preserves Unicode');
  const fileRefs = [fileURLToPath(new URL('../README.md', import.meta.url)), fixturePath];
  await system({ operation: 'clipboard_set', file_paths: fileRefs });
  check(JSON.stringify((await system({ operation: 'clipboard_get', format: 'files' })).file_paths) === JSON.stringify(fileRefs), 'file clipboard round trip preserves two existing absolute references without moving or copying them');
  check((await system({ operation: 'clipboard_get', format: 'auto' })).format === 'files', 'automatic clipboard selection prioritizes real file references');
  const refusedPaths = await call('desktop_system', { operation: 'clipboard_set', file_paths: [root + 'missing-file-nonce'] }, true);
  check(refusedPaths.error?.code === 'INVALID_ARGUMENT' && JSON.stringify((await system({ operation: 'clipboard_get', format: 'files' })).file_paths) === JSON.stringify(fileRefs), 'invalid file reference preserves the previous clipboard unchanged');
  await act({ action: 'click', ref: await find('Copy test image', 'button') });
  const clipImage = await system({ operation: 'clipboard_get', image: true });
  check(clipImage.image?.type === 'image' && clipImage.width === 12, 'reads a real clipboard image as an in-memory MCP image block');
  await system({ operation: 'clipboard_clear' });
  const cleared = await system({ operation: 'clipboard_get' });
  check(cleared.text === undefined && !cleared.has_image, 'clears clipboard formats');
  await system({ operation: 'clipboard_restore', token: clipboardToken });
  clipboardToken = undefined;

  const goodBatch = await call('desktop_batch', { commands: [
    { tool: 'desktop_act', arguments: { action: 'set_value', ref: message, text: 'batched', expect: { property: 'value', equals: 'batched' } } },
    { tool: 'desktop_query', arguments: { operation: 'is', ref: message, property: 'value', equals: 'batched' } }
  ] });
  check(goodBatch.results.every(entry => entry.ok), 'batch executes a known action and verification in order');
  const badBatch = await call('desktop_batch', { commands: [
    { tool: 'desktop_act', arguments: { action: 'set_value', ref: message, text: 'must-not-write' } },
    { tool: 'desktop_act', arguments: { action: 'press_key', ref: message, key: 'NotAKey' } }
  ] }, true);
  check(badBatch.error?.code === 'INVALID_ARGUMENT' && (await query({ operation: 'get', ref: message, property: 'value' })).value === 'batched', 'batch rejects invalid later arguments before its first mutation');

  const event = await query({ operation: 'watch', event: 'window_opened', query: title + ' Child' });
  await act({ action: 'click', ref: await find('Open child', 'button') });
  check((await query({ operation: 'wait_event', token: event.token, timeout_ms: 3000 })).verification === 'passed', 'event watch is armed before a real child window opens');
  const child = await query({ operation: 'window_wait', query: title + ' Child', timeout_ms: 3000 });
  const closedWatch = await query({ operation: 'watch', event: 'window_closed', window_id: child.windows[0].id });
  await system({ operation: 'close_window', window_id: child.windows[0].id });
  check((await query({ operation: 'wait_event', token: closedWatch.token, timeout_ms: 3000 })).verification === 'passed', 'closed-window event resolves its captured runtime identity after the provider disappears');
  check((await query({ operation: 'window_wait', query: title + ' Child', present: false, timeout_ms: 3000 })).verification === 'passed', 'polite close and wait for disappearance work');

  const launchTitle = title + ' Launched';
  const launched = await system({ operation: 'launch', app: fixturePath, args: [launchTitle] });
  const opened = await query({ operation: 'window_wait', query: launchTitle, timeout_ms: 5000 });
  check(opened.windows.some(item => item.pid === launched.pid), 'launch passes an argument array without a shell and finds the new app');
  await system({ operation: 'close_app', window_id: opened.windows[0].id });
  await query({ operation: 'window_wait', query: launchTitle, present: false, timeout_ms: 3000 });
  await system({ operation: 'focus_window', window_id });
  await act({ action: 'focus', ref: message });
  const focusWatch = await query({ operation: 'watch', event: 'focus_changed', window_id });
  await act({ action: 'focus', ref: pad, expect: { property: 'focused', equals: true } });
  check((await query({ operation: 'wait_event', token: focusWatch.token, timeout_ms: 3000 })).verification === 'passed', 'focus watch receives an actual control focus change');
  const focused = await query({ operation: 'focused', window_id });
  check(focused.item.name === 'Gesture pad', 'focused query returns the exact control in the selected window');

  const session = await call('desktop_session', { operation: 'start' });
  await call('desktop_session', { operation: 'switch', session_id: 'default' });
  const size = await find('Size', 'combobox');
  await act({ action: 'select', ref: size, option: 'Large' });
  check((await query({ operation: 'get', ref: size, property: 'value' })).value === 'Large', 'selects an exact option from a real WinForms dropdown');
  await call('desktop_session', { operation: 'switch', session_id: session.session_id });
  const foreign = await call('desktop_query', { operation: 'get', ref: message, property: 'value' }, true);
  check(foreign.error?.code === 'STALE_REF', 'a new memory-only session cannot use another session ref');
  await call('desktop_session', { operation: 'switch', session_id: 'default' });
  check((await query({ operation: 'get', ref: message, property: 'value' })).value === 'batched', 'switching back restores that session reference namespace');
  await call('desktop_session', { operation: 'end', session_id: session.session_id });
  process.stdout.write(`Advanced desktop smoke passed: ${checks} checks. No evidence files saved.\n`);
} finally {
  if (clipboardToken) await call('desktop_system', { operation: 'clipboard_restore', token: clipboardToken }).catch(() => process.stderr.write('Clipboard restore failed; inspect clipboard state.\n'));
  await client.close().catch(() => {});
  fixture.kill();
}
