import { tools, validateCall } from './tools.mjs';
import { guides } from './guides.mjs';
import { TaskContext } from './task-context.mjs';
import { WorkflowRuntime } from './workflow-runtime.mjs';

const readOperations = new Set(['status','permissions','version','list_apps','list_displays','window_info','clipboard_get','list_surfaces','list_notifications','cursor_status','control_status','control_stop','control_resume','control_start','control_end']);
const mutates = (method, args) => args.open === true || method === 'act' || method === 'batch' || method === 'read' && args.operation === 'collect' || method === 'system' && !readOperations.has(args.operation);
const failure = (code, message, details) => ({ ok: false, error: { code, message, details } });
export class DesktopController {
  constructor(bridge) { this.bridge = bridge; this.tail = Promise.resolve(); this.session = 'default'; this.contexts = new Map([['default', new TaskContext()]]); this.runtimes = new Map(); this.generation = 0; this.closed = false; this.stopRun = null; bridge.on?.('controlStop', () => { this.stopRun?.(); for (const runtime of this.runtimes.values()) runtime.close(); this.runtimes.clear(); this.context.invalidate(); }); }
  get context() { if (!this.contexts.has(this.session)) this.contexts.set(this.session, new TaskContext()); return this.contexts.get(this.session); }
  request(name, args, { signal } = {}) {
    const work = this.tail.then(async () => {
      if (this.closed || signal?.aborted) return failure('CANCELLED', 'Request stopped before execution.');
      try {
        const tool = tools.find(item => item.name === name); if (!tool) return failure('UNKNOWN_TOOL', 'Unknown desktop tool.');
        validateCall(tool, args);
        if (name === 'desktop_help') return { ok: true, data: { topic: args.topic, instructions: guides[args.topic] } };
        if (name === 'desktop_context') return { ok: true, data: this.context.command(args, this.session) };
        if ((['desktop_windows','desktop_observe','desktop_find','desktop_capture','desktop_query','desktop_read','desktop_run','desktop_act','desktop_batch'].includes(name) || name === 'desktop_system' && mutates('system', args)) && !this.bridge.userStopped) {
          const scope = await this.#native('system', { operation: 'control_start' }, signal);
          const recoveryRead = ['desktop_windows','desktop_observe','desktop_find','desktop_capture','desktop_query'].includes(name) || name === 'desktop_read' && args.operation === 'snapshot';
          if (!scope.ok && !(scope.error?.code === 'USER_STOPPED' && recoveryRead)) return scope;
        }
        if (name === 'desktop_run') return await this.#run(args, signal);
        const method = name.slice(8);
        const native = method === 'batch' ? { ...args, commands: args.commands.map(item => ({ method: item.tool.slice(8), params: item.arguments })) } : args;
        const reply = await this.#native(method, native, signal);
        if (method === 'session' && reply.ok) this.#session(args, reply.data);
        return reply;
      } catch (error) { return failure(error.code ?? 'INVALID_ARGUMENT', error.message, error.details); }
    });
    this.tail = work.catch(() => {}); return work;
  }
  #epoch() {
    if (this.generation && this.generation !== this.bridge.generation) {
      for (const runtime of this.runtimes.values()) runtime.close(); this.runtimes.clear();
      const active = this.context; active.invalidate(); this.contexts.clear(); this.session = 'default'; this.contexts.set('default', active);
    }
    this.generation = this.bridge.generation;
  }
  async #native(method, args, signal) {
    try {
      const reply = await this.bridge.request(method, args, { signal }); this.#epoch();
      if (method !== 'lease' && method !== 'session') this.context.record(method, args, reply);
      if (mutates(method, args) && (reply.data?.verification === 'failed' && ['sent','unknown'].includes(reply.data.delivery) || reply.error?.delivery === 'unknown' || reply.error?.details?.delivery === 'unknown')) this.context.uncertain(method, args, reply.error?.code ?? 'VERIFICATION_FAILED', reply.data?.delivery ?? 'unknown');
      return reply;
    } catch (error) {
      // A killed helper loses every live runtime namespace, not the task's intent.
      if (!this.bridge.child) { for (const runtime of this.runtimes.values()) runtime.close(); this.runtimes.clear(); this.context.invalidate(); }
      if (mutates(method, args)) this.context.uncertain(method, args, error.code ?? 'NATIVE_ERROR');
      throw error;
    }
  }
  #session(args, data) {
    const discard = id => { this.runtimes.get(id)?.close(); this.runtimes.delete(id); this.contexts.delete(id); };
    if (args.operation === 'reset') discard(this.session);
    if (args.operation === 'end') discard(data.ended);
    if (data.session_id) this.session = data.session_id;
  }
  async #run(args, signal) {
    // Synchronize helper/session generation before reusing guest variables.
    const info = await this.#native('session', { operation: 'info' }, signal); if (!info.ok) return info;
    this.session = info.data.session_id;
    let runtime = this.runtimes.get(this.session);
    if (!runtime || runtime.closed) { runtime = new WorkflowRuntime(); this.runtimes.set(this.session, runtime); }
    const started = Date.now(); const controller = new AbortController();
    const abort = () => controller.abort(); signal?.addEventListener('abort', abort, { once: true }); if (signal?.aborted) abort();
    this.stopRun = abort;
    let latch = null, steps = 0, characters = 0, mutationSent = false, lease = null, leaseGeneration = this.bridge.generation;
    const emitted = [], artifacts = [];
    const dispatch = async (method, encoded) => {
      if (latch) return failure('WORKFLOW_HALTED', 'A previous step failed. No further desktop call was executed.');
      try {
        if (controller.signal.aborted) throw Object.assign(new Error('Workflow was cancelled.'), { code: 'CANCELLED' });
        if (encoded.length > 120000) throw new Error('Step arguments exceed the request budget.');
        const parameters = JSON.parse(encoded);
        if (method === 'emit') {
          const text = JSON.stringify(parameters.value); if (text === undefined || text.length > 4000 || emitted.length >= 8) throw new Error('Emit budget exceeded.');
          emitted.push(parameters.value); return { ok: true, data: { emitted: emitted.length } };
        }
        if (!['windows','observe','find','act','query','system','capture','read'].includes(method)) throw new Error('This API is unavailable in workflows.');
        const tool = tools.find(item => item.name === `desktop_${method}`); validateCall(tool, parameters);
        if (method === 'system' && ['control_resume','control_start','control_end'].includes(parameters.operation)) throw new Error('Control lifecycle operations must be direct desktop_system calls, never workflow steps.');
        if (++steps > (args.max_steps ?? 20)) throw Object.assign(new Error('Workflow desktop step budget exceeded.'), { code: 'STEP_LIMIT' });
        if (method === 'capture' || method === 'system' && parameters.operation === 'clipboard_get' && (parameters.image || ['auto','image'].includes(parameters.format))) { if (artifacts.length >= 2) throw new Error('Workflow image budget exceeded.'); }
        if (mutates(method, parameters) && this.context.uncertainty) throw Object.assign(new Error('An earlier action has unresolved delivery. Read the UI and reconcile task context before new input.'), { code: 'RECONCILIATION_REQUIRED' });
        const reply = await this.#native(method, parameters, controller.signal);
        const failed = !reply.ok || reply.data?.verification === 'failed';
        if (failed) {
          const code = reply.error?.code ?? 'VERIFICATION_FAILED';
          if (mutates(method, parameters) && !this.context.uncertainty && (reply.data?.delivery === 'sent' || reply.data?.delivery === 'unknown' || reply.error?.details?.delivery === 'unknown')) this.context.uncertain(method, parameters, code, reply.data?.delivery ?? 'unknown');
          latch = reply.error ?? { code, message: 'The postcondition failed. Observe before choosing another action.' }; return { ok: false, error: latch };
        }
        if (mutates(method, parameters)) mutationSent = true;
        const data = { ...reply.data };
        if (typeof data.image === 'string') { data.image_index = artifacts.length; artifacts.push({ image: data.image }); delete data.image; }
        characters += JSON.stringify(data).length;
        return { ok: true, data };
      } catch (error) { latch = { code: error.code ?? 'INVALID_ARGUMENT', message: error.message }; return { ok: false, error: latch }; }
    };
    let result, output;
    try {
      const locked = await this.#native('lease', { operation: 'begin' }, controller.signal); if (!locked.ok) return locked;
      lease = locked.data.token; leaseGeneration = this.bridge.generation;
      result = await runtime.run(args.code, { timeout_ms: args.timeout_ms ?? 12000, dispatch, signal: controller.signal });
      if (this.bridge.userStopped) latch = { code: 'USER_STOPPED', message: 'Escape stopped the workflow. Resume only after a new explicit user instruction.' };
      if (!result.ok || latch) {
        controller.abort(); runtime.close(); this.runtimes.delete(this.session);
        if (mutationSent && !this.context.uncertainty) this.context.uncertain('run', {}, latch?.code ?? result.error?.code ?? 'SCRIPT_ERROR');
        output = { ok: true, data: { execution_status: 'failed', verification: 'failed', error: latch ?? result.error, steps, elapsed_ms: Date.now() - started, emitted, artifacts, runtime_reset: true, recovery: this.context.command({ operation: 'read' }, this.session), replayed_actions: 0 } }; return output;
      }
      output = { ok: true, data: { execution_status: 'completed', result: result.value, emitted, artifacts, steps, elapsed_ms: Date.now() - started, metrics: { native_response_characters: characters, output_policy: 'selected return/emits only; image bytes excluded', token_usage: null }, state_preserved: true, note: 'Completed execution is not proof of task completion. Read explicit postconditions.', replayed_actions: 0 } }; return output;
    } finally {
      this.stopRun = null;
      signal?.removeEventListener('abort', abort);
      if (lease && this.bridge.child && this.bridge.generation === leaseGeneration) {
        let released = false;
        try { released = (await this.bridge.request('lease', { operation: 'end', token: lease })).ok === true; } catch { }
        if (!released) {
          this.bridge.invalidate(); runtime.close(); this.runtimes.delete(this.session); this.context.invalidate();
          if (mutationSent && !this.context.uncertainty) this.context.uncertain('run', {}, 'LEASE_RELEASE_UNKNOWN');
          if (output) Object.assign(output.data, { execution_status: 'failed', verification: 'failed', error: this.bridge.userStopped || output.data.error?.code === 'USER_STOPPED' ? { code: 'USER_STOPPED', message: 'Escape stopped control; helper/runtime were discarded. Inspect the UI before resuming.' } : { code: 'LEASE_RELEASE_UNKNOWN', message: 'Native workflow lease cleanup was not confirmed. Helper/runtime were discarded; inspect UI before continuing.' }, state_preserved: false, runtime_reset: true, recovery: this.context.command({ operation: 'read' }, this.session) });
        }
      }
    }
  }
  close() { this.closed = true; for (const runtime of this.runtimes.values()) runtime.close(); this.runtimes.clear(); this.bridge.close(); }
}
