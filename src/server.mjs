import metadata from '../package.json' with { type: 'json' };
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { CallToolRequestSchema, ListToolsRequestSchema, ListResourcesRequestSchema, ReadResourceRequestSchema } from '@modelcontextprotocol/sdk/types.js';
import { NativeBridge } from './native-bridge.mjs';
import { tools, instructions, toolResult } from './tools.mjs';
import { DesktopController } from './desktop-controller.mjs';
import { guides } from './guides.mjs';

const bridge = new NativeBridge();
const controller = new DesktopController(bridge);
const server = new Server({ name: 'agy-computer-use', version: metadata.version }, { capabilities: { tools: {}, resources: {} }, instructions });
server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools }));
server.setRequestHandler(ListResourcesRequestSchema, async () => ({ resources: Object.keys(guides).map(topic => ({ uri: `agy://guides/${topic}`, name: `${topic} guide`, mimeType: 'text/plain', description: `Load the ${topic} instructions only when needed.` })) }));
server.setRequestHandler(ReadResourceRequestSchema, async request => {
  const topic = request.params.uri.replace(/^agy:\/\/guides\//, '');
  if (request.params.uri !== `agy://guides/${topic}` || !Object.hasOwn(guides, topic)) throw new Error('Unknown computer-use guide.');
  return { contents: [{ uri: request.params.uri, mimeType: 'text/plain', text: guides[topic] }] };
});
server.setRequestHandler(CallToolRequestSchema, async (request, extra) => {
  const tool = tools.find(entry => entry.name === request.params.name);
  if (!tool) return toolResult({ ok: false, error: { code: 'UNKNOWN_TOOL', message: 'Unknown desktop tool.' } });
  try {
    const args = request.params.arguments ?? {};
    const reply = await controller.request(tool.name, args, { signal: extra.signal });
    return toolResult(reply);
  } catch (error) {
    return toolResult({ ok: false, error: { code: error.code ?? 'INVALID_ARGUMENT', message: error.message, details: error.details } });
  }
});

const shutdown = async () => { await controller.close(); await server.close(); };
process.once('SIGINT', shutdown);
process.once('SIGTERM', shutdown);
process.stdin.once('end', () => void controller.close().catch(() => {}));
try {
  await server.connect(new StdioServerTransport());
} catch {
  await controller.close().catch(() => {});
  process.stderr.write('Computer use MCP failed to start. Check Node.js and dependencies.\n');
  process.exitCode = 1;
}
