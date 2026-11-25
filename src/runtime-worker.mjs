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
  (() => {
    const stringify = JSON.stringify, parse = JSON.parse, compile = Function;
    const invoke = __desktopCall;
    const call = (method, args = {}) => {
      const response = parse(invoke(method, stringify(args)));
      if (!response.ok) { const error = new Error(response.error.message); error.code = response.error.code; throw error; }
      return response.data;
    };
    const api = Object.create(null);
    for (const method of ['windows','observe','find','act','query','system','capture','read']) api[method] = args => call(method, args);
    api.one = result => {
      if (!result || result.complete !== true || result.items?.length !== 1) throw new Error('one() requires exactly one candidate in a complete search. Narrow the scope.');
      return result.items[0];
    };
    api.emit = value => call('emit', { value });
    globalThis.desktop = Object.freeze(api);
    const bApi = Object.create(null);
    for (const method of ['session','tabs','read','act','wait','capture','dialog']) bApi[method] = args => call('browser:' + method, args);
    bApi.find = (readResult, filter = {}) => {
      const items = readResult?.items ?? [];
      const exact = filter.exact ?? true;
      return items.filter(item =>
        (filter.role === undefined || item.role === filter.role) &&
        (filter.name === undefined || (exact ? item.name === filter.name : String(item.name ?? '').toLowerCase().includes(String(filter.name).toLowerCase())))
      );
    };
    bApi.one = (readResult, filter) => {
      const matches = filter ? bApi.find(readResult, filter) : (readResult?.items ?? []);
      if (matches.length !== 1) throw new Error('browser.one() requires exactly one matching candidate.');
      return matches[0];
    };
    bApi.emit = api.emit;
    globalThis.browser = Object.freeze(bApi);
    delete globalThis.__desktopCall;
    return code => {
      const fn = compile('"use strict";\\n' + code);
      const value = fn.call(undefined);
      if (value && typeof value.then === 'function') throw new Error('Use synchronous workflow JavaScript; desktop calls already suspend for native I/O.');
      const encoded = stringify(value);
      return encoded === undefined ? 'null' : encoded;
    };
  })()
`);
const runner = vm.unwrapResult(bootstrap);
let running = false;
parentPort.on('message', async message => {
  if (message.type === 'reply') { const resolve = replies.get(message.id); replies.delete(message.id); resolve?.(message.reply); return; }
  if (message.type !== 'run' || running) return;
  running = true; deadline = Date.now() + message.timeout_ms;
  try {
    // The guest has no Node objects, filesystem, network, module loader or host eval.
    // Desktop methods suspend through Asyncify; scripts compile safely via captured Function.
    const runKey = '__agyRun_' + Math.random().toString(36).slice(2);
    const codeKey = '__agyCode_' + Math.random().toString(36).slice(2);
    const codeVal = vm.newString(message.code);
    vm.setProp(vm.global, runKey, runner);
    vm.setProp(vm.global, codeKey, codeVal);
    codeVal.dispose();
    const evaluated = await vm.evalCodeAsync(`(() => { const r = globalThis.${runKey}, c = globalThis.${codeKey}; delete globalThis.${runKey}; delete globalThis.${codeKey}; return r(c); })()`, 'desktop-workflow.js');
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
