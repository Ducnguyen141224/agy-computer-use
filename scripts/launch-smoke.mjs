import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const root = fileURLToPath(new URL('../', import.meta.url));
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, windowsHide: true, stdio: 'pipe' });
const app = fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url));
const client = new Client({ name: 'launch-mcp-smoke', version: '0.2.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
let window_id;
try {
  await client.connect(transport);
  const call = async (name, args) => { const reply = await client.callTool({ name, arguments: args }); const data = JSON.parse(reply.content[0].text); assert.equal(reply.isError, false, JSON.stringify(data)); return data; };
  const title = 'AGY launch ' + randomUUID();
  const literal = 'Literal $(not-a-command) `backticks` Tiếng Việt';
  const launched = await call('desktop_system', { operation: 'launch', app, args: [title, '--launch-metadata'], env: { AGY_FIXTURE_VALUE: literal }, cwd: resolve(root), activate: true });
  assert.equal(launched.verification, 'passed'); assert.equal(launched.window.foreground, true);
  window_id = launched.window.id;
  for (const [name, expected] of [['Launch environment', literal], ['Launch directory', resolve(root)]]) {
    const found = await call('desktop_find', { window_id, name, role: 'edit' }); assert.equal(found.items.length, 1);
    assert.equal((await call('desktop_query', { operation: 'get', ref: found.items[0].ref, property: 'value' })).value, expected);
  }
  const attached = await call('desktop_system', { operation: 'launch', app, attach: true });
  assert.equal(attached.attached, true); assert.equal(attached.pid, launched.pid);
  process.stdout.write('PASS launched GUI receives literal Unicode environment and cwd, its real window is activated, stdout/stderr stay out of MCP, and attach returns the existing process.\n');
  await call('desktop_system', { operation: 'close_window', window_id }); window_id = undefined;
  const forcedLaunch = await call('desktop_system', { operation: 'launch', app, args: [title + ' force close', '--launch-metadata'], activate: true });
  window_id = forcedLaunch.window.id;
  const forced = await call('desktop_system', { operation: 'close_app', window_id, force: true });
  assert.equal(forced.verification, 'passed'); assert.equal(forced.pid, forcedLaunch.pid);
  await call('desktop_query', { operation: 'window_wait', query: title + ' force close', present: false, timeout_ms: 2000 }); window_id = undefined;
  process.stdout.write('PASS explicit force close terminates only the selected disposable process generation and verifies its exit.\n');
} finally {
  if (window_id) await client.callTool({ name: 'desktop_system', arguments: { operation: 'close_window', window_id } }).catch(() => {});
  await client.close().catch(() => {});
}
