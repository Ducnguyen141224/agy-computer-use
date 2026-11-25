import test from 'node:test';
import assert from 'node:assert/strict';
import { tools, validate, validateCall, toolResult } from '../src/tools.mjs';

const schema = name => tools.find(tool => tool.name === name).inputSchema;
test('reader continuations cannot silently change target/direction and lifecycle calls cannot hide inside a batch', () => {
  const read = tools.find(tool => tool.name === 'desktop_read');
  for (const args of [
    {operation:'snapshot'}, {operation:'snapshot',window_id:'w',reader_id:'r'},
    {operation:'collect'}, {operation:'collect',root_ref:'@a',reader_id:'r'},
    {operation:'collect',reader_id:'r',direction:'up'}, {operation:'collect',root_ref:'@a',budget_ms:12000}
  ]) assert.throws(() => validateCall(read, args));
  validateCall(read, {operation:'collect',reader_id:'r',max_pages:2});
  for (const operation of ['control_resume','control_start','control_end'])
    assert.throws(() => validateCall(tools.find(tool => tool.name === 'desktop_batch'), {commands:[{tool:'desktop_system',arguments:{operation}}]}));
});
test('unknown arguments, oversized observations, and invalid actions fail before native dispatch', () => {
  assert.throws(() => validate(schema('desktop_act'), { action: 'run_shell' }));
  assert.throws(() => validate(schema('desktop_observe'), { window_id: 'w', limit: 10000 }));
  assert.throws(() => validate(schema('desktop_act'), { action: 'click', shell: 'anything' }));
  assert.throws(() => validate(schema('desktop_act'), { action: 'set_value', text: 'a'.repeat(10001) }));
  assert.throws(() => validate(schema('desktop_capture'), { window_id: 'w', region: { x: 0 } }));
});
test('capture bytes occur only once, in the MCP image block', () => {
  const result = toolResult({ ok: true, data: { capture_id: 'c1', image: 'fake-image-bytes' } });
  assert.equal(result.content[1].type, 'image');
  assert.equal(result.content[1].data, 'fake-image-bytes');
  assert.equal(result.content[0].text.includes('fake-image-bytes'), false);
  assert.equal('structuredContent' in result, false);
});
test('failed postcondition is exposed as an MCP error despite successful dispatch', () => {
  const result = toolResult({ ok: true, data: { delivery: 'sent', verification: 'failed' } });
  assert.equal(result.isError, true);
  assert.equal(JSON.parse(result.content[0].text).delivery, 'sent');
});

test('whole-batch preflight rejects missing targets and wrong expectation types before dispatch', () => {
  const batch = tools.find(tool => tool.name === 'desktop_batch');
  assert.throws(() => validateCall(batch, { commands: [
    { tool: 'desktop_act', arguments: { action: 'set_value', ref: '@existing', text: 'valid' } },
    { tool: 'desktop_act', arguments: { action: 'click' } }
  ] }));
  assert.throws(() => validateCall(batch, { commands: [
    { tool: 'desktop_act', arguments: { action: 'check', ref: '@existing', expect: { property: 'checked', equals: 'yes' } } }
  ] }));
});
test('dynamic state maps reject unknown names and non-boolean filters before native dispatch', () => {
  const find = tools.find(tool => tool.name === 'desktop_find');
  assert.throws(() => validateCall(find, { window_id: 'w', states: { unsupported_state: true } }));
  assert.throws(() => validateCall(find, { window_id: 'w', states: { readonly: 'false' } }));
  assert.throws(() => validateCall(find, { window_id: 'w', states: {} }));
  validateCall(find, { window_id: 'w', automation_id: 'field', states: { readonly: false, enabled: true } });
});
test('secure attention cannot be synthesized and desktop shortcuts require an explicit force flag', () => {
  const act = tools.find(tool => tool.name === 'desktop_act');
  assert.throws(() => validateCall(act, { action: 'press_key', ref: '@field', key: 'Ctrl+Alt+Delete', force: true }));
  assert.throws(() => validateCall(act, { action: 'press_key', ref: '@field', key: 'Meta+L' }));
  assert.throws(() => validateCall(act, { action: 'press_key', ref: '@field', key: 'Cmd+Win+A' }));
  validateCall(act, { action: 'press_key', ref: '@field', key: 'Win+L', force: true });
  validateCall(act, { action: 'press_key', ref: '@field', key: 'Ctrl+A' });
});

test('batch images are mapped once and launch/gesture conflicts fail before dispatch', () => {
  const batch = toolResult({ ok: true, data: { results: [{ ok: true, data: { image: 'first-png' } }, { ok: true, data: { image: 'second-png' } }] } });
  const data = JSON.parse(batch.content[0].text);
  assert.equal(data.results[0].data.image_index, 0);
  assert.equal(data.results[1].data.image_index, 1);
  assert.deepEqual(batch.content.slice(1).map(item => item.data), ['first-png', 'second-png']);
  assert.equal(batch.content[0].text.includes('first-png'), false);
  const system = tools.find(tool => tool.name === 'desktop_system');
  assert.throws(() => validateCall(system, { operation: 'launch', app: 'test\0.exe' }));
  assert.throws(() => validateCall(system, { operation: 'launch', app: 'browser.exe', args: ['--headless'], activate: true }));
  const act = tools.find(tool => tool.name === 'desktop_act');
  assert.throws(() => validateCall(act, { action: 'click', ref: '@r', modifiers: ['ctrl', 'ctrl'] }));
  assert.throws(() => validateCall(act, { action: 'drag', ref: '@r', to_ref: '@other', to_x: 2 }));
});
