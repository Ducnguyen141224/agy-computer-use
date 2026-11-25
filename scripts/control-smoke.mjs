import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { NativeBridge } from '../src/native-bridge.mjs';
import { pixel } from '../tests/png.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
execFileSync('dotnet', ['build','tests/fixture/DesktopFixture.csproj','-c','Release','--nologo'], { cwd: root, stdio: 'pipe', windowsHide: true });
const executable = fileURLToPath(new URL('../tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe', import.meta.url));
const title = `AGY Control ${Date.now()}`;
const fixture = spawn(executable, [title], { stdio: 'ignore' });
const probe = spawn(executable, ['--control-probe', String(fixture.pid)], { stdio: ['pipe','pipe','ignore'], windowsHide: true });
const lines = createInterface({ input: probe.stdout });
const inspect = async command => { const result = once(lines,'line'); probe.stdin.write(command+'\n'); const [line] = await result; const data = JSON.parse(line); assert.ok(!data.error, line); return data; };
const client = new Client({ name: 'control-smoke', version: '0.5.0' });
const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('../src/server.mjs',import.meta.url))], stderr: 'pipe' });
const competitor = new NativeBridge();
let checks = 0;
const check = (condition, label) => { assert.ok(condition,label); checks++; process.stdout.write(`PASS ${label}\n`); };
const delay = milliseconds => new Promise(resolve=>setTimeout(resolve,milliseconds));
const waitProbe = async (command, predicate) => { const deadline = Date.now()+4500; let value; do { value = await inspect(command); if (predicate(value)) return value; await delay(25); } while (Date.now()<deadline); assert.fail(`Probe condition timed out: ${JSON.stringify(value)}`); };
const call = async (name,args,allowError=false) => { const reply = await client.callTool({name,arguments:args}); const data=JSON.parse(reply.content[0].text); if(!allowError) assert.equal(reply.isError,false,JSON.stringify(data)); return data; };
try {
  await client.connect(transport);
  await competitor.request('system',{operation:'status'}); // Retain the cross-helper RAM stop handle.
  check(!(await call('desktop_system',{operation:'control_status'})).rendered,'indicator is hidden before control starts');
  const background = (await inspect('visual')).borders;
  let window_id = (await call('desktop_query',{operation:'window_wait',query:title,timeout_ms:5000})).windows.find(w=>w.pid===fixture.pid).id;
  const find = async name => { const data=await call('desktop_find',{window_id,name,...(name==='Message'?{role:'edit'}:{}),include_bounds:true}); assert.equal(data.items.length,1); return data.items[0]; };
  let message = await find('Message');
  await call('desktop_act',{action:'focus_window',window_id});
  await call('desktop_act',{action:'focus',ref:message.ref});
  // Keep ownership while the independent GDI observer initializes; an idle indicator
  // is deliberately allowed to disappear after 1.8s between ordinary tool calls.
  const visualHold = call('desktop_run',{code:'desktop.query({operation:"time_wait",timeout_ms:8000}); return true;',timeout_ms:11000});
  visualHold.catch(()=>{}); // Preserve the observer's first error during fixture teardown.
  // Layered-window pixels can be dimmed by the compositor. Verify blue hue and
  // a change from the independently captured background, rather than raw SDR intensity.
  const visual = await waitProbe('visual',v=>v.rendered && v.borders.every((rgb,index)=>rgb[2]>40 && Math.abs(rgb[0]/rgb[2]-59/246)<0.08 && Math.abs(rgb[1]/rgb[2]-130/246)<0.08 && rgb.reduce((sum,n,i)=>sum+Math.abs(n-background[index][i]),0)>15));
  check(visual.borders.length>0,'blue border is present in real pixels on every attached display');
  check(visual.click_through && visual.no_activate && visual.foreground===fixture.pid,'indicator is click-through and never takes foreground focus');
  await visualHold;
  check(!(await call('desktop_windows',{query:'AGY '})).windows.some(w=>['AGY control indicator','AGY animated pointer'].includes(w.title)),'neither indicator nor animated pointer can be selected as an application target');
  check(!(await call('desktop_system',{operation:'cursor_status'})).profiles.length,'default presentation requires no configured cursor profile');
  await call('desktop_act',{action:'press_key',ref:message.ref,key:'Escape'});
  check(!(await call('desktop_system',{operation:'control_status'})).paused,'the agent can send Escape to an app without triggering its own stop');
  const pad = await find('Gesture pad'); const log = await find('Gesture log');
  await call('desktop_act',{action:'click',ref:pad.ref,modifiers:['ctrl']});
  check((await call('desktop_query',{operation:'get',ref:log.ref,property:'value'})).value.includes('down:Left'),'real modified click passes through the default pointer overlay');
  await call('desktop_act',{action:'hover',ref:pad.ref});
  await call('desktop_query',{operation:'time_wait',timeout_ms:550});
  const cursorRegion = {x:pad.bounds.x+Math.floor(pad.bounds.width/2)-8,y:pad.bounds.y+Math.floor(pad.bounds.height/2)-8,width:48,height:50};
  const capture = await client.callTool({name:'desktop_capture',arguments:{window_id,region:cursorRegion,mode:'screen',max_dimension:200}});
  assert.equal(capture.isError,false,capture.content[0].text);
  const cursorImage=capture.content.find(item=>item.type==='image').data;
  let black=0,white=0,magenta=0;
  for(let y=1;y<47;y+=2) for(let x=1;x<45;x+=2) { const rgb=pixel(cursorImage,x,y); if(rgb.slice(0,3).every(n=>n>=10&&n<27))black++; if(rgb.slice(0,3).every(n=>n>240))white++; if(rgb[0]>180&&rgb[1]<80&&rgb[2]>180)magenta++; }
  check(black>25 && white>3,'default arrow is black with a white outline in actual display pixels');
  check(magenta===0,'per-pixel alpha leaves no magenta color-key fringe around the pointer');
  check((await inspect('visual')).pointer_visible,'the small animated pointer is independently visible before physical input');
  await call('desktop_act',{action:'click',ref:pad.ref,modifiers:['ctrl']});
  const frameBefore=(await call('desktop_system',{operation:'control_status'})).presentation;
  const save=await find('Save');
  await call('desktop_run',{code:`for(let i=0;i<12;i++){desktop.act({action:'hover',ref:i%2?${JSON.stringify(pad.ref)}:${JSON.stringify(save.ref)}}); desktop.query({operation:'time_wait',timeout_ms:180});} return true;`,max_steps:30,timeout_ms:10000});
  const frameAfter=(await call('desktop_system',{operation:'control_status'})).presentation;
  check(frameAfter.border_paints===frameBefore.border_paints,'pointer animation never repaints the static fullscreen border');
  check(frameAfter.pointer.uploaded_frames>frameBefore.pointer.uploaded_frames+25 && frameAfter.pointer.upload_width*frameAfter.pointer.upload_height===112*112,'animation updates only a small 112x112 alpha surface');
  check(frameAfter.pointer.high_resolution_clock && frameAfter.pointer.sample_count>20,'high-resolution pacing submits a measured sequence of animation frames');
  await call('desktop_run',{code:`for(let i=0;i<10;i++)desktop.act({action:'hover',ref:${JSON.stringify(pad.ref)}}); return true;`,timeout_ms:8000});
  const stable=(await call('desktop_system',{operation:'control_status'})).presentation.pointer;
  check(!stable.motion_active,'repeated commands at the same point do not restart pointer travel');
  process.stdout.write(`Motion submissions (not display FPS): ${JSON.stringify(frameAfter.pointer)}\n`);
  if (process.argv.includes('--preview')) {
    process.stdout.write('PREVIEW_READY: AGY Control fixture; Escape stops the active workflow.\n');
    const preview = await call('desktop_run',{code:'desktop.query({operation:"time_wait",timeout_ms:10000}); desktop.query({operation:"time_wait",timeout_ms:10000}); desktop.query({operation:"time_wait",timeout_ms:5000}); return true;',timeout_ms:30000},true);
    process.stdout.write(JSON.stringify({preview:preview.error??preview.error?.code??preview.execution_status})+'\n');
    await call('desktop_system',{operation:'control_resume'});
    window_id = (await call('desktop_windows',{query:title})).windows.find(w=>w.pid===fixture.pid).id; message=await find('Message');
  }
  await call('desktop_act',{action:'set_value',ref:message.ref,text:'initial'});
  const started = Date.now();
  const running = call('desktop_run',{code:`desktop.act({action:'set_value',ref:${JSON.stringify(message.ref)},text:'started'}); desktop.query({operation:'time_wait',timeout_ms:9000}); desktop.act({action:'set_value',ref:${JSON.stringify(message.ref)},text:'must not run'});`,timeout_ms:12000},true);
  await waitProbe('read',v=>v.value==='started');
  check((await inspect('escape')).sent,'independent Escape input reaches the global stop hook');
  const stopped = await running;
  check((stopped.error?.code==='USER_STOPPED') && Date.now()-started<3000,`Escape interrupts an in-flight native wait rather than waiting for its deadline: ${JSON.stringify({error:stopped.error,elapsed:Date.now()-started})}`);
  check((await inspect('read')).value==='started','the next workflow mutation was not sent after Escape');
  const hiddenByEscape=await inspect('visual');
  check(!hiddenByEscape.rendered&&!hiddenByEscape.pointer_visible,'Escape immediately removes both the blue border and the separate pointer window');
  check((await call('desktop_system',{operation:'control_status'})).paused,'MCP stop latch survives helper termination/restart');
  const blocked = await call('desktop_act',{action:'set_value',ref:message.ref,text:'blocked'},true);
  check(blocked.error?.code==='USER_STOPPED' && (await inspect('read')).value==='started','new input stays blocked until explicit resume');
  const cross = await competitor.request('act',{action:'focus_window',window_id:'not-an-app'});
  check(!cross.ok && cross.error.code==='USER_STOPPED','another live helper respects the shared user stop latch');
  const noAutoResume = await call('desktop_run',{code:'desktop.system({operation:"control_resume"});'},true);
  check(noAutoResume.error?.code==='USER_STOPPED','a new workflow cannot clear the stop latch');
  await call('desktop_system',{operation:'control_resume'});
  window_id = (await call('desktop_windows',{query:title})).windows.find(w=>w.pid===fixture.pid).id; message=await find('Message');
  await call('desktop_query',{operation:'get',ref:message.ref,property:'value'});
  await call('desktop_context',{operation:'reconcile',recovery_note:'Independent probe confirmed Escape stopped after the started marker; test explicitly resumes.'});
  const forbidden = await call('desktop_run',{code:'desktop.system({operation:"control_resume"});'},true);
  check(forbidden.error?.message.includes('forbidden inside workflows'),'workflow scripts cannot resume even when control is active');
  const cpu = call('desktop_run',{code:`desktop.act({action:'set_value',ref:${JSON.stringify(message.ref)},text:'cpu'}); while(true){}`,timeout_ms:10000},true);
  await waitProbe('read',v=>v.value==='cpu'); const cpuStop=Date.now(); await inspect('escape');
  const cpuResult = await cpu;
  check(cpuResult.error?.code==='USER_STOPPED' && Date.now()-cpuStop<1500,'Escape also interrupts guest CPU code with no subsequent desktop call');
  await call('desktop_system',{operation:'control_resume'});
  window_id = (await call('desktop_windows',{query:title})).windows.find(w=>w.pid===fixture.pid).id;
  const source=await find('Gesture pad'), destination=await find('Drop zone');
  const dragging = call('desktop_act',{action:'drag',ref:source.ref,to_ref:destination.ref,duration_ms:2000,drop_delay_ms:1500,modifiers:['ctrl']},true);
  await waitProbe('held',v=>v.ctrl&&v.left); await inspect('escape');
  const dragged=await dragging;
  check(dragged.error?.code==='USER_STOPPED','Escape interrupts a real mouse drag');
  const released=await waitProbe('held',v=>!v.ctrl&&!v.shift&&!v.left);
  check(!released.ctrl&&!released.left,'owned mouse button and modifier are released on Escape');
  await call('desktop_system',{operation:'control_resume'});
  window_id=(await call('desktop_windows',{query:title})).windows.find(w=>w.pid===fixture.pid).id; message=await find('Message');
  await call('desktop_act',{action:'set_value',ref:message.ref,text:'resumed'});
  check((await inspect('read')).value==='resumed','explicit resume permits new input after a fresh observation');
  await delay(2100);
  check(!(await inspect('visual')).rendered,'indicator hides automatically when idle');
  await inspect('escape'); await delay(100);
  check(!(await call('desktop_system',{operation:'control_status'})).paused,'Escape outside active control remains an ordinary app key');
  await call('desktop_act',{action:'set_value',ref:message.ref,text:'closing'});
  await client.close(); await delay(200);
  check(!(await inspect('visual')).rendered,'disconnect removes the overlay and unregisters its lifetime');
  process.stdout.write(`Control presentation smoke passed: ${checks} checks; no images/logs saved.\n`);
} finally {
  await client.close().catch(()=>{}); competitor.close(); lines.close(); probe.kill();
  if (fixture.exitCode===null) { const exited=once(fixture,'exit'); fixture.kill(); await exited; }
}
