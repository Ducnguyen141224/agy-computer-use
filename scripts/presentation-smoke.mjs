import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { NativeBridge } from '../src/native-bridge.mjs';
import { DesktopController } from '../src/desktop-controller.mjs';
import { toolResult } from '../src/tools.mjs';
import { randomUUID } from 'node:crypto';
import { pixel, solidPng } from '../tests/png.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
process.env.AGY_DESKTOP_TEST_SCOPE=randomUUID().replaceAll('-','');
execFileSync('dotnet', ['build', 'tests/fixture/DesktopFixture.csproj', '-c', 'Release', '--nologo'], { cwd: root, stdio: 'pipe', windowsHide: true });
const title = `AGY presentation ${Date.now()}`;
const fixture = spawn(fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url)), [title], { stdio: 'ignore' });
const bridge=new NativeBridge(),controller=new DesktopController(bridge);
const client={async callTool({name,arguments:args}){return toolResult(await controller.request(name,args));},async close(){controller.close();}};
let checks = 0;
const check = (value, description) => { assert.ok(value, description); checks++; process.stdout.write(`PASS ${description}\n`); };
try {
  const call = async (name, args) => {
    const reply = await client.callTool({ name, arguments: args });
    const data = JSON.parse(reply.content[0].text);
    if (reply.isError && data.error?.code==='FOCUS_DENIED') {
      const diagnostic=JSON.parse((await client.callTool({name:'desktop_system',arguments:{operation:'status',diagnostic:true}})).content[0].text);
      process.stdout.write(`Focus diagnostic: ${JSON.stringify({foreground:diagnostic.diagnostic?.foreground_app,foreground_class:diagnostic.diagnostic?.foreground_class,helper_pid:diagnostic.process_id,fixture_pid:fixture.pid})}\n`);
    }
    assert.equal(reply.isError, false, JSON.stringify(data));
    return { ...data, image: reply.content.find(item => item.type === 'image')?.data };
  };
  const waitFrame = async (window_id, region, expected) => {
    const deadline = Date.now() + 2500; let frame, observed;
    do {
      frame = await call('desktop_capture', { window_id, region, mode: 'screen', max_dimension: 400 });
      observed = pixel(frame.image, Math.floor(frame.width / 2), Math.floor(frame.height / 2));
      if (expected.every((value, index) => Math.abs(value - observed[index]) < 10)) return { frame, observed };
      await call('desktop_query', { operation: 'time_wait', timeout_ms: 50 });
    } while (Date.now() < deadline);
    process.stdout.write(`Presentation pixel mismatch: ${JSON.stringify({ expected, observed, renderer: (await call('desktop_system', { operation: 'cursor_status' })).renderer, foreground: (await call('desktop_system', { operation: 'window_info', window_id })).foreground })}\n`);
    if (process.argv.includes('--diagnostic-image')) {
      const visual = await call('desktop_capture', { window_id, mode: 'screen', max_dimension: 1000 });
      process.stdout.write('DIAGNOSTIC_IMAGE:' + visual.image + '\n');
    }
    return { frame, observed };
  };
  const window_id = (await call('desktop_query', { operation: 'window_wait', query: title, timeout_ms: 5000 })).windows.find(item => item.pid === fixture.pid).id;
  const find = async (name, role) => { const result = await call('desktop_find', { window_id, name, ...(role ? { role } : {}), include_bounds: true }); assert.equal(result.items.length, 1); return result.items[0]; };
  const pad = await find('Gesture pad');
  const log = await find('Gesture log');
  const region = { x: pad.bounds.x + 16, y: pad.bounds.y + pad.bounds.height - 40, width: 60, height: 40 };
  await call('desktop_act', { action: 'focus_window', window_id });
  const original = await call('desktop_capture', { window_id, region, max_dimension: 200 });
  const basePixel = pixel(original.image, 30, 20);
  // Keep the render surface alive only for these explicit pixel/theme assertions;
  // activity-smoke covers the automatic input-only lifetime independently.
  const renderLease=await bridge.request('presentation',{operation:'begin'});assert.ok(renderLease.ok);
  await call('desktop_system', { operation: 'cursor_config', agent: 'tester', cursor: { enabled: true, style: 'dot', fill: '#FF0000', accent: '#FF0000', labels: false, size: 28, travel_ms: 0 } });
  await call('desktop_act', { action: 'mouse_move', window_id, capture_id: original.capture_id, x: 30, y: 20 });
  const { frame: seen, observed: dot } = await waitFrame(window_id, region, [255, 0, 0]);
  check(dot[0] > 240 && dot[1] < 15 && dot[2] < 15, 'presentation cursor is actually rendered in visible desktop pixels');
  check((await call('desktop_system', { operation: 'window_info', window_id })).foreground, 'overlay does not steal foreground focus');
  check(!(await call('desktop_windows', { query: 'AGY cursor presentation' })).windows.some(item => item.title === 'AGY cursor presentation'), 'presentation window is excluded from app targets');
  await call('desktop_act', { action: 'click_point', window_id, capture_id: seen.capture_id, x: 30, y: 20 });
  check((await call('desktop_query', { operation: 'get', ref: log.ref, property: 'value' })).value.includes('down:Left'), 'physical click passes through the rendered cursor into the intended real control');
  await call('desktop_act', { action: 'click', ref: pad.ref, modifiers: ['ctrl', 'shift'] });
  check((await call('desktop_query', { operation: 'get', ref: log.ref, property: 'value' })).value.includes('modifiers:Shift, Control'), 'atomic modified click reaches the control with both modifiers held');
  await call('desktop_act', { action: 'click', ref: (await find('Reset gestures')).ref });
  const released = await call('desktop_capture', { window_id, region, max_dimension: 200 });
  await call('desktop_act', { action: 'click_point', window_id, capture_id: released.capture_id, x: 30, y: 20 });
  check(!(await call('desktop_query', { operation: 'get', ref: log.ref, property: 'value' })).value.includes('modifiers:'), 'the following real click observes that both modifiers were released');
  const field = await find('Message', 'edit');
  await call('desktop_act', { action: 'focus', ref: field.ref, expect: { property: 'focused', equals: true } });
  await call('desktop_act', { action: 'press_key', ref: field.ref, key: 'End' });
  check((await call('desktop_system', { operation: 'cursor_status' })).renderer.caret?.height > 0, 'caret presentation uses an actual Windows insertion caret after a focused key action');
  await call('desktop_system', { operation: 'cursor_config', agent: 'second', cursor: { style: 'ring', label: 'Second agent profile' } });
  check((await call('desktop_system', { operation: 'cursor_status' })).profiles.length === 2, 'multiple agent profiles coexist in one RAM session');
  const session = (await call('desktop_session', { operation: 'info' })).session_id;
  await call('desktop_system', { operation: 'cursor_config', agent: 'tester', cursor: { png_base64: solidPng(0, 0, 255), pointer_png_base64: solidPng(0, 255, 0), hotspot_x: 14, hotspot_y: 14, pointer_hotspot_x: 14, pointer_hotspot_y: 14, ripple: false, highlight: false, travel_min_ms: 40, travel_max_ms: 100, bow: 1.5, overshoot: 0.07, tremor: 1.2, motion_seed: 27, pause_ms: 30 } });
  const hoverTarget = await find('Reset gestures', 'button');
  await call('desktop_act', { action: 'hover', ref: hoverTarget.ref });
  const waitPixel = async (bounds, expected) => (await waitFrame(window_id, bounds, expected)).observed;
  const pointerPixel = await waitPixel(hoverTarget.bounds, [0, 255, 0]);
  check(pointerPixel[1] > 240 && pointerPixel[0] < 10 && pointerPixel[2] < 10, 'custom pressable PNG reaches its actual target after curved seeded presentation motion');
  await call('desktop_act', { action: 'hover', ref: pad.ref });
  const normalPixel = await waitPixel(pad.bounds, [0, 0, 255]);
  check(normalPixel[2] > 240 && normalPixel[0] < 10 && normalPixel[1] < 10, 'custom normal PNG is restored over a non-pressable control with its own hotspot');
  await call('desktop_session', { operation: 'start' });
  check(!(await call('desktop_system', { operation: 'cursor_status' })).visible, 'new session hides the previous session cursor profiles');
  await call('desktop_session', { operation: 'switch', session_id: session });
  check((await call('desktop_system', { operation: 'cursor_status' })).profiles.length === 2, 'switching back restores its own cursor profiles');
  await call('desktop_system', { operation: 'cursor_config', agent: 'second', cursor: { multi_agent: true } });
  const refused = await client.callTool({ name: 'desktop_act', arguments: { action: 'focus_window', window_id } });
  check(refused.isError && JSON.parse(refused.content[0].text).error.code === 'AGENT_REQUIRED', 'multi-agent presentation refuses an action without an explicit configured agent');
  await call('desktop_act', { action: 'focus_window', window_id, agent: 'tester' });
  await call('desktop_system', { operation: 'cursor_remove', agent: 'second' });
  await call('desktop_system', { operation: 'cursor_remove', agent: 'tester' });
  const { frame: cleared } = await waitFrame(window_id, region, basePixel);
  check(basePixel.every((value, index) => Math.abs(value - pixel(cleared.image, 30, 20)[index]) < 3), 'removing all profiles removes their actual pixels from the screen');
  await bridge.request('presentation',{operation:'end',token:renderLease.data.token});
  process.stdout.write(`Presentation smoke passed: ${checks} checks, PNGs stayed in RAM.\n`);
} finally { await client.close().catch(() => {}); fixture.kill(); }
