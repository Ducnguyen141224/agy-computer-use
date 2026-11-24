import { parentPort } from 'node:worker_threads';
import { newAsyncRuntime } from 'quickjs-emscripten';

const runtime = await newAsyncRuntime();
runtime.setMemoryLimit(32 * 1024 * 1024);
runtime.setMaxStackSize(256 * 1024);
let deadline = Infinity;
runtime.setInterruptHandler(() => Date.now() >= deadline);
const vm = runtime.newContext();
let sequence = 0;
const replies = new Map();
const call = vm.newAsyncifiedFunction('__desktopCall', async (method, args) => {
  const id = ++sequence;
  const result = await new Promise(resolve => { replies.set(id, resolve); parentPort.postMessage({ type: 'call', id, method: vm.getString(method), json: vm.getString(args) }); });
  return vm.newString(JSON.stringify(result));
});
vm.setProp(vm.global, '__desktopCall', call); call.dispose();
const bootstrap = vm.evalCode(`
  globalThis.state = Object.create(null);
  globalThis.desktop = (() => {
    const invoke = __desktopCall;
    const call = (method, args = {}) => {
      const response = JSON.parse(invoke(method, JSON.stringify(args)));
      if (!response.ok) { const error = new Error(response.error.message); error.code = response.error.code; throw error; }
      return response.data;
    };
    const api = Object.create(null);
    for (const method of ['windows','observe','find','act','query','system','capture']) api[method] = args => call(method, args);
    api.one = result => {
      if (!result || result.complete !== true || result.items?.length !== 1) throw new Error('one() requires exactly one candidate in a complete search. Narrow the scope.');
      return result.items[0];
    };
    api.emit = value => call('emit', { value });
    return Object.freeze(api);
  })();
  delete globalThis.__desktopCall;
`);
vm.unwrapResult(bootstrap).dispose();
let running = false;
parentPort.on('message', async message => {
  if (message.type === 'reply') { const resolve = replies.get(message.id); replies.delete(message.id); resolve?.(message.reply); return; }
  if (message.type !== 'run' || running) return;
  running = true; deadline = Date.now() + message.timeout_ms;
  try {
    // The guest has no Node objects, filesystem, network, module loader or host eval.
    // Desktop methods suspend through Asyncify; scripts use ordinary synchronous JS.
    const expression = `(() => { const value = (function(){ "use strict";\n${message.code}\n}).call(undefined); if (value && typeof value.then === 'function') throw new Error('Use synchronous workflow JavaScript; desktop calls already suspend for native I/O.'); return JSON.stringify(value); })()`;
    const evaluated = await vm.evalCodeAsync(expression, 'desktop-workflow.js');
    const value = vm.unwrapResult(evaluated);
    try {
      const encoded = vm.typeof(value) === 'undefined' ? 'null' : vm.getString(value);
      if (encoded.length > 16000) throw new Error('Return value exceeds 16000 characters. Select the fields needed for the next decision.');
      parentPort.postMessage({ type: 'done', ok: true, value: JSON.parse(encoded) });
    } finally { value.dispose(); }
  } catch (error) {
    parentPort.postMessage({ type: 'done', ok: false, error: { code: Date.now() >= deadline ? 'RUNTIME_TIMEOUT' : 'SCRIPT_ERROR', message: String(error?.message ?? error).slice(0, 1000) } });
  } finally { deadline = Infinity; running = false; }
});
parentPort.postMessage({ type: 'ready' });
