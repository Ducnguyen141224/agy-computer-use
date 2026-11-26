import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { guides } from '../src/guides.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const target = join(root, 'plugin');
const metadata = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
const write = (path, text) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, text, 'utf8'); };
write(join(target, 'plugin.json'), JSON.stringify({
  $schema: 'https://agent-plugins.org/schemas/1.0.0/plugin.schema.json',
  name: 'agy-computer-use', version: metadata.version,
  description: 'Windows UIA3 and work tabs in your running Chrome; guarded refs, input-only indicators and RAM-only captures.'
}, null, 2) + '\n');
write(join(target, 'mcp.json'), JSON.stringify({ $schema: 'https://agent-plugins.org/schemas/1.0.0/mcp.schema.json', mcpServers: {
  'agy-computer-use': { type: 'stdio', command: 'node', args: [join(root, 'src', 'server.mjs')] }
} }, null, 2) + '\n');
// Native Antigravity loaders still discover mcp_config.json when the Agent
// Plugins feature flag is off. The spec loader uses mcp.json instead.
write(join(target, 'mcp_config.json'), JSON.stringify({ mcpServers: {
  'agy-computer-use': { command: 'node', args: [join(root, 'src', 'server.mjs')] }
} }, null, 2) + '\n');
write(join(target, 'skills', 'agy-computer-use', 'SKILL.md'), readFileSync(join(root, '.agent', 'skills', 'agy-computer-use', 'SKILL.md'), 'utf8'));
for (const [topic, instructions] of Object.entries(guides)) write(join(target, 'skills', 'agy-computer-use', 'references', topic + '.md'), instructions + '\n');
write(join(target, 'README.md'), 'AGY Computer Use for Windows\n\nThis local plugin uses the built runtime at ' + root + '. Keep that project directory in place. After moving it, rebuild and reinstall the plugin. No desktop recordings or evidence files are produced.\n');
process.stdout.write('Built local Antigravity plugin at ' + target + '\n');
