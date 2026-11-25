import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {execFileSync,spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {once} from 'node:events';
import {fileURLToPath} from 'node:url';
import {NativeBridge} from '../src/native-bridge.mjs';
import {DesktopController} from '../src/desktop-controller.mjs';
import {browserFixture} from '../tests/browser-fixture.mjs';

process.env.AGY_DESKTOP_TEST_SCOPE=randomUUID().replaceAll('-','');
execFileSync('dotnet',['build','tests/fixture/DesktopFixture.csproj','-c','Release','--nologo'],{cwd:fileURLToPath(new URL('../',import.meta.url)),stdio:'pipe',windowsHide:true});
const bridge=new NativeBridge(),controller=new DesktopController(bridge),fixture=await browserFixture();
let probe,lines,checks=0;
const check=(value,message)=>{assert.ok(value,message);checks++;process.stdout.write(`PASS ${message}\n`);};
const call=async(name,args,allowError=false)=>{const reply=await controller.request(name,args);if(!allowError)assert.equal(reply.ok,true,JSON.stringify(reply));return reply;};
try {
  await call('browser_session',{operation:'start',browser:'chrome',headless:false});
  const label='ESC-'+randomUUID();
  const tab=(await call('browser_tabs',{operation:'new',url:fixture.url+'/main?tab='+label})).data.tab_id;
  // Independent native observation is constrained to the nonce-titled browser created above.
  const windows=await bridge.request('windows',{query:label});assert.equal(windows.data.windows.length,1);
  const window=windows.data.windows[0];assert.ok(/chrome/i.test(window.app));
  assert.equal((await bridge.request('system',{operation:'focus_window',window_id:window.id})).ok,true);
  probe=spawn(fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe',import.meta.url)),['--control-probe',String(window.pid)],{stdio:['pipe','pipe','ignore'],windowsHide:true});
  lines=createInterface({input:probe.stdout});
  const inspect=async command=>{const result=once(lines,'line');probe.stdin.write(command+'\n');const [text]=await result;const data=JSON.parse(text);assert.ok(!data.error,JSON.stringify(data));return data;};
  const waiting=call('browser_wait',{tab_id:tab,condition:'url',equals:fixture.url+'/never',timeout_ms:10000},true);
  const ownedBrowser=controller.browser.browser;
  await new Promise(resolve=>setTimeout(resolve,150));const started=Date.now();
  check((await inspect('escape')).sent,'an independent guarded injector delivers Escape only while the nonce-owned browser is foreground');
  const stopped=await waiting;
  check(stopped.error?.code==='USER_STOPPED'&&Date.now()-started<2000,'the actual Windows Escape hook interrupts browser work and returns USER_STOPPED');
  check(!ownedBrowser.isConnected()&&!(await call('browser_session',{operation:'info'})).data.active,'Escape actually disconnects the real owned browser and closes the session');
  check((await call('desktop_system',{operation:'control_status'})).data.paused,'the browser stop retains the native/bridge pause latch');
  check(fixture.saved.length===0,'the stop test produces no form submission or external message');
  const held=await inspect('held');check(!held.ctrl&&!held.shift&&!held.left,'no modifier or mouse button remains held after browser stop');
  process.stdout.write(`Browser Escape smoke passed ${checks} checks; no evidence files saved.\n`);
} finally {lines?.close();probe?.kill();await controller.close();await fixture.close();}
