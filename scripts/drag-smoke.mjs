import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const root = fileURLToPath(new URL('../', import.meta.url));
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, windowsHide: true, stdio: 'pipe' });
const executable = fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url));
const nonce = randomUUID();
const sourceTitle = `AGY drag source ${nonce}`;
const targetTitle = `AGY drag target ${nonce}`;
const payload = `Disposable drag ${nonce}`;
const source = spawn(executable, [sourceTitle, '--drag-source', payload], { stdio: 'ignore' });
const destination = spawn(executable, [targetTitle, '--drag-target', payload], { stdio: 'ignore' });
const client = new Client({ name: 'cross-process-drag-smoke', version: '0.2.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
try {
  await client.connect(transport);
  const call = async (name, args) => {
    const reply = await client.callTool({ name, arguments: args });
    const data = JSON.parse(reply.content[0].text); assert.equal(reply.isError, false, JSON.stringify(data)); return data;
  };
  const window = async (title, pid) => (await call('desktop_query', { operation: 'window_wait', query: title, timeout_ms: 5000 })).windows.find(item => item.pid === pid).id;
  const fromWindow = await window(sourceTitle, source.pid);
  const toWindow = await window(targetTitle, destination.pid);
  const find = async (window_id, name) => {
    const result = await call('desktop_find', { window_id, name }); assert.equal(result.items.length, 1); return result.items[0].ref;
  };
  const from = await find(fromWindow, 'Drag token');
  const to = await find(toWindow, 'Drop target');
  const status = await find(toWindow, 'Drop status');
  const counter = await find(toWindow, 'Drop count');
  // Establish two visible fixture windows before preparing the gesture.
  await call('desktop_act', { action: 'focus_window', window_id: toWindow });
  await call('desktop_act', { action: 'focus_window', window_id: fromWindow });
  const result = await call('desktop_act', { action: 'drag', ref: from, to_ref: to, duration_ms: 650, drop_delay_ms: 600, expect: { ref: status, property: 'value', equals: payload }, timeout_ms: 3000 });
  assert.equal(result.verification, 'passed');
  assert.equal((await call('desktop_query', { operation: 'get', ref: status, property: 'value' })).value, payload);
  const point = async (window_id, reference) => {
    const bounds = (await call('desktop_query', { operation: 'get', ref: reference, property: 'bounds' })).value;
    const cap = await call('desktop_capture', { window_id, region: bounds, max_dimension: 400 });
    return { capture_id: cap.capture_id, x: Math.floor(cap.width / 2), y: Math.floor(cap.height / 2) };
  };
  const expectCount = count => ({ ref: counter, property: 'value', equals: String(count) });
  await call('desktop_act', { action: 'focus_window', window_id: fromWindow });
  let p = await point(toWindow, to);
  await call('desktop_act', { action: 'drag', ref: from, to_window_id: toWindow, to_capture_id: p.capture_id, to_x: p.x, to_y: p.y, duration_ms: 650, expect: expectCount(2) });
  await call('desktop_act', { action: 'focus_window', window_id: fromWindow });
  p = await point(fromWindow, from);
  await call('desktop_act', { action: 'drag_point', window_id: fromWindow, ...p, to_ref: to, duration_ms: 650, expect: expectCount(3) });
  await call('desktop_act', { action: 'focus_window', window_id: fromWindow });
  p = await point(fromWindow, from);
  const q = await point(toWindow, to);
  await call('desktop_act', { action: 'drag_point', window_id: fromWindow, ...p, to_window_id: toWindow, to_capture_id: q.capture_id, to_x: q.x, to_y: q.y, duration_ms: 650, expect: expectCount(4) });
  assert.equal((await call('desktop_query', { operation: 'get', ref: status, property: 'value' })).value, payload);
  process.stdout.write('PASS 4 real cross-process OLE transfers: ref/ref, ref/point, point/ref and point/point; each receives the payload and increments the independent drop counter.\n');
} finally { await client.close().catch(() => {}); source.kill(); destination.kill(); }
