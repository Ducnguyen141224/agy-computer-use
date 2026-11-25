/** Compact task notes are recovery hints, never authorization or live UI identity. */
export class TaskContext {
  constructor() { this.goal = ''; this.constraints = []; this.next_step = ''; this.revision = 0; this.readRevision = 0; this.window_id = null; this.observation = null; this.verified = []; this.uncertainty = null; this.needs_observation = true; }
  record(method, args, reply) {
    this.revision++;
    const data = reply.data;
    if (!reply.ok) return;
    if (['observe', 'find'].includes(method)) {
      this.window_id = data.window_id; this.observation = { snapshot: data.snapshot, complete: data.complete, scope: args.root_ref ?? 'window' }; this.readRevision = this.revision; this.needs_observation = false;
    }
    if (method === 'query' && (['get', 'is', 'focused'].includes(args.operation) || data.verification === 'passed')) this.readRevision = this.revision;
    if (method === 'read' && data.source === 'uia_text') this.readRevision = this.revision;
    if (data.verification === 'passed') {
      this.verified.push({ source: method, operation: args.action ?? args.operation, ref: args.ref, property: args.expect?.property ?? args.property, revision: this.revision });
      this.verified = this.verified.slice(-6);
    }
  }
  uncertain(method, args, code, delivery = 'unknown') {
    this.revision++; this.uncertainty = { method, operation: args.action ?? args.operation, code, delivery, revision: this.revision }; this.needs_observation = true;
  }
  invalidate() { this.window_id = null; this.observation = null; this.needs_observation = true; this.readRevision = 0; this.verified = this.verified.map(({ ref, ...condition }) => ({ ...condition, historical: true })); }
  command(args, session_id) {
    if (args.operation === 'update') {
      if (args.goal !== undefined) this.goal = args.goal;
      if (args.constraints !== undefined) this.constraints = [...args.constraints];
      if (args.next_step !== undefined) this.next_step = args.next_step;
      this.revision++;
    }
    if (args.operation === 'reconcile') {
      if (!this.uncertainty || this.readRevision <= this.uncertainty.revision) throw Object.assign(new Error('Read the current UI after the uncertain action before reconciling. No action was sent.'), { code: 'FRESH_READ_REQUIRED' });
      this.recovery_note = args.recovery_note; this.uncertainty = null; this.revision++;
    }
    if (args.operation === 'compact') {
      const bound = (value, max) => value.length <= max ? value : value.slice(0, max);
      this.goal = bound(this.goal, 400);
      this.constraints = this.constraints.slice(0, 8).map(item => bound(item, 160));
      this.next_step = bound(this.next_step, 300);
      this.verified = this.verified.slice(-3);
      this.revision++;
    }
    return { session_id, revision: this.revision, goal: this.goal, constraints: this.constraints, next_step: this.next_step, window_id: this.window_id, last_observation: this.observation, verified_conditions: this.verified, unresolved_action: this.uncertainty, needs_observation: this.needs_observation, recovery_note: this.recovery_note, memory_only: true, note: 'Task notes are not live refs or authorization. This does not compact the host conversation or prove task completion.' };
  }
}
