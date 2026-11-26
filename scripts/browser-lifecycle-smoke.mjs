import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {NativeBridge} from '../src/native-bridge.mjs';
import {DesktopController} from '../src/desktop-controller.mjs';
import {browserFixture} from '../tests/browser-fixture.mjs';
import {execFileSync} from 'node:child_process';
import {existsSync} from 'node:fs';
import {basename} from 'node:path';

function ownedProfile() {
  // Inspect only direct browser children of this nonce-scoped test process; never print unrelated command lines.
  const script=`$items=@(Get-CimInstance Win32_Process -Filter 'ParentProcessId=${process.pid}' | Where-Object {$_.Name -in @('chrome.exe','msedge.exe') -and $_.CommandLine -match '--user-data-dir='});$items | ForEach-Object { $match=[regex]::Match($_.CommandLine,'(?:"--user-data-dir=([^"]+)"|--user-data-dir="([^"]+)"|--user-data-dir=([^\\s"]+))'); $profile=($match.Groups | Select-Object -Skip 1 | Where-Object Success | Select-Object -First 1).Value; [pscustomobject]@{pid=$_.ProcessId;profile=$profile;pipe=($_.CommandLine -match '--remote-debugging-pipe');port=($_.CommandLine -match '--remote-debugging-port')} } | ConvertTo-Json -Compress`;
  const data=JSON.parse(execFileSync('powershell.exe',['-NoProfile','-Command',script],{encoding:'utf8',windowsHide:true}));
  return Array.isArray(data)?data:[data];
}

process.env.AGY_DESKTOP_TEST_SCOPE=randomUUID().replaceAll('-','');
const channel=process.argv.includes('--edge')?'edge':'chrome';
const fixture=await browserFixture();const bridge=new NativeBridge();const controller=new DesktopController(bridge);
let checks=0;
const check=(value,message)=>{assert.ok(value,message);checks++;process.stdout.write(`PASS ${message}\n`);};
const call=async(name,args,allowError=false,options)=>{const reply=await controller.request(name,args,options);if(!allowError)assert.equal(reply.ok,true,JSON.stringify(reply));return reply;};
const start=async()=>call('browser_session',{operation:'start', connection:'isolated',browser:channel,headless:true});
const open=async(label)=>(await call('browser_tabs',{operation:'new',url:fixture.url+'/main?tab='+label})).data.tab_id;
try {
  await start();
  const profiles=ownedProfile();assert.equal(profiles.length,1);const profile=profiles[0];
  check(profile.pipe&&!profile.port&&basename(profile.profile).startsWith('playwright_chromiumdev_profile-')&&existsSync(profile.profile),'Windows process inspection proves a new temporary browser profile and pipe transport, with no debug port');
  const a=await open('Lifecycle-A'),b=await open('Lifecycle-B');
  const timed=await call('browser_act',{tab_id:a,action:'fill',locator:{selector:'#blocked'},text:'must never appear',timeout_ms:200},true);
  check(!timed.ok&&timed.error.code==='BROWSER_TIMEOUT'&&timed.error.details.delivery==='unknown','a real actionability timeout reports unknown delivery without replay');
  check((await call('browser_act',{tab_id:a,action:'fill',locator:{selector:'#message'},text:'must be blocked'},true)).error?.code==='RECONCILIATION_REQUIRED','uncertain browser delivery blocks later input');
  check((await call('browser_tabs',{operation:'reconcile',tab_id:a,recovery_note:'No fresh read yet'},true)).error?.code==='FRESH_READ_REQUIRED','a recovery note cannot replace actual post-failure inspection');
  await call('browser_read',{tab_id:a,operation:'text'});
  await call('browser_tabs',{operation:'reconcile',tab_id:a,recovery_note:'The rendered fixture remains unchanged after the hidden-field timeout.'});
  await call('browser_act',{tab_id:a,action:'click',locator:{selector:'#reveal'}});
  check((await call('browser_wait',{tab_id:a,condition:'value',locator:{selector:'#blocked'},equals:'not changed',timeout_ms:100})).data.verification==='passed','the timed-out input does not execute later when the field becomes visible');
  const local=controller.browser.tabs.get(a).page;
  await local.evaluate(()=>{document.querySelector('#blocked').style.display='none';}); // Fixture-only external user change, outside plugin request dispatch.
  const abort=new AbortController();
  const pending=call('browser_act',{tab_id:a,action:'fill',locator:{selector:'#blocked'},text:'cancelled input',timeout_ms:10000},true,{signal:abort.signal});
  await new Promise(resolve=>setTimeout(resolve,200));const cancelledAt=Date.now();abort.abort();
  const cancelled=await pending;
  check(cancelled.error?.code==='CANCELLED'&&Date.now()-cancelledAt<1500,'cancellation interrupts a real browser action wait promptly');
  check((await call('browser_read',{tab_id:a,operation:'text'},true)).error?.code==='TAB_CLOSED','cancellation closes the exact target tab so queued input cannot appear later');
  check((await call('browser_read',{tab_id:b,operation:'text'})).data.text.includes('Lifecycle-B')&&fixture.saved.length===0,'cancelling tab A preserves tab B and produces no form side effect');
  const helperWait=call('browser_wait',{tab_id:b,condition:'url',equals:fixture.url+'/never',timeout_ms:10000},true);
  await new Promise(resolve=>setTimeout(resolve,100));const nativeLostAt=Date.now(),ownBeforeHelperLoss=controller.browser.browser;
  controller.bridge.child.kill(); // Kill only this test's native helper, not the app or another plugin server.
  check((await helperWait).error?.code==='NATIVE_CONTROL_LOST'&&Date.now()-nativeLostAt<2000,'loss of the real native Escape helper interrupts an in-flight browser wait');
  check(!ownBeforeHelperLoss.isConnected()&&(await call('desktop_windows',{},true)).error?.code==='BROWSER_SCOPE_ACTIVE','native-helper failure closes the owned browser and retains the browser-only scope');
  await call('browser_session',{operation:'end'});await start();const disconnectedTab=await open('Disconnect-fixture');
  const nextProfile=ownedProfile()[0];
  await controller.browser.tabs.get(disconnectedTab).page.goto('data:text/html,<title>Fixture outside HTTP scope</title><p>must not be read by plugin</p>');
  check((await call('browser_read',{tab_id:disconnectedTab,operation:'text'},true)).error?.code==='INVALID_URL','an external navigation to a disallowed scheme cannot bypass the tab URL guard');
  const connected=controller.browser.browser;
  await connected.close(); // Independent test harness disconnects the actual owned browser.
  check(!existsSync(profile.profile)&&!existsSync(nextProfile.profile),'closing the actual browsers removes both owned temporary profile directories');
  check(!(await call('browser_session',{operation:'info'})).data.active,'external browser closure is detected by the session');
  check((await call('browser_read',{tab_id:disconnectedTab,operation:'text'},true)).error?.code==='BROWSER_DISCONNECTED','disconnected tab access fails instead of creating/reusing another browser');
  check((await call('desktop_windows',{},true)).error?.code==='BROWSER_SCOPE_ACTIVE','losing the browser connection never enables desktop fallback');
  await call('browser_session',{operation:'end'});await start();const c=await open('New-session');
  check(c!==b&&(await call('browser_read',{tab_id:b,operation:'text'},true)).error?.code==='TAB_NOT_OWNED','a fresh session cannot reuse old tab ids');
  const own=controller.browser.browser;await call('browser_session',{operation:'end'});
  check(!own.isConnected()&&!controller.browser.scoped,'session end actually closes its browser process connection and releases the task scope');
  check((await call('desktop_session',{operation:'info'})).ok,'desktop tools are available again only after explicit browser session end');
  const never=new AbortController();never.abort();
  check((await call('browser_session',{operation:'start', connection:'isolated',browser:channel,headless:true},true,{signal:never.signal})).error?.code==='CANCELLED'&&!controller.browser.active,'a pre-cancelled request cannot launch a browser');
  process.stdout.write(`Browser lifecycle smoke passed ${checks} checks: real ${channel}; no evidence files saved.\n`);
} finally {await controller.close();await fixture.close();}
