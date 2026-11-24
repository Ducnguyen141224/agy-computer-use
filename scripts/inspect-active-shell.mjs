import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { fileURLToPath } from 'node:url';
const client = new Client({ name: 'read-only-shell-inspection', version: '0.2.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
try {
  await client.connect(transport);
  const call = async (name, args) => client.callTool({ name, arguments: args });
  const state = JSON.parse((await call('desktop_system', { operation: 'status', diagnostic: true })).content[0].text);
  const app = state.diagnostic.foreground_app;
  if (!['explorer', 'SearchHost', 'StartMenuExperienceHost', 'ShellExperienceHost', 'ShellHost'].some(value => value.toLowerCase() === app.toLowerCase())) {
    process.stdout.write(JSON.stringify({ metadata: { shell_active: false, foreground_app: app } }));
  } else {
    const windows = JSON.parse((await call('desktop_windows', { query: app })).content[0].text);
    const target = windows.windows.find(window => window.foreground);
    if (!target) process.stdout.write(JSON.stringify({ metadata: { shell_active: true, foreground_app: app, listed: false } }));
    else {
      const capture = await call('desktop_capture', { window_id: target.id, max_dimension: 1000, ...(process.argv.includes('--screen') ? { mode: 'screen' } : {}) });
      const tree = JSON.parse((await call('desktop_observe', { window_id: target.id, depth: 4, limit: 30 })).content[0].text);
      process.stdout.write(JSON.stringify({ metadata: { shell_active: true, foreground_app: app, tree }, image: capture.content.find(block => block.type === 'image') }));
    }
  }
} finally { await client.close(); }
