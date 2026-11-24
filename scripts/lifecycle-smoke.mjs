import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const root = fileURLToPath(new URL('../', import.meta.url));
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, stdio: 'pipe', windowsHide: true });
const title = `AGY lifecycle ${Date.now()}`;
const client = new Client({ name: 'lifecycle-smoke', version: '0.2.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
let fixture, checks = 0;
const check = (value, description) => { assert.ok(value, description); checks++; process.stdout.write(`PASS ${description}\n`); };
try {
  await client.connect(transport);
  const call = async (name, args) => {
    const reply = await client.callTool({ name, arguments: args });
    const data = JSON.parse(reply.content[0].text); assert.equal(reply.isError, false, JSON.stringify(data)); return data;
  };
  const query = args => call('desktop_query', args);
  const started = await query({ operation: 'watch', event: 'app_launched', app: 'DesktopFixture.exe' });
  fixture = spawn(fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url)), [title], { stdio: 'ignore' });
  check((await query({ operation: 'wait_event', token: started.token, timeout_ms: 5000 })).observed.pid === fixture.pid, 'process watch identifies the actual newly launched fixture PID');
  const window_id = (await query({ operation: 'window_wait', query: title, timeout_ms: 5000 })).windows.find(item => item.pid === fixture.pid).id;
  const find = async (name, role) => { const result = await call('desktop_find', { window_id, name, ...(role ? { role } : {}) }); assert.equal(result.items.length, 1); return result.items[0].ref; };
  const message = await find('Message', 'edit');
  check((await query({ operation: 'is', ref: message, property: 'actionable', action: 'set_value', equals: true })).matches, 'actionable predicate checks the actual editable provider for the requested action');
  const providerText = (await query({ operation: 'get', ref: message, property: 'text' })).value;
  check(providerText.includes('initial') && (await query({ operation: 'text_wait', window_id, root_ref: message, text: providerText, exact: true, count: 1, timeout_ms: 1000 })).count === 1, 'text_wait proves an exact count inside a complete narrow UIA scope');
  check((await query({ operation: 'text_wait', window_id, root_ref: message, text: 'absent nonce', present: false, timeout_ms: 1000 })).count === 0, 'text_wait proves absence only after a complete read');
  await call('desktop_act', { action: 'focus_window', window_id });
  const focused = await call('desktop_system', { operation: 'list_surfaces', window_id, surface: 'focused' });
  check(focused.surfaces[0].root_ref, 'focused surface returns a guarded root ref');
  const menuWatch = await query({ operation: 'watch', event: 'surface_appeared', window_id, surface: 'menu' });
  const pad = await find('Gesture pad');
  await call('desktop_act', { action: 'right_click', ref: pad });
  check((await query({ operation: 'wait_event', token: menuWatch.token, timeout_ms: 4000 })).observed.kind === 'menu', 'surface watch observes a real menu appearing in the selected app');
  const dismissal = await query({ operation: 'watch', event: 'surface_dismissed', window_id, surface: 'menu' });
  await call('desktop_act', { action: 'press_key', ref: pad, key: 'Escape' });
  check((await query({ operation: 'wait_event', token: dismissal.token, timeout_ms: 4000 })).observed.kind === 'menu', 'surface watch observes the actual menu dismissal');
  const batch = await client.callTool({ name: 'desktop_batch', arguments: { commands: [
    { tool: 'desktop_capture', arguments: { window_id, max_dimension: 200 } },
    { tool: 'desktop_query', arguments: { operation: 'get', ref: message, property: 'value' } },
    { tool: 'desktop_capture', arguments: { window_id, max_dimension: 200 } }
  ] } });
  assert.equal(batch.isError, false, batch.content[0].text);
  const entries = JSON.parse(batch.content[0].text).results;
  check(batch.content.filter(item => item.type === 'image').length === 2 && entries[0].data.image_index === 0 && entries[2].data.image_index === 1 && !batch.content[0].text.includes('iVBOR'), 'batch maps two real PNGs to separate MCP image blocks without duplicate base64 text');
  const stopped = await query({ operation: 'watch', event: 'app_terminated', window_id });
  await call('desktop_system', { operation: 'close_window', window_id });
  check((await query({ operation: 'wait_event', token: stopped.token, timeout_ms: 5000 })).observed.pid === fixture.pid, 'process watch retains the selected terminated generation after its window disappears');
  process.stdout.write(`Lifecycle smoke passed: ${checks} checks. No evidence saved.\n`);
} finally { await client.close().catch(() => {}); fixture?.kill(); }
