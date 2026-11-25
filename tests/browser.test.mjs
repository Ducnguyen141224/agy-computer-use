import test from 'node:test';
import assert from 'node:assert/strict';
import { tools, validateCall, toolResult } from '../src/tools.mjs';
import { browserUrl } from '../src/browser-controller.mjs';
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
