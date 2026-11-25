import { createServer } from 'node:http';

// Real HTTP pages and independently observed server effects, kept in RAM only.
export async function browserFixture() {
  const saved=[];
  const frameHtml='<!doctype html><label>Frame message <input aria-label="Frame message"></label><button onclick="document.querySelector(\'output\').textContent=\'Frame saved\'">Frame save</button><output>Frame idle</output>';
  const frameServer=createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end(frameHtml);});
  await new Promise(resolve=>frameServer.listen(0,'127.0.0.1',resolve));
  const server=createServer(async(req,res)=>{
    const url=new URL(req.url,'http://fixture');
    if(url.pathname==='/record') {let text='';for await(const chunk of req) text+=chunk; saved.push(JSON.parse(text));res.end('ok');return;}
    const label=url.searchParams.get('tab')??'fixture';
    res.setHeader('Content-Type','text/html; charset=utf-8');
    if(url.pathname==='/frame') {res.end(frameHtml);return;}
    if(url.pathname==='/next') {res.end('<!doctype html><title>Next fixture</title><h1>Next page</h1><a href="/main?tab='+encodeURIComponent(label)+'">Return</a>');return;}
    if(url.pathname==='/popup') {res.end('<!doctype html><title>Popup fixture</title><h1>Owned popup</h1><button>Popup action</button>');return;}
    res.end(`<!doctype html><meta charset="utf-8"><title>AGY browser ${label}</title>
      <style>body{font:16px Segoe UI;margin:24px}button,input,select{margin:6px;padding:8px}#pad{width:160px;height:80px;background:rgb(30,110,220)}#space{height:1800px}#scroller{height:70px;width:200px;overflow:auto}#inner{height:400px}iframe{width:400px;height:130px}</style>
      <h1>Browser fixture ${label}</h1>
      <div style="display:flex;gap:20px;height:50px"><div id="drag-source" role="button" aria-label="Drag source" draggable="true" ondragstart="event.dataTransfer.setData('text/plain','owned-drag')">Drag source</div><div id="drop-target" role="button" aria-label="Drop target" ondragover="event.preventDefault()" ondrop="event.preventDefault();this.textContent=event.dataTransfer.getData('text/plain')==='owned-drag'?'Drop received':'Wrong drag'">Drop idle</div></div>
      <form id="form"><label>Message <input id="message" aria-label="Message"></label><button id="save">Save</button></form>
      <input id="blocked" aria-label="Initially hidden" value="not changed" style="display:none"><button id="reveal" onclick="document.querySelector('#blocked').style.display='inline-block'">Reveal field</button>
      <label>Secret <input type="password" value="password-secret-sentinel"></label>
      <label><input type="checkbox" id="check"> Confirm</label>
      <label>Size <select id="size"><option>Small</option><option>Large</option></select></label>
      <button>Duplicate</button><button>Duplicate</button>
      <button id="replace" onclick="document.querySelector('#victim').outerHTML='<button id=victim>Replacement target</button>'">Replace target</button><button id="victim">Original target</button>
      <button id="popup" onclick="window.open('/popup','_blank')">Open popup</button>
      <button id="confirm" onclick="document.querySelector('#dialog-result').textContent=confirm('Fixture confirm?')?'Accepted':'Dismissed'">Confirm dialog</button><output id="dialog-result">No dialog</output>
      <button id="prompt" onclick="document.querySelector('#dialog-result').textContent=prompt('Fixture prompt?','initial')??'Cancelled'">Prompt dialog</button>
      <button id="arm" onclick="setTimeout(()=>document.querySelector('#timer').textContent='Changed after capture',1500)">Arm mutation</button><output id="timer">Timer idle</output>
      <div id="pad" role="button" aria-label="Point pad" onclick="this.textContent='Point received'">Point idle</div>
      <output id="status" role="status">Idle</output>
      <a href="/next?tab=${encodeURIComponent(label)}">Next page</a>
      <iframe title="Fixture frame" src="http://127.0.0.1:${frameServer.address().port}/frame"></iframe>
      <div id="shadow"></div>
      <div id="scroller" role="region" aria-label="Scroll region"><div id="inner">Scroll contents</div></div>
      <div id="space"></div><p>Bottom marker</p>
      <script>const shadow=document.querySelector('#shadow').attachShadow({mode:'open'});shadow.innerHTML='<label>Shadow message <input aria-label="Shadow message"></label>';document.querySelector('#form').onsubmit=async event=>{event.preventDefault();const value=document.querySelector('#message').value;await fetch('/record',{method:'POST',body:JSON.stringify({tab:${JSON.stringify(label)},value})});document.querySelector('#status').textContent='Saved: '+value;};</script>`);
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  return {url:`http://127.0.0.1:${server.address().port}`,saved,close:()=>Promise.all([new Promise(resolve=>server.close(resolve)),new Promise(resolve=>frameServer.close(resolve))])};
}
