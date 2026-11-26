import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { browserFixture } from '../tests/browser-fixture.mjs';
import { desktopTestEnv } from '../tests/mcp-env.mjs';
import { pixel } from '../tests/png.mjs';

const channel = process.argv.includes('--edge') ? 'edge' : 'chrome';
process.env.AGY_DESKTOP_TEST_SCOPE = randomUUID().replaceAll('-', '');
const fixture = await browserFixture();

// Tiny cookie server on localhost to verify persistent work profiles across real browser restarts.
const cookieServer = createServer((req, res) => {
  res.setHeader('Content-Type', 'text/html; charset=utf-8');
  if (req.url === '/login') {
    res.setHeader('Set-Cookie', 'agy_work_token=persisted-ok; Path=/; Max-Age=3600');
    res.end('<!doctype html><title>Work Login</title><output id="auth">Logged in</output>');
    return;
  }
  const hasCookie = (req.headers.cookie ?? '').includes('agy_work_token=persisted-ok');
  res.end(`<!doctype html><title>Work Check</title><output id="auth">${hasCookie ? 'Session persisted' : 'Anonymous'}</output>`);
});
await new Promise(resolve => cookieServer.listen(0, '127.0.0.1', resolve));
const cookieUrl = `http://127.0.0.1:${cookieServer.address().port}`;

const client = new Client({ name: 'codex-browser-smoke', version: '0.8.0' });
const transport = new StdioClientTransport({ env: desktopTestEnv(), command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs', import.meta.url))], stderr: 'pipe' });
let checks = 0;
const check = (cond, msg) => { assert.ok(cond, msg); checks++; process.stdout.write(`PASS ${msg}\n`); };
const call = async (name, args, allowError = false) => {
  const reply = await client.callTool({ name, arguments: args });
  const data = JSON.parse(reply.content[0].text);
  if (!allowError) assert.equal(reply.isError, false, JSON.stringify(data));
  return { ...data, images: reply.content.filter(b => b.type === 'image') };
};

const profileName = `codex-e2e-${channel}-${Date.now()}`;
try {
  await client.connect(transport);

  // 1. Headless session + compact include_text:false snapshot timing & b1 refs
  await call('browser_session', { operation: 'start', connection:'isolated', browser: channel, headless: true });
  const tab = await call('browser_tabs', { operation: 'new', url: `${fixture.url}/main?tab=Codex` });
  const t0 = performance.now();
  const snap = await call('browser_read', { tab_id: tab.tab_id, operation: 'snapshot', include_text: false, limit: 80 });
  const snapMs = Math.round(performance.now() - t0);
  check(snap.text === '' && snap.text_omitted === true && snap.items.length >= 10, `compact browser_read snapshot (include_text:false) returned ${snap.items.length} items in ${snapMs}ms`);
  check(snap.items.every(i => /^b\d+$/.test(i.ref) && !('visible' in i) && !('disabled' in i) && Number.isInteger(i.bounds.x)), 'snapshot uses compact b1.. refs, omits defaults, and rounds integer bounds');

  // 2. One-loop browser_act observe_after:'diff'
  const msg = snap.items.find(i => i.name === 'Message');
  const save = snap.items.find(i => i.name === 'Save');
  assert.ok(msg && save);
  await call('browser_act', { tab_id: tab.tab_id, action: 'fill', ref: msg.ref, text: 'Codex One-Loop' });
  const clicked = await call('browser_act', { tab_id: tab.tab_id, action: 'click', ref: save.ref, observe_after: 'diff', settle_ms: 120 });
  check(clicked.observation?.mode === 'diff', `browser_act observe_after:'diff' returned inline DOM delta in 1 call`);
  await call('browser_wait', { tab_id: tab.tab_id, condition: 'text', locator: { selector: '#status' }, equals: 'Saved: Codex One-Loop', timeout_ms: 3000 });
  check(fixture.saved.some(s => s.value === 'Codex One-Loop'), 'one-loop action produced actual HTTP save effect');

  // 3. Real browser_capture { annotate: true } Set-of-Marks + pixel check + click_mark
  const shot = await call('browser_capture', { tab_id: tab.tab_id, annotate: true, max_marks: 30 });
  check(shot.images.length === 1 && Array.isArray(shot.marks) && shot.marks.length >= 5, `annotated browser_capture produced ${shot.marks.length} Set-of-Marks items`);
  const checkMark = shot.marks.find(m => m.role === 'checkbox');
  assert.ok(checkMark, 'checkbox present in Set-of-Marks');
  // Verify at least some pixels in the PNG carry #31A8FF (49,168,255) or #085CA7 (8,92,167) badge colors
  const snapBox = snap.items.find(i => i.role === 'checkbox').bounds;
  const edgePx = pixel(shot.images[0].data, Math.max(0, snapBox.x), Math.max(0, snapBox.y));
  check(edgePx[0] === 49 && edgePx[1] === 168 && edgePx[2] === 255, `annotated Chromium PNG contains actual #31A8FF outline on checkbox mark #${checkMark.mark}`);
  await call('browser_act', { tab_id: tab.tab_id, action: 'click_mark', capture_id: shot.capture_id, mark: checkMark.mark });
  const checkedWait = await call('browser_wait', { tab_id: tab.tab_id, condition: 'checked', locator: { selector: '#check' }, equals: true, timeout_ms: 2000 });
  check(checkedWait.verification === 'passed', `browser_act click_mark clicked checkbox mark #${checkMark.mark} cleanly`);

  // 4. Unified desktop_run using browser.* synchronous QuickJS APIs + desktop isolation check
  const wf = await call('desktop_run', {
    code: `
      const s = browser.read({ tab_id: ${JSON.stringify(tab.tab_id)}, operation: "snapshot", include_text: false });
      const field = browser.one(s, { role: "textbox", name: "Message" });
      const btn = browser.one(s, { role: "button", name: "Save" });
      browser.act({ tab_id: ${JSON.stringify(tab.tab_id)}, action: "fill", ref: field.ref, text: "QuickJS Browser Workflow" });
      browser.act({ tab_id: ${JSON.stringify(tab.tab_id)}, action: "click", ref: btn.ref });
      const w = browser.wait({ tab_id: ${JSON.stringify(tab.tab_id)}, condition: "text", locator: { selector: "#status" }, equals: "Saved: QuickJS Browser Workflow", timeout_ms: 3000 });
      state.lastSaved = w.value;
      return { verified: w.verification, saved: state.lastSaved };
    `
  });
  check(wf.execution_status === 'completed' && wf.result?.verified === 'passed' && wf.result?.saved === 'Saved: QuickJS Browser Workflow', 'desktop_run executed multi-step browser.* QuickJS workflow in 1 MCP call');
  const escaped = await call('desktop_run', { code: 'return desktop.windows({});' }, true);
  check(escaped.execution_status === 'failed' && escaped.error?.code === 'BROWSER_SCOPE_ACTIVE', 'desktop.* remains strictly blocked inside browser-scoped desktop_run');

  // 5. Human-in-the-loop handoff & resume_handoff
  const preHandoff = await call('browser_read', { tab_id: tab.tab_id, operation: 'snapshot', include_text: false });
  const oldRef = preHandoff.items.find(i => i.name === 'Message').ref;
  const ho = await call('browser_session', { operation: 'handoff', reason: 'Simulated 2FA check' });
  check(ho.handoff?.active === true, 'browser_session handoff activates user takeover mode');
  const blockedAct = await call('browser_act', { tab_id: tab.tab_id, action: 'click', ref: oldRef }, true);
  check(blockedAct.error?.code === 'USER_HANDOFF_ACTIVE', 'browser_act is blocked while user handoff is active');
  const allowedRead = await call('browser_read', { tab_id: tab.tab_id, operation: 'text' });
  check(allowedRead.text.includes('Browser fixture Codex'), 'read-only inspection remains available during handoff');
  const resumed = await call('browser_session', { operation: 'resume_handoff' });
  check(resumed.handoff_resumed === true && resumed.handoff === null, 'resume_handoff restores control');
  const staleAfterHandoff = await call('browser_act', { tab_id: tab.tab_id, action: 'click', ref: oldRef }, true);
  check(staleAfterHandoff.error?.code === 'STALE_BROWSER_REF', 'pre-handoff refs are invalidated on resume_handoff');

  await call('browser_session', { operation: 'end' });

  // 6. Opt-in isolated persistent work profile across real browser restarts
  const pStart1 = await call('browser_session', { operation: 'start', connection:'isolated', browser: channel, headless: true, profile: profileName });
  check(pStart1.persistent_profile === true && pStart1.personal_profile_attached === false && pStart1.tab_count === 0, `started isolated persistent profile '${profileName}' cleanly`);
  const pTab1 = await call('browser_tabs', { operation: 'new', url: `${cookieUrl}/login` });
  check((await call('browser_tabs', { operation: 'list' })).tabs.length === 1, 'persistent profile reuses starter page so tab_count stays 1');
  check((await call('browser_read', { tab_id: pTab1.tab_id, operation: 'text' })).text.includes('Logged in'), 'persistent profile set HTTP cookie in session 1');
  await call('browser_session', { operation: 'end' });

  const listed = await call('browser_session', { operation: 'list_profiles' });
  check(listed.profiles.includes(profileName), 'list_profiles reports saved isolated profile');

  await call('browser_session', { operation: 'start', connection:'isolated', browser: channel, headless: true, profile: profileName });
  const pTab2 = await call('browser_tabs', { operation: 'new', url: `${cookieUrl}/check` });
  const checkText = await call('browser_read', { tab_id: pTab2.tab_id, operation: 'text' });
  check(checkText.text.includes('Session persisted'), `real ${channel} persistent profile retained auth cookie across full browser restart`);
  await call('browser_session', { operation: 'end' });

  const cleared = await call('browser_session', { operation: 'clear_profile', profile: profileName });
  check(cleared.cleared === true && cleared.existed === true, 'clear_profile removes isolated profile');
  check(!(await call('browser_session', { operation: 'list_profiles' })).profiles.includes(profileName), 'cleared profile no longer listed');

  process.stdout.write(`Codex browser smoke passed ${checks} checks on real ${channel}.\n`);
} finally {
  await call('browser_session', { operation: 'end' }, true).catch(() => {});
  await call('browser_session', { operation: 'clear_profile', profile: profileName }, true).catch(() => {});
  await client.close().catch(() => {});
  await fixture.close();
  await new Promise(resolve => cookieServer.close(resolve));
}
