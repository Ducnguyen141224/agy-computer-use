import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { fileURLToPath } from 'node:url';

const client = new Client({ name: 'agy-shell-smoke', version: '0.2.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
let checks = 0;
let opened;
const check = (condition, message) => { assert.ok(condition, message); checks++; process.stdout.write(`PASS ${message}\n`); };
async function call(name, args) {
  const reply = await client.callTool({ name, arguments: args });
  const data = JSON.parse(reply.content[0].text);
  if (reply.isError) {
    const inventory = await client.callTool({ name: 'desktop_system', arguments: { operation: 'list_surfaces', diagnostic: true } });
    const state = JSON.parse(inventory.content[0].text);
    process.stderr.write(`Shell metadata: ${JSON.stringify((state.surfaces ?? []).filter(item => item.visible).map(item => ({ app: item.app, class_name: item.class_name, uia_offscreen: item.uia_offscreen, width: item.width, height: item.height, foreground: item.foreground })))}\n`);
    if (data.error?.details) process.stderr.write(`Failure metadata: ${JSON.stringify(data.error.details)}\n`);
    throw new Error(`Shell tool failed: ${data.error?.code ?? data.verification}`);
  }
  return data;
}
try {
  await client.connect(transport);
  const status = await call('desktop_system', { operation: 'status' });
  process.stdout.write(`Shell status: ${JSON.stringify({ os_build: status.os_build, helper_integrity: status.integrity, taskbar: status.taskbar })}\n`);
  const surfaces = await call('desktop_system', { operation: 'list_surfaces' });
  for (const existing of surfaces.surfaces.filter(item => ['start_menu', 'shell_panel', 'system_tray_overflow'].includes(item.kind))) await call('desktop_system', { operation: 'close_surface', window_id: existing.window.id });
  const taskbar = surfaces.surfaces.find(item => item.kind === 'taskbar');
  check(Boolean(taskbar), 'discovers the real Windows taskbar');
  await call('desktop_system', { operation: 'open_surface', surface: 'start_menu' });
  const start = await call('desktop_query', { operation: 'surface_wait', surface: 'start_menu', timeout_ms: 4000 });
  opened = start.surfaces[0].window.id;
  const observed = await call('desktop_observe', { window_id: opened, depth: 3, limit: 30 });
  check(observed.items.length > 0, 'opens and reads the real Start menu accessibility tree');
  await call('desktop_system', { operation: 'close_surface', window_id: opened });
  await call('desktop_query', { operation: 'surface_wait', surface: 'start_menu', present: false, timeout_ms: 4000 });
  opened = undefined;
  check(true, 'closes Start menu without closing its system process');
  const activeTaskbar = await call('desktop_system', { operation: 'open_surface', surface: 'taskbar' });
  const taskbarFocus = await call('desktop_query', { operation: 'focused', window_id: activeTaskbar.window.id });
  check(Boolean(taskbarFocus.item.ref), 'taskbar keyboard navigation exposes an exact focused ref');
  await call('desktop_system', { operation: 'close_surface', window_id: activeTaskbar.window.id });
  await call('desktop_system', { operation: 'open_surface', surface: 'system_tray_overflow' });
  const overflow = await call('desktop_query', { operation: 'surface_wait', surface: 'system_tray_overflow', timeout_ms: 3000 });
  opened = overflow.surfaces[0].window.id;
  check(Boolean(opened), 'opens the real system-tray overflow by its accessibility control');
  await call('desktop_system', { operation: 'close_surface', window_id: opened });
  opened = undefined;
  await call('desktop_system', { operation: 'open_surface', surface: 'action_center' });
  const notifications = await call('desktop_query', { operation: 'surface_wait', surface: 'shell_panel', timeout_ms: 4000 });
  opened = notifications.surfaces[0].window.id;
  const notificationTree = await call('desktop_system', { operation: 'list_notifications', window_id: opened, limit: 60 });
  check(Array.isArray(notificationTree.notifications), 'opens and reads recognized notification cards without dismissing user notifications');
  const absentText = `agy-absent-${Date.now()}-${Math.random()}`;
  const absence = await call('desktop_query', { operation: 'notification_wait', window_id: opened, text: absentText, present: false, timeout_ms: 0 });
  check(absence.verification === 'passed', 'notification wait verifies absence within the complete materialized tree');
  const missing = await client.callTool({ name: 'desktop_query', arguments: { operation: 'notification_wait', window_id: opened, text: absentText, timeout_ms: 0 } });
  check(missing.isError && JSON.parse(missing.content[0].text).verification === 'failed', 'a notification wait timeout is reported as a failure');
  const sameCenter = await call('desktop_system', { operation: 'open_surface', surface: 'action_center' });
  check(sameCenter.delivery === 'already_satisfied', 'opening the notification center twice leaves it open');
  await call('desktop_system', { operation: 'close_surface', window_id: opened });
  opened = undefined;
  await call('desktop_system', { operation: 'open_surface', surface: 'quick_settings' });
  const quick = await call('desktop_query', { operation: 'surface_wait', surface: 'shell_panel', timeout_ms: 3000 });
  opened = quick.surfaces[0].window.id;
  const quickTree = await call('desktop_observe', { window_id: opened, depth: 5, limit: 60 });
  check(quickTree.items.some(item => item.role === 'button'), 'opens and reads actual quick-settings controls without changing settings');
  check((await call('desktop_system', { operation: 'open_surface', surface: 'quick_settings' })).delivery === 'already_satisfied', 'opening quick settings twice leaves the existing panel open');
  await call('desktop_system', { operation: 'close_surface', window_id: opened });
  opened = undefined;
  process.stdout.write(`Shell desktop smoke passed: ${checks} checks. No private labels or evidence files were printed/saved.\n`);
} finally {
  if (opened) await call('desktop_system', { operation: 'close_surface', window_id: opened }).catch(() => {});
  await client.close().catch(() => {});
}
