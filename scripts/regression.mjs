import { spawn } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';
import { join } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const node = (label, script, ...args) => ({ label, command: process.execPath, args: [script, ...args] });
const suites = [
  { label: 'unit and transport', command: process.execPath, args: ['--test', ...readdirSync(join(root, 'tests')).filter(name => name.endsWith('.test.mjs')).map(name => join('tests', name))] },
  ...['desktop', 'advanced', 'provider', 'capture', 'drag', 'launch', 'presentation', 'lifecycle', 'workflow', 'shell', 'cli'].map(name => node(name, `scripts/${name}-smoke.mjs`)),
  { label: 'C ABI', command: 'dotnet', args: ['run', '--project', 'tests/ffi/FfiSmoke.csproj', '-c', 'Release', '--', '.'] },
  { label: 'notification parser', command: 'dotnet', args: ['run', '--project', 'tests/notifications/NotificationSmoke.csproj', '-c', 'Release', '--', '.'] },
  { label: 'real shell notifications', command: 'dotnet', args: ['run', '--project', 'tests/notifications/NotificationSmoke.csproj', '-c', 'Release', '--', '.', '--shell'] },
  { label: 'Chromium UI and CDP', command: 'dotnet', args: ['run', '--project', 'tests/cdp/CdpSmoke.csproj', '-c', 'Release', '--', join(process.env.ProgramFiles, 'Google', 'Chrome', 'Application', 'chrome.exe'), '--ui'] },
  { label: 'Edge headless CDP', command: 'dotnet', args: ['run', '--project', 'tests/cdp/CdpSmoke.csproj', '-c', 'Release', '--', join(process.env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe')] },
  node('installer merge', 'scripts/setup-smoke.mjs'),
  node('installed plugin', 'scripts/plugin-smoke.mjs', join(homedir(), '.gemini', 'config', 'plugins', 'agy-computer-use'))
];
let passed = 0;
for (const suite of suites) {
  process.stdout.write(`RUN ${suite.label}\n`);
  const result = await new Promise(resolve => {
    const child = spawn(suite.command, suite.args, { cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = ''; let settled = false;
    const collect = chunk => { output += chunk.toString(); if (output.length > 2000000) output = output.slice(-2000000); };
    child.stdout.on('data', collect); child.stderr.on('data', collect);
    child.on('error', error => { if (!settled) { settled = true; resolve({ code: -1, output: error.message }); } });
    child.on('close', code => { if (!settled) { settled = true; resolve({ code, output }); } });
  });
  if (result.code !== 0) { process.stderr.write(result.output + '\n'); throw new Error(`Regression failed in ${suite.label}; no subsequent desktop suite was started.`); }
  const lines = result.output.split(/\r?\n/).filter(line => /passed:|^PASS|^ℹ pass/.test(line));
  process.stdout.write(`PASS ${suite.label}: ${lines.at(-1) ?? 'completed with exit code 0'}\n`);
  passed++;
}
process.stdout.write(`All ${passed} regression suites passed. Results and media were kept in RAM; no test report was written.\n`);
