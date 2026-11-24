import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const executable = fileURLToPath(new URL('../native/bin/Release/net10.0-windows/AgyComputerUse.exe', import.meta.url));

export class BridgeError extends Error {
  constructor(code, message, details = {}) {
    super(message);
    this.code = code;
    this.details = details;
  }
}

/** One helper, one request at a time. No desktop data or stderr is written to disk. */
export class NativeBridge {
  constructor({ command = executable, args = [], timeoutMs = 12000 } = {}) {
    this.command = command;
    this.args = args;
    this.timeoutMs = timeoutMs;
    this.tail = Promise.resolve();
    this.sequence = 0;
    this.child = null;
    this.pending = null;
    this.closed = false;
    this.generation = 0;
  }

  request(method, params, { signal } = {}) {
    const operation = this.tail.then(() => {
      if (signal?.aborted) throw new BridgeError('CANCELLED', 'Request cancelled before execution.', { delivery: 'not_sent' });
      if (this.closed) throw new BridgeError('CLOSED', 'Desktop server is closed.');
      return this.#send(method, params, signal);
    });
    this.tail = operation.catch(() => {});
    return operation;
  }

  #start() {
    if (this.child) return;
    const child = spawn(this.command, this.args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    this.generation++;
    this.child = child;
    let buffer = '';
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      if (this.child !== child) return;
      buffer += chunk;
      if (buffer.length > 48 * 1024 * 1024) {
        this.#reset(new BridgeError('PROTOCOL_ERROR', 'Helper output exceeded its memory budget. Observe again.', { delivery: 'unknown' }));
        return;
      }
      for (let end; (end = buffer.indexOf('\n')) !== -1;) {
        const line = buffer.slice(0, end).trim();
        buffer = buffer.slice(end + 1);
        if (!line) continue;
        try {
          const reply = JSON.parse(line);
          if (!this.pending || reply.id !== this.pending.id) throw new Error('Unexpected helper response.');
          const pending = this.pending;
          this.pending = null;
          pending.cleanup();
          pending.resolve(reply);
        } catch {
          this.#reset(new BridgeError('PROTOCOL_ERROR', 'Helper protocol failed. List windows and observe again.', { delivery: 'unknown' }));
          return;
        }
      }
    });
    // Deliberately discard native stderr: it must not leak desktop content into MCP stdout/logs.
    child.stderr.resume();
    child.stdin.on('error', () => {
      if (this.child === child) this.#reset(new BridgeError('HELPER_EXITED', 'Helper connection closed. List windows again.', { delivery: 'unknown' }));
    });
    child.once('error', () => {
      if (this.child === child) this.#reset(new BridgeError('HELPER_START_FAILED', 'Build the native helper first: npm run build.'));
    });
    child.once('exit', () => {
      if (this.child === child) this.#reset(new BridgeError('HELPER_EXITED', 'Helper exited. Its in-memory refs are invalid; list windows again.', { delivery: 'unknown' }));
    });
  }

  #send(method, params, signal) {
    this.#start();
    return new Promise((resolve, reject) => {
      const id = ++this.sequence;
      const mutating = ['act', 'system', 'batch'].includes(method);
      const timer = setTimeout(() => this.#reset(new BridgeError('HELPER_TIMEOUT', 'Windows accessibility timed out. The helper was restarted; list windows again. Do not repeat input until its effect is checked.', { delivery: mutating ? 'unknown' : 'not_sent' })), this.timeoutMs);
      const onAbort = () => this.#reset(new BridgeError('CANCELLED', 'Operation interrupted. List windows and inspect the current state before retrying.', { delivery: mutating ? 'unknown' : 'not_sent' }));
      const cleanup = () => {
        clearTimeout(timer);
        signal?.removeEventListener('abort', onAbort);
      };
      this.pending = { id, resolve, reject, cleanup };
      signal?.addEventListener('abort', onAbort, { once: true });
      if (signal?.aborted) { onAbort(); return; }
      this.child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
    });
  }

  #reset(error) {
    const child = this.child;
    this.child = null;
    if (this.pending) {
      const pending = this.pending;
      this.pending = null;
      pending.cleanup();
      pending.reject(error);
    }
    if (child) {
      child.stdin.destroy();
      child.kill();
    }
  }

  close() {
    this.closed = true;
    this.#reset(new BridgeError('CLOSED', 'Server closed.', { delivery: 'unknown' }));
  }
  invalidate() { this.#reset(new BridgeError('HELPER_RESET', 'Helper state was invalidated. Observe current UI before further input.', { delivery: 'unknown' })); }
}
