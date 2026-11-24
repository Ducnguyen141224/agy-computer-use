import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { userInfo } from 'node:os';
import { fileURLToPath } from 'node:url';

export const helperPath = fileURLToPath(new URL('../native/bin/Release/net10.0-windows/AgyComputerUse.exe', import.meta.url));
export const defaultPipe = `agy-cu-${createHash('sha256').update(userInfo().username + '|' + helperPath).digest('hex').slice(0, 24)}`;
const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function requestOnce(pipe, method, params) {
  return new Promise((resolve, reject) => {
    // .NET validates the named-pipe server owner against the current Windows account.
    const client = spawn(helperPath, ['--pipe-client', pipe], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
    let output = '', settled = false;
    const timer = setTimeout(() => { client.kill(); finish(new Error('Desktop transport timed out; delivery is unknown.')); }, 15500);
    function finish(error, reply) {
      if (settled) return;
      settled = true; clearTimeout(timer);
      if (error) reject(error); else resolve(reply);
    }
    client.stdout.setEncoding('utf8');
    client.stdout.on('data', chunk => {
      output += chunk;
      if (output.length > 48 * 1024 * 1024) { client.kill(); finish(new Error('Desktop response exceeded its size budget.')); }
    });
    client.on('error', error => finish(error));
    client.stdin.on('error', () => {});
    client.on('close', () => { try { finish(null, JSON.parse(output)); } catch { finish(new Error('Desktop connection closed; delivery may be unknown.')); } });
    client.stdin.end(JSON.stringify({ id: 1, method, params }) + '\n');
  });
}
export async function pipeRequest(method, params, { pipe = defaultPipe, start = true } = {}) {
  let started = false;
  const deadline = Date.now() + 6000;
  for (;;) {
    const reply = await requestOnce(pipe, method, params);
    if (reply.error?.code !== 'PIPE_NOT_READY' || !start || Date.now() >= deadline) return reply;
    if (!started) {
      const daemon = spawn(helperPath, ['--daemon', '--pipe', pipe], { detached: true, windowsHide: true, stdio: 'ignore' });
      daemon.on('error', () => {}); daemon.unref(); started = true;
    }
    await pause(60);
  }
}
