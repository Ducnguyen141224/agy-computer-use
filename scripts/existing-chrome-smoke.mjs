import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtempSync,readFileSync,existsSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,resolve,dirname,basename} from 'node:path';
import {randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {chromium} from 'playwright-core';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {browserFixture} from '../tests/browser-fixture.mjs';
import {desktopTestEnv} from '../tests/mcp-env.mjs';

// This fixture simulates a Chrome that was opened before the plugin. It never uses personal data.
const temp=resolve(tmpdir()),profile=mkdtempSync(join(temp,'agy-existing-smoke-'));
const visible=process.argv.includes('--visible');
const chrome=spawn(join(process.env.ProgramFiles,'Google','Chrome','Application','chrome.exe'),[
  '--remote-debugging-port=0','--remote-debugging-address=127.0.0.1',`--user-data-dir=${profile}`,
  '--no-first-run','--no-default-browser-check',...(visible?[]:['--headless=new']),'about:blank'
],{windowsHide:true,stdio:'ignore'});
let original,client,transport,checks=0;
const fixture=await browserFixture();
const auth=createServer((req,res)=>{
  res.setHeader('Content-Type','text/html');
  if(req.url==='/seed') {res.setHeader('Set-Cookie','agy_existing_login=fixture-authenticated; HttpOnly; Path=/');res.end('<title>Existing personal sentinel</title><input id="personal" value="leave this unchanged">');}
  else res.end('<title>Work login check</title><p id="auth">'+((req.headers.cookie??'').includes('agy_existing_login=fixture-authenticated')?'Existing login inherited':'Not authenticated')+'</p>');
});
await new Promise(resolve=>auth.listen(0,'127.0.0.1',resolve));const origin=`http://127.0.0.1:${auth.address().port}`;
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const check=(value,message)=>{assert.ok(value,message);checks++;process.stdout.write(`PASS ${message}\n`);};
try {
  const activePort=join(profile,'DevToolsActivePort');for(let i=0;i<100&&!existsSync(activePort);i++) await pause(50);
  assert.ok(existsSync(activePort),'Fixture Chrome did not expose its temporary test endpoint.');
  const port=Number(readFileSync(activePort,'utf8').split('\n')[0]);assert.ok(port>0&&port<65536);
  const endpoint=`http://127.0.0.1:${port}`;
  original=await chromium.connectOverCDP(endpoint,{noDefaults:true});const context=original.contexts()[0];
  const personal=context.pages()[0];await personal.goto(origin+'/seed');
  process.env.AGY_DESKTOP_TEST_SCOPE=randomUUID().replaceAll('-','');process.env.AGY_CHROME_CDP_TEST_ENDPOINT=endpoint;
  transport=new StdioClientTransport({command:process.execPath,args:[fileURLToPath(new URL('../src/server.mjs',import.meta.url))],env:desktopTestEnv(),stderr:'pipe'});
  client=new Client({name:'existing-chrome-smoke',version:'0.9.0'});await client.connect(transport);
  const call=async(name,args,allowError=false)=>{const reply=await client.callTool({name,arguments:args});const data=JSON.parse(reply.content[0].text);if(!allowError)assert.equal(reply.isError,false,JSON.stringify(data));return {...data,images:reply.content.filter(block=>block.type==='image')};};
  const idle=async()=>{await pause(300);const status=await call('desktop_system',{operation:'control_status'});assert.equal(status.rendered,false);assert.equal(status.active,false);return status;};
  const started=await call('browser_session',{operation:'start'});
  check(started.connection==='existing'&&started.personal_profile_attached&&started.active,'default start attaches to a Chrome that was already running');
  check((await call('browser_tabs',{operation:'list'})).tabs.length===0,'pre-existing personal tabs are not exposed by the plugin');
  check((await idle()).armed,'connecting/arming Escape does not show a blue border or pointer');
  const work=await call('browser_tabs',{operation:'new',url:origin+'/check'});
  const workPage=context.pages().find(page=>page.url()===origin+'/check');assert.ok(workPage);
  check(context.pages().includes(personal)&&original.isConnected(),'creating a work tab does not replace or close existing Chrome');
  const inherited=await call('browser_read',{tab_id:work.tab_id,operation:'text'});
  check(inherited.text.includes('Existing login inherited'),'a real HTTP-only cookie from the existing session is reused without copying it');
  await idle();
  const foreign=await context.newPage();await foreign.goto('about:blank');
  check((await call('browser_tabs',{operation:'list'})).tabs.length===1,'a tab opened independently while attached is not silently adopted');
  await call('browser_tabs',{operation:'navigate',tab_id:work.tab_id,url:fixture.url+'/main?tab=Existing'});
  await call('browser_read',{tab_id:work.tab_id,operation:'snapshot'});await idle();
  check(true,'DOM reads and the idle/model gap leave all AGY overlays hidden');
  const failed=await call('browser_act',{tab_id:work.tab_id,action:'fill',locator:{selector:'#blocked'},text:'not sent',timeout_ms:1200},true);
  check(failed.error?.code==='BROWSER_TIMEOUT','an actual browser input attempt has a bounded failure rather than hanging');
  await idle();
  await call('browser_read',{tab_id:work.tab_id,operation:'text'});
  await call('browser_tabs',{operation:'reconcile',tab_id:work.tab_id,recovery_note:'Fixture hidden field remained unchanged; the failed fill was not retried.'});
  await call('browser_act',{tab_id:work.tab_id,action:'fill',locator:{selector:'#message'},text:'Uses existing login'});
  await call('browser_act',{tab_id:work.tab_id,action:'click',locator:{selector:'#save'}});
  await call('browser_wait',{tab_id:work.tab_id,condition:'text',locator:{selector:'#status'},equals:'Saved: Uses existing login'});await idle();
  check(fixture.saved.some(item=>item.value==='Uses existing login'),'tab-bound input reaches the real HTTP fixture');
  await workPage.setViewportSize({width:2200,height:1200});
  await workPage.evaluate(()=>{const button=document.createElement('button');button.id='wide';button.textContent='Wide viewport test';Object.assign(button.style,{position:'fixed',left:'1900px',top:'950px',width:'200px',height:'80px'});button.onclick=()=>button.textContent='Wide click confirmed';document.body.append(button);});
  const image=await call('browser_capture',{tab_id:work.tab_id});
  check(image.width===1600&&image.height<=1600&&image.source_viewport.width===2200,'large existing viewport is bounded in RAM without changing its size');
  await call('browser_act',{tab_id:work.tab_id,action:'click_point',capture_id:image.capture_id,x:Math.round(2000*image.width/2200),y:Math.round(990*image.height/1200)});
  check(await workPage.locator('#wide').innerText()==='Wide click confirmed','image-pixel coordinates map back to the large real viewport correctly');
  await call('browser_session',{operation:'end'});
  check(original.isConnected()&&!personal.isClosed()&&!foreign.isClosed(),'end disconnects the plugin while Chrome and independently opened tabs stay alive');
  check(workPage.isClosed()&&await personal.locator('#personal').inputValue()==='leave this unchanged','end closes only agent-created tabs and preserves personal page content');
  await call('browser_session',{operation:'start'});const next=await call('browser_tabs',{operation:'new',url:origin+'/check'});
  check((await call('browser_read',{tab_id:next.tab_id,operation:'text'})).text.includes('Existing login inherited'),'ending and reconnecting preserves the existing login cookie');
  await call('desktop_system',{operation:'control_stop'},true);await pause(150);
  check(original.isConnected()&&!personal.isClosed()&&!foreign.isClosed(),'stop/ESC cleanup does not terminate the user Chrome or personal tabs');
  await call('browser_session',{operation:'end'});await client.close();client=null;
  check(chrome.exitCode===null&&existsSync(profile),'disconnect does not delete or take ownership of the existing profile');
  process.stdout.write(`Existing Chrome smoke passed ${checks} checks (${visible?'visible':'headless fixture'}); no evidence files saved.\n`);
} finally {
  await client?.close().catch(()=>{});await original?.close().catch(()=>{});
  if(chrome.exitCode===null) {chrome.kill();await new Promise(resolve=>chrome.once('exit',resolve));}
  await fixture.close();await new Promise(resolve=>auth.close(resolve));
  if(dirname(resolve(profile))!==temp||!basename(profile).startsWith('agy-existing-smoke-')) throw new Error('Refusing cleanup outside the exact temporary fixture profile.');
  for(let i=0;i<30&&existsSync(profile);i++){try{rmSync(profile,{recursive:true,force:true});}catch{await pause(100);}}
}
