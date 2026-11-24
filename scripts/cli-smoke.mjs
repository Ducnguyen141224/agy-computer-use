import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const cli = fileURLToPath(new URL('../src/cli.mjs', import.meta.url));
const pipe = `agy-cli-test-${randomUUID()}`;
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, stdio: 'pipe', windowsHide: true });
const title = `AGY CLI ${Date.now()}`;
const fixture = spawn(fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url)), [title], { windowsHide: false, stdio: 'ignore' });
let checks = 0;
const check = (condition, message) => { assert.ok(condition, message); checks++; process.stdout.write(`PASS ${message}\n`); };
function run(command, args, options = {}) {
  const result = execFileSync(process.execPath, [cli, command, '--pipe', pipe, '--json', JSON.stringify(args)], { windowsHide: true, timeout: 20000, ...options });
  const reply = JSON.parse(result.toString());
  assert.equal(reply.ok, true, JSON.stringify(reply));
  return reply.data;
}
try {
  const status = run('status', {});
  check(status.platform === 'windows', 'CLI starts a current-user-only native memory daemon');
  check(run('status', {}).process_id === status.process_id, 'separate CLI invocations share the same live helper');
  const appeared = run('query', { operation: 'window_wait', query: title, timeout_ms: 5000 });
  const window_id = appeared.windows.find(window => window.pid === fixture.pid).id;
  const result = run('find', { window_id, name: 'Message', role: 'edit' });
  const reference = result.items[0].ref;
  run('set-value', { ref: reference, text: 'CLI persistent ref', expect: { property: 'value', equals: 'CLI persistent ref' } });
  check(run('get', { ref: reference, property: 'value' }).value === 'CLI persistent ref', 'ref survives across separate find/action/query CLI processes');
  const literal = 'literal $(not-a-command) and `backticks`\nTiếng Việt';
  execFileSync(process.execPath, [cli, 'set-value', reference, '--stdin', '--pipe', pipe], { input: literal, windowsHide: true, timeout: 20000 });
  check(run('get', { ref: reference, property: 'value' }).value === literal, 'stdin text remains literal and is not interpreted by a shell');
  const image = execFileSync(process.execPath, [cli, 'screenshot', '--window-id', window_id, '--png', '--pipe', pipe], { windowsHide: true, timeout: 20000 });
  check(image.subarray(1, 4).toString() === 'PNG', 'CLI streams real PNG bytes without saving a file');
  run('session', { operation: 'reset' });
  let oldRefRejected = false;
  try { run('get', { ref: reference, property: 'value' }); } catch { oldRefRejected = true; }
  check(oldRefRejected, 'reset invalidates refs across CLI invocations');
  process.stdout.write(`CLI desktop smoke passed: ${checks} checks. No evidence files saved.\n`);
} finally {
  try { execFileSync(process.execPath, [cli, 'daemon-stop', '--pipe', pipe], { windowsHide: true, timeout: 16000, stdio: 'pipe' }); } catch { }
  fixture.kill();
}
