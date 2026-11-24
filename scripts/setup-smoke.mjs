import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { writeFileSync, readFileSync, unlinkSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const configPath = join(tmpdir(), `agy-mcp-config-${randomUUID()}.json`);
try {
  writeFileSync(configPath, JSON.stringify({
    mcpServers: { existing: { command: 'existing-server', args: ['--keep'], env: { SAMPLE: 'fixture-value' } } },
    extraSetting: { enabled: true }
  }));
  execFileSync('powershell.exe', ['-NoProfile', '-File', fileURLToPath(new URL('../setup.ps1', import.meta.url)), '-Register', '-ConfigPath', configPath], { stdio: 'pipe', windowsHide: true });
  const merged = JSON.parse(readFileSync(configPath, 'utf8'));
  assert.deepEqual(merged.mcpServers.existing, { command: 'existing-server', args: ['--keep'], env: { SAMPLE: 'fixture-value' } });
  assert.equal(merged.extraSetting.enabled, true);
  assert.ok(merged.mcpServers['agy-computer-use'].command.endsWith('node.exe'));
  assert.ok(merged.mcpServers['agy-computer-use'].args[0].endsWith('server.mjs'));
  process.stdout.write('PASS installer preserves existing servers and unrelated config. The real Antigravity configuration was not modified.\n');
} finally {
  if (existsSync(configPath)) unlinkSync(configPath);
}
