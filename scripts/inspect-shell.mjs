import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { fileURLToPath } from 'node:url';
const client = new Client({ name: 'shell-inspection', version: '0.2.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
try {
  await client.connect(transport);
  const surfaces = JSON.parse((await client.callTool({ name: 'desktop_system', arguments: { operation: 'list_surfaces' } })).content[0].text);
  const taskbar = surfaces.surfaces.find(item => item.kind === 'taskbar');
  if (process.argv.includes('--capture-start')) {
    const diagnostic = JSON.parse((await client.callTool({ name: 'desktop_system', arguments: { operation: 'list_surfaces', diagnostic: true } })).content[0].text);
    const startHost = diagnostic.surfaces.find(item => item.app === 'startmenuexperiencehost');
    const opened = await client.callTool({ name: 'desktop_system', arguments: { operation: 'open_surface', surface: 'start_menu' } });
    const capture = await client.callTool({ name: 'desktop_capture', arguments: { window_id: startHost.window.id, max_dimension: 1000 } });
    process.stdout.write(JSON.stringify({ metadata: { opened: JSON.parse(opened.content[0].text).delivery, host: { width: startHost.width, height: startHost.height, foreground: startHost.foreground }, capture: JSON.parse(capture.content[0].text) }, image: capture.content.find(block => block.type === 'image') }));
  } else {
  const tree = JSON.parse((await client.callTool({ name: 'desktop_observe', arguments: { window_id: taskbar.window.id, depth: 8, limit: 80, include_bounds: true } })).content[0].text);
  const start = tree.items.find(item => item.name === 'Start' && item.role === 'button');
  let focus;
  if (process.argv.includes('--click-start')) focus = JSON.parse((await client.callTool({ name: 'desktop_system', arguments: { operation: 'focus_window', window_id: taskbar.window.id } })).content[0].text);
  const capture = await client.callTool({ name: 'desktop_capture', arguments: { window_id: taskbar.window.id, max_dimension: 1600 } });
  const meta = JSON.parse(capture.content[0].text);
  let clicked, after;
  if (process.argv.includes('--click-start') && focus.delivery === 'sent') {
    const x = Math.floor((start.bounds.x + start.bounds.width / 2 - meta.source_region.x) * meta.width / meta.source_region.width);
    const y = Math.floor((start.bounds.y + start.bounds.height / 2 - meta.source_region.y) * meta.height / meta.source_region.height);
    clicked = JSON.parse((await client.callTool({ name: 'desktop_act', arguments: { action: 'click_point', window_id: taskbar.window.id, capture_id: meta.capture_id, x, y } })).content[0].text);
    const waited = JSON.parse((await client.callTool({ name: 'desktop_query', arguments: { operation: 'surface_wait', surface: 'start_menu', timeout_ms: 4000 } })).content[0].text);
    const status = JSON.parse((await client.callTool({ name: 'desktop_system', arguments: { operation: 'status', diagnostic: true } })).content[0].text);
    after = { verification: waited.verification, count: waited.surfaces?.length, diagnostic: status.diagnostic };
    if (waited.surfaces?.[0]) await client.callTool({ name: 'desktop_system', arguments: { operation: 'close_surface', window_id: waited.surfaces[0].window.id } });
  }
  process.stdout.write(JSON.stringify({ metadata: { taskbar: taskbar.window.bounds, start: start ? { role: start.role, bounds: start.bounds } : null, focus, clicked, after, capture: meta }, image: capture.content.find(block => block.type === 'image') }));
  }
} finally { await client.close(); }
