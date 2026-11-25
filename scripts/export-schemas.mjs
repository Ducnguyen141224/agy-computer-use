import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { tools } from '../src/tools.mjs';

const schemas = Object.fromEntries(tools.filter(tool => !['desktop_run','desktop_context','desktop_help'].includes(tool.name)).map(tool => [tool.name.slice(8), tool.inputSchema]));
schemas.batch = {
  type: 'object', additionalProperties: false, required: ['commands'], properties: {
    commands: { type: 'array', minItems: 1, maxItems: 15, items: { type: 'object', additionalProperties: false, required: ['method', 'params'], properties: { method: { type: 'string', enum: ['windows', 'observe', 'find', 'act', 'query', 'system', 'capture', 'read'] }, params: { type: 'object', additionalProperties: true } } } },
    stop_on_error: { type: 'boolean' }, timeout_ms: { type: 'integer', minimum: 100, maximum: 10000 }
  }
};
mkdirSync(dirname(process.argv[2]), { recursive: true });
writeFileSync(process.argv[2], JSON.stringify(schemas));
