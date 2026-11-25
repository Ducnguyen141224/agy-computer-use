import { Worker } from 'node:worker_threads';

/** A worker owns one QuickJS/WASM heap. Guest state is never copied onto disk. */
export class WorkflowRuntime {
  constructor() {
    this.worker = new Worker(new URL('./runtime-worker.mjs', import.meta.url), { execArgv: [], stdout: true, stderr: true, resourceLimits: { maxOldGenerationSizeMb: 96, maxYoungGenerationSizeMb: 16, stackSizeMb: 4 } });
    this.worker.stdout.resume(); this.worker.stderr.resume();
    this.running = null; this.closed = false;
    this.ready = new Promise((resolve, reject) => { this.resolveReady = resolve; this.rejectReady = reject; });
    this.worker.on('message', message => {
      if (message.type === 'ready') { this.resolveReady(); return; }
      const run = this.running; if (!run || run.settled) return;
      if (message.type === 'call') {
        Promise.resolve().then(() => run.dispatch(message.method, message.json)).then(reply => {
          if (!this.closed && this.running === run) this.worker.postMessage({ type: 'reply', id: message.id, reply });
        }, error => {
          if (!this.closed && this.running === run) this.worker.postMessage({ type: 'reply', id: message.id, reply: { ok: false, error: { code: error.code ?? 'RUNTIME_ERROR', message: error.message } } });
        });
      }
      if (message.type === 'done') { run.settled = true; run.finish(message); }
    });
    const failed = error => { this.closed = true; this.rejectReady(error); this.running?.finish({ ok: false, error: { code: 'RUNTIME_EXITED', message: 'The isolated runtime stopped. Its variables were discarded; no action was retried.' } }); };
    this.worker.on('error', failed);
    this.worker.on('exit', () => failed(new Error('Runtime worker exited.')));
    // Avoid an unhandled rejection if an idle worker is closed before initialization.
    this.ready.catch(() => {});
  }
  async run(code, { timeout_ms, dispatch, signal }) {
    if (this.closed || this.running) throw new Error('Runtime is closed or busy.');
    return new Promise(resolve => {
      let finished = false, timer = null, initTimer = null;
      const finish = result => { if (finished) return; finished = true; clearTimeout(initTimer); clearTimeout(timer); signal?.removeEventListener('abort', abort); this.running = null; resolve(result); };
      const interrupt = code => { this.close(); finish({ ok: false, error: { code, message: 'Workflow was interrupted. Inspect current UI before any further input; no action was retried.' } }); };
      const abort = () => interrupt('CANCELLED');
      initTimer = setTimeout(() => interrupt('RUNTIME_TIMEOUT'), Math.max(timeout_ms, 5000));
      this.running = { dispatch, finish, settled: false };
      signal?.addEventListener('abort', abort, { once: true });
      if (signal?.aborted) { abort(); return; }
      this.ready.then(() => {
        if (finished) return;
        clearTimeout(initTimer);
        timer = setTimeout(() => interrupt('RUNTIME_TIMEOUT'), timeout_ms);
        this.worker.postMessage({ type: 'run', code, timeout_ms });
      }, () => interrupt('RUNTIME_EXITED'));
    });
  }
  close() { if (this.closed) return; this.closed = true; this.worker.terminate(); }
}
