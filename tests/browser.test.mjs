import test from 'node:test';
import assert from 'node:assert/strict';
import { tools, validateCall, toolResult } from '../src/tools.mjs';
import { browserUrl, BrowserController } from '../src/browser-controller.mjs';
import { annotatePng, decodePng, encodePng } from '../src/som-png.mjs';
import { chromium } from 'playwright-core';
const valid=(name,args)=>validateCall(tools.find(tool=>tool.name===name),args);

test('browser URLs cannot select local/internal files, script schemes, relative URLs or embedded credentials',()=>{
  for(const url of ['file:///C:/Windows/win.ini','javascript:alert(1)','data:text/html,test','chrome://settings','/relative','https://user:secret@example.com/']) assert.throws(()=>browserUrl(url));
  assert.equal(browserUrl('about:blank'),'about:blank');assert.equal(browserUrl('https://example.com'),'https://example.com/');
});
test('browser launch accepts no profile attachment, arbitrary executable, arguments or raw CDP endpoint',()=>{
  for(const extra of [{profile_dir:'C:/personal'},{executable:'tool.exe'},{args:['--user-data-dir=C:/personal']},{cdp:'http://localhost:9222'},{code:'return process.env'}]) assert.throws(()=>valid('browser_session',{operation:'start',...extra}));
  assert.throws(()=>valid('browser_session',{operation:'info',browser:'chrome'}));
});
test('existing-tab calls always require a tab id and a unique structured target',()=>{
  assert.throws(()=>valid('browser_read',{operation:'text'}));
  assert.throws(()=>valid('browser_tabs',{operation:'navigate',url:'https://example.com'}));
  for(const locator of [{},{frame_id:'other'},{role:'button',selector:'button'},{selector:'button',name:'Save'},{text:'Save',nth:0}]) assert.throws(()=>valid('browser_act',{tab_id:'t',action:'click',locator}));
  assert.throws(()=>valid('browser_act',{tab_id:'t',action:'click',ref:'bref',locator:{selector:'button'}}));
  valid('browser_act',{tab_id:'t',action:'click',locator:{role:'button',name:'Save',exact:true}});
});
test('browser action contracts reject stale-coordinate bypasses, script execution and browser/global clipboard shortcuts',()=>{
  for(const action of [{action:'evaluate',code:'window.close()'},{action:'click_point',x:10,y:10},{action:'fill',locator:{selector:'input'}},{action:'press',locator:{selector:'input'},key:'Control+V'},{action:'press',locator:{selector:'input'},key:'Control+L'},{action:'click',locator:{selector:'button'},x:10}]) assert.throws(()=>valid('browser_act',{tab_id:'t',...action}));
  assert.throws(()=>valid('browser_capture',{tab_id:'t',path:'evidence.png'}));
  valid('browser_act',{tab_id:'t',action:'click_point',capture_id:'c',x:0,y:0});
});
test('browser waits and recovery notes cannot claim success with a malformed predicate or blank reconciliation',()=>{
  for(const args of [{condition:'checked',equals:'yes',locator:{selector:'input'}},{condition:'url',equals:true},{condition:'text',equals:'ready'},{condition:'url',equals:'x',locator:{selector:'input'}}]) assert.throws(()=>valid('browser_wait',{tab_id:'t',...args}));
  assert.throws(()=>valid('browser_tabs',{operation:'reconcile',tab_id:'t',recovery_note:' '}));
  assert.throws(()=>valid('browser_dialog',{operation:'dismiss',tab_id:'t',prompt_text:'text'}));
  assert.equal(toolResult({ok:true,data:{verification:'failed'}}).isError,true);
});

test('empty strings are rejected by minLength and shifted/duplicate browser shortcuts cannot bypass blocklist',()=>{
  assert.throws(()=>valid('browser_read',{tab_id:'',operation:'text'}));
  assert.throws(()=>valid('browser_act',{tab_id:'t',action:'click',locator:{selector:''}}));
  for(const key of ['Control+Shift+T','Control+Shift+V','Alt+Shift+Tab','Control+F4','Alt+F4','Control+Ctrl+A']) {
    assert.throws(()=>valid('browser_act',{tab_id:'t',action:'press',locator:{selector:'input'},key}));
  }
  valid('browser_act',{tab_id:'t',action:'press',locator:{selector:'input'},key:'Shift+Ctrl+A'});
  valid('browser_act',{tab_id:'t',action:'press',locator:{selector:'input'},key:'Shift+Enter'});
});

test('failed browser_session start resets scoped flag cleanly',async()=>{
  const bridge={userStopped:false,async request(){return {ok:true,data:{active:true}};}};
  const controller=new BrowserController(bridge);
  const originalLaunch=chromium.launch;
  chromium.launch=async()=>{throw new Error('Simulated launch failure');};
  try {
    const first=await controller.request('browser_session',{operation:'start',browser:'chrome',headless:true});
    assert.equal(first.ok,false);
    assert.equal(controller.scoped,false);
    assert.equal(controller.active,false);
    const second=await controller.request('browser_session',{operation:'start',browser:'chrome',headless:true});
    assert.notEqual(second.error?.code,'BROWSER_SESSION_EXISTS');
  } finally { chromium.launch=originalLaunch; }
});

test('browser profile, handoff, capture annotate, and click_mark contracts validate strictly',()=>{
  valid('browser_session',{operation:'start',browser:'chrome',profile:'work-test_1'});
  valid('browser_session',{operation:'list_profiles'});
  valid('browser_session',{operation:'clear_profile',profile:'work-test_1'});
  valid('browser_session',{operation:'handoff',reason:'2FA login'});
  valid('browser_session',{operation:'resume_handoff'});
  for(const bad of [
    {operation:'start',profile:'../escape'},
    {operation:'start',profile:'C:/personal'},
    {operation:'start',profile:'a',profile_dir:'C:/x'},
    {operation:'clear_profile'},
    {operation:'info',profile:'work'},
    {operation:'start',reason:'nope'}
  ]) assert.throws(()=>valid('browser_session',bad));

  valid('browser_read',{tab_id:'t',operation:'snapshot',include_text:false});
  valid('browser_capture',{tab_id:'t',annotate:true,max_marks:12});
  valid('browser_act',{tab_id:'t',action:'click_mark',capture_id:'c',mark:1,observe_after:'diff',settle_ms:100});
  for(const bad of [
    {tab_id:'t',action:'click_mark',capture_id:'c'},
    {tab_id:'t',action:'click_mark',capture_id:'c',mark:1,ref:'b1'},
    {tab_id:'t',action:'click_mark',capture_id:'c',mark:1,x:10},
    {tab_id:'t',action:'click',ref:'b1',mark:1},
    {tab_id:'t',action:'click_mark',capture_id:'c',mark:0}
  ]) assert.throws(()=>valid('browser_act',bad));
});

test('annotatePng draws crisp Set-of-Marks outlines and badge pixels onto real PNG bytes',()=>{
  const width=160, height=100, bpp=4;
  const pixels=new Uint8Array(width*height*bpp);
  pixels.fill(255);
  const src=encodePng({width,height,colorType:6,bpp,pixels});
  const out=annotatePng(src,[{mark:1,bounds:{x:30,y:40,width:60,height:24}}]);
  const decoded=decodePng(out);
  assert.equal(decoded.width,width);
  assert.equal(decoded.height,height);
  const px=(x,y)=>{const i=(y*width+x)*4; return [decoded.pixels[i],decoded.pixels[i+1],decoded.pixels[i+2]];};
  // #31A8FF outline at (30,40)
  assert.deepEqual(px(30,40),[49,168,255]);
  // #085CA7 badge fill interior inside (30, 40-19=21) -> e.g. (33,24)
  assert.deepEqual(px(33,24),[8,92,167]);
  // #FFFFFF digit '1' pixel inside badge at (gx + col*2) -> badge x=30, padX=4 => gx=34; row 0 col 2 => x=38, y=23
  assert.deepEqual(px(38,23),[255,255,255]);
});
