import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { execFileSync } from 'node:child_process';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const root = process.argv[2] ?? fileURLToPath(new URL('../plugin', import.meta.url));
const manifest = JSON.parse(readFileSync(join(root, 'plugin.json'), 'utf8'));
const config = JSON.parse(readFileSync(join(root, 'mcp.json'), 'utf8'));
assert.equal(manifest.name, 'agy-computer-use');
assert.match(readFileSync(join(root, 'skills', 'agy-computer-use', 'SKILL.md'), 'utf8'), /Windows computer use/);
const server = config.mcpServers['agy-computer-use'];
assert.equal(server.type, 'stdio');
const compatible = JSON.parse(readFileSync(join(root, 'mcp_config.json'), 'utf8')).mcpServers['agy-computer-use'];
assert.equal(compatible.command, server.command);
assert.deepEqual(compatible.args, server.args);
if (process.argv[2]) {
  const plugins = JSON.parse(execFileSync('agy', ['plugin', 'list', '--json'], { encoding: 'utf8', windowsHide: true }));
  const installed = plugins.find(plugin => plugin.name === manifest.name);
  assert.equal(installed?.enabled, true);
  assert.equal(installed.version, manifest.version);
  assert.ok(installed.mcpServers?.includes('agy-computer-use'), 'Native Antigravity loader must discover the MCP server, not just its skill.');
}
const client = new Client({ name: 'installed-plugin-smoke', version: '0.2.0' });
const transport = new StdioClientTransport({ command: server.command, args: server.args, stderr: 'pipe' });
try {
  await client.connect(transport);
  const inventory = await client.listTools();
  assert.deepEqual(inventory.tools.map(tool => tool.name).sort(), ['act', 'batch', 'capture', 'find', 'observe', 'query', 'session', 'system', 'windows','run','context','help'].map(name => `desktop_${name}`).sort());
  const reply = await client.callTool({ name: 'desktop_system', arguments: { operation: 'status' } });
  assert.equal(reply.isError, false);
  assert.equal(JSON.parse(reply.content[0].text).accessibility, 'UIA3 (CUIAutomation8)');
  assert.equal(JSON.parse(reply.content[0].text).version, manifest.version);
  const windows = await client.callTool({ name: 'desktop_windows', arguments: { limit: 1 } });
  assert.equal(windows.isError, false);
  assert.ok(Array.isArray(JSON.parse(windows.content[0].text).windows));
  const runtime = await client.callTool({ name: 'desktop_run', arguments: { code: 'state.installed=true; return {installed:state.installed, platform:desktop.system({operation:"status"}).platform};' } });
  assert.equal(runtime.isError, false, runtime.content[0].text); assert.equal(JSON.parse(runtime.content[0].text).result.platform, 'windows');
  assert.equal((await client.listResources()).resources.length, 10);
  process.stdout.write('PASS installed plugin manifest/skill, all twelve tools, ten lazy guides, real UIA3 and isolated workflow runtime connect successfully. No evidence files saved.\n');
} finally { await client.close(); }
