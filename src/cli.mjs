#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { tools, validateCall } from './tools.mjs';
import { pipeRequest } from './ipc-client.mjs';

const argv = process.argv.slice(2);
const command = argv.shift() ?? 'help';
const options = {};
const positional = [];
let png = false, includeImage = false;
for (let index = 0; index < argv.length; index++) {
  const value = argv[index];
  if (value === '--png') { png = true; continue; }
  if (value === '--include-image') { includeImage = true; continue; }
  if (!value.startsWith('--')) { positional.push(value); continue; }
  const key = value.slice(2).replaceAll('-', '_');
  let raw = argv[index + 1];
  if (raw === undefined || raw.startsWith('--')) options[key] = true;
  else {
    index++;
    if (raw === 'true' || raw === 'false' || /^-?\d+(?:\.\d+)?$/.test(raw) || raw.startsWith('[') || raw.startsWith('{')) { try { raw = JSON.parse(raw); } catch { /* Validation reports an invalid typed argument. */ } }
    options[key] = raw;
  }
}

const actionAliases = { click: 'click', 'right-click': 'right_click', 'double-click': 'double_click', 'triple-click': 'triple_click', hover: 'hover', drag: 'drag', type: 'type_text', 'set-value': 'set_value', clear: 'clear', focus: 'focus', select: 'select', toggle: 'toggle', check: 'check', uncheck: 'uncheck', expand: 'expand', collapse: 'collapse', scroll: 'scroll', 'scroll-to': 'scroll_into_view', press: 'press_key', 'mouse-click': 'click_point', 'mouse-move': 'mouse_move', 'mouse-wheel': 'mouse_wheel', 'mouse-drag': 'drag_point' };
const systemAliases = { status: 'status', permissions: 'permissions', version: 'version', launch: 'launch', 'close-app': 'close_app', 'close-window': 'close_window', 'list-apps': 'list_apps', 'list-displays': 'list_displays', 'focus-window': 'focus_window', 'resize-window': 'resize', 'move-window': 'move', minimize: 'minimize', maximize: 'maximize', restore: 'restore', 'clipboard-get': 'clipboard_get', 'clipboard-set': 'clipboard_set', 'clipboard-clear': 'clipboard_clear', 'list-surfaces': 'list_surfaces', 'open-system-surface': 'open_surface', 'list-notifications': 'list_notifications', 'dismiss-notification': 'dismiss_notification', 'dismiss-all-notifications': 'dismiss_all_notifications', 'notification-action': 'notification_action', 'cursor-config': 'cursor_config', 'cursor-status': 'cursor_status', 'cursor-remove': 'cursor_remove' };

try {
  if (command === 'help' || command === '--help') {
    process.stdout.write('AGY computer use: windows/observe/find/act/query/system/batch/session/capture [--json JSON].\nAliases: snapshot, list-windows, click REF, set-value REF TEXT, press REF Ctrl+A, launch APP, clipboard-get, resize-window --window-id ID --width N --height N.\nCLI refs live in a current-user-only memory daemon (idle timeout 5 minutes). daemon-stop clears its state.\nCapture omits image bytes by default; --png writes PNG bytes to stdout, --include-image includes base64. Nothing is recorded to disk.\n');
  } else if (command === 'daemon-stop') {
    process.stdout.write(JSON.stringify(await pipeRequest('shutdown', {}, { ...(options.pipe ? { pipe: options.pipe } : {}), start: false })) + '\n');
  } else {
    let method = command;
    let args = options.json === undefined ? { ...options } : typeof options.json === 'string' ? JSON.parse(options.json) : options.json;
    const pipe = options.pipe;
    delete args.json; delete args.pipe;
    if (command === 'list-windows') method = 'windows';
    if (command === 'snapshot') method = 'observe';
    if (command === 'screenshot') method = 'capture';
    if (actionAliases[command]) {
      method = 'act'; args.action = actionAliases[command];
      if (positional[0]) args.ref = positional[0];
      if (['type', 'set-value'].includes(command) && positional[1] !== undefined) args.text = positional[1];
      if (command === 'select' && positional[1] !== undefined) args.option = positional[1];
      if (command === 'press' && positional[1]) args.key = positional[1];
      if (command === 'drag' && positional[1]) args.to_ref = positional[1];
    }
    if (systemAliases[command]) {
      method = 'system'; args.operation = systemAliases[command];
      if (command === 'launch' && positional[0]) args.app = positional[0];
      if (command === 'clipboard-set' && positional[0] !== undefined) args.text = positional[0];
    }
    if (['get', 'is', 'wait'].includes(command)) { method = 'query'; args.operation = command; if (positional[0]) args.ref = positional[0]; }
    if (command === 'session') { args.operation ??= positional[0] ?? 'info'; if (positional[1]) args.session_id = positional[1]; }
    if (options.stdin) { delete args.stdin; args.text = readFileSync(0, 'utf8'); }
    const tool = tools.find(entry => entry.name === `desktop_${method}`);
    if (!tool) throw new Error('Unknown CLI command. Run with help.');
    if (['run', 'context', 'help'].includes(method)) throw new Error('The persistent JavaScript runtime and task context require the MCP server. Native CLI commands remain available.');
    validateCall(tool, args);
    const nativeArgs = method === 'batch' ? { ...args, commands: args.commands.map(entry => ({ method: entry.tool.slice(8), params: entry.arguments })) } : args;
    const reply = await pipeRequest(method, nativeArgs, pipe ? { pipe } : {});
    if (png && reply.ok && reply.data.image) process.stdout.write(Buffer.from(reply.data.image, 'base64'));
    else {
      if (!includeImage) {
        const omit = value => { if (!value || typeof value !== 'object') return; if (typeof value.image === 'string') { delete value.image; value.image_omitted = true; } for (const item of Object.values(value)) omit(item); };
        omit(reply.data);
      }
      process.stdout.write(JSON.stringify(reply) + '\n');
    }
    if (!reply.ok || reply.data?.verification === 'failed' || reply.data?.results?.some(result => !result.ok)) process.exitCode = 1;
  }
} catch (error) {
  process.stdout.write(JSON.stringify({ ok: false, error: { code: 'CLI_ERROR', message: error.message, retry: 'observe_first' } }) + '\n');
  process.exitCode = 1;
}
