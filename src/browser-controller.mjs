import { randomUUID } from 'node:crypto';
import { chromium } from 'playwright-core';

export class BrowserError extends Error {
  constructor(code, message, details) { super(message); this.code=code; this.details=details; }
}
const fail = (code,message,details) => {throw new BrowserError(code,message,details);};
const sleep = ms => new Promise(resolve => setTimeout(resolve,ms));
const bounded = (text,limit) => {let length=Math.min(text.length,limit); if(length && /[\uD800-\uDBFF]/.test(text[length-1])) length--; return text.slice(0,length);};
export function browserUrl(value) {
  if(value === 'about:blank') return value;
  let url; try {url=new URL(value);} catch {fail('INVALID_URL','Use an absolute HTTP(S) URL or about:blank.');}
  if(!['http:','https:'].includes(url.protocol) || url.username || url.password) fail('INVALID_URL','Only HTTP(S) URLs without embedded credentials and about:blank are supported.');
  return url.href;
}

// Fixed internal DOM inspection, never user-provided JavaScript. No globals are exported to the page.
function elementInfo(element) {
  if(!element.isConnected) return null;
  const tag=element.tagName.toLowerCase(), type=(element.getAttribute('type')??'').toLowerCase();
  const role=element.getAttribute('role') || ({button:'button',a:'link',textarea:'textbox',select:'combobox',summary:'button',input:type==='checkbox'?'checkbox':type==='radio'?'radio':['button','submit','reset'].includes(type)?'button':'textbox'}[tag]??tag);
  const labelled=(element.getAttribute('aria-labelledby')??'').split(/\s+/).map(id=>element.ownerDocument.getElementById(id)?.textContent??'').join(' ').trim();
  const name=element.getAttribute('aria-label') || labelled || [...(element.labels??[])].map(label=>label.innerText).join(' ').trim() || (['button','a','summary'].includes(tag)?element.innerText:'') || element.getAttribute('placeholder') || element.getAttribute('title') || '';
  const password=tag==='input' && type==='password';
  const value=password?undefined:tag==='input'||tag==='textarea'||tag==='select'?String(element.value??'').slice(0,10000):undefined;
  const box=element.getBoundingClientRect(); const style=getComputedStyle(element);
  return {tag,type,role,name:name.slice(0,500),password,value,checked:typeof element.checked==='boolean'?element.checked:undefined,disabled:!!element.disabled||element.getAttribute('aria-disabled')==='true',visible:box.width>0&&box.height>0&&style.visibility!=='hidden'&&style.display!=='none',href:element.closest('a[href]')?.href??null,bounds:{x:box.x,y:box.y,width:box.width,height:box.height}};
}
const identity = item => JSON.stringify([item.tag,item.type,item.role,item.name,item.href]);

export class BrowserController {
  constructor(bridge) {
    this.bridge=bridge; this.browser=null; this.context=null; this.scoped=false; this.sessionId=null;
    this.tabs=new Map(); this.pages=new WeakMap(); this.interrupted=null; this.generation=0; this.closing=null;
  }
  get active() {return !!this.browser?.isConnected() && !!this.context;}
  #summary() {return {session_id:this.sessionId,active:this.active,scoped:this.scoped,browser:this.channel,headless:this.headless,tab_count:[...this.tabs.values()].filter(tab=>!tab.page.isClosed()).length,stop_reason:this.interrupted,transport:'Playwright pipe; owned temporary browser profile',personal_profile_attached:false,desktop_fallback:false,memory_only_content:true};}
  async request(name,args,{signal}={}) {
    let entry=null, mutating=false, timer, abort, cancelledCleanup;
    try {
      if(name==='browser_session' && args.operation==='info') return {ok:true,data:this.#summary()};
      if(name==='browser_session' && args.operation==='end') {await this.close(); return {ok:true,data:{...this.#summary(),ended:true}};}
      if(signal?.aborted) fail('CANCELLED','Browser request cancelled before execution.',{delivery:'not_sent'});
      if(name==='browser_tabs' && args.operation==='list') {
        this.#connected(); return {ok:true,data:{...this.#summary(),tabs:await Promise.all([...this.tabs.values()].filter(tab=>!tab.page.isClosed()).map(tab=>this.#tabInfo(tab)))}};
      }
      if(args.tab_id) entry=this.#tab(args.tab_id, name==='browser_tabs' && ['adopt','close','info'].includes(args.operation));
      const scope=await this.bridge.request('system',{operation:'control_start'},{signal});
      if(!scope.ok) {if(scope.error?.code==='USER_STOPPED') this.stop('USER_STOPPED'); return scope;}
      if(this.bridge.userStopped) fail('USER_STOPPED','Control was stopped; resume only after a new explicit user request.');
      if(this.interrupted==='USER_STOPPED') this.interrupted=null; // Only the bridge's explicit control_resume can clear its latch.
      const timeout=args.timeout_ms??(name==='browser_session'?15000:5000);
      let cancelled=null;
      const cancellation=new Promise((_,reject)=>{
        const cancel=(code='CANCELLED')=>{
          cancelled=code;
          // Closing the exact target prevents Playwright's internal waits from delivering input later.
          const cleanup=entry?entry.page.close({runBeforeUnload:false}):this.stop(code);
          cancelledCleanup=Promise.resolve(cleanup).catch(()=>{});
          reject(new BrowserError(code,code==='BROWSER_TIMEOUT'?'Browser operation exceeded its time budget; no action was replayed.':'Browser operation was cancelled; no action was replayed.',{delivery:mutating?'unknown':'not_sent'}));
        };
        abort=()=>cancel('CANCELLED');
        signal?.addEventListener('abort',abort,{once:true});
        if(signal?.aborted) abort();
        timer=setTimeout(()=>cancel('BROWSER_TIMEOUT'),timeout+1000);
      });
      const work=(async()=>{
        if(cancelled||signal?.aborted) fail('CANCELLED','Browser request cancelled before execution.',{delivery:'not_sent'});
        if(name==='browser_session') return this.#start(args);
        this.#connected();
        if(name==='browser_tabs') return this.#tabs(args,entry,timeout,()=>{mutating=true;});
        if(entry.dialog && name!=='browser_dialog') fail('BROWSER_DIALOG_OPEN','Resolve this tab dialog explicitly before another page operation.');
        if(name==='browser_read') return this.#read(entry,args,timeout);
        if(name==='browser_capture') return this.#capture(entry,timeout);
        if(name==='browser_wait') return this.#wait(entry,args,timeout);
        if(name==='browser_dialog') return this.#dialog(entry,args,()=>{mutating=true;});
        if(name==='browser_act') {
          if(entry.uncertain) fail('RECONCILIATION_REQUIRED','Read this tab and explicitly reconcile before new input.',{delivery:'not_sent'});
          return this.#act(entry,args,timeout,()=>{mutating=true;});
        }
        fail('UNKNOWN_TOOL','Unknown browser tool.');
      })();
      const data=await Promise.race([work,cancellation]);
      if(cancelled) fail(cancelled,'Browser request interrupted.');
      return {ok:true,data};
    } catch(error) {
      const code=this.interrupted==='USER_STOPPED'||this.bridge.userStopped?'USER_STOPPED':this.interrupted==='NATIVE_CONTROL_LOST'?'NATIVE_CONTROL_LOST':error.code??(error.name==='TimeoutError'?'BROWSER_TIMEOUT':this.browser&&!this.browser.isConnected()?'BROWSER_DISCONNECTED':'BROWSER_ERROR');
      if(entry&&mutating) {entry.uncertain={code}; entry.freshRead=false;}
      return {ok:false,error:{code,message:error instanceof BrowserError?error.message:code==='BROWSER_DISCONNECTED'?'Owned browser disconnected. Start a new session explicitly; no fallback was used.':'Browser operation failed or timed out. Inspect the same tab before any retry.',details:error.details??{delivery:mutating?'unknown':'not_sent',replayed_actions:0}}};
    } finally {
      clearTimeout(timer);if(abort) signal?.removeEventListener('abort',abort);
      if(cancelledCleanup) await cancelledCleanup;
      if(['USER_STOPPED','NATIVE_CONTROL_LOST'].includes(this.interrupted)&&this.closing) await this.closing;
    }
  }
  #connected() {if(this.interrupted==='NATIVE_CONTROL_LOST') fail('NATIVE_CONTROL_LOST','Native Escape/control helper was lost. The owned browser was stopped; explicitly end/start the session.');if(!this.active) fail(this.interrupted==='USER_STOPPED'?'USER_STOPPED':'BROWSER_DISCONNECTED','No connected owned browser session. Start one explicitly; there is no desktop fallback.');}
  #tab(id, allowUnmanaged=false) {
    this.#connected(); const entry=this.tabs.get(id);
    if(!entry) fail('TAB_NOT_OWNED','tab_id does not belong to this browser session.');
    if(entry.page.isClosed()) fail('TAB_CLOSED','This tab is closed. It was not replaced with the active tab.');
    if(!entry.managed&&!allowUnmanaged) fail('POPUP_NOT_ADOPTED','Popup is quarantined. Adopt its returned tab_id explicitly before controlling it.');
    if(!allowUnmanaged) browserUrl(entry.page.url());
    return entry;
  }
  async #start(args) {
    if(this.scoped) fail('BROWSER_SESSION_EXISTS','End the existing browser session before starting another.');
    this.scoped=true; this.interrupted=null; const generation=++this.generation;
    this.channel=args.browser??'chrome'; this.headless=args.headless??false;
    let launched;
    try {
      launched=await chromium.launch({channel:this.channel==='edge'?'msedge':'chrome',headless:this.headless,chromiumSandbox:true,handleSIGINT:false,handleSIGTERM:false,handleSIGHUP:false,timeout:15000});
      if(generation!==this.generation) {await launched.close(); fail('CANCELLED','Browser launch was cancelled.');}
      this.browser=launched;
      this.context=await launched.newContext({acceptDownloads:false,viewport:{width:1280,height:800},deviceScaleFactor:1,serviceWorkers:'block'});
      this.context.setDefaultTimeout(5000); this.context.setDefaultNavigationTimeout(5000);
      this.context.on('page',page=>this.#register(page,false));
      launched.on('disconnected',()=>{this.interrupted??='BROWSER_DISCONNECTED'; for(const tab of this.tabs.values()) this.#invalidate(tab);});
      this.sessionId='browser-'+randomUUID();
      return this.#summary();
    } catch(error) {if(launched) await launched.close().catch(()=>{}); this.browser=null;this.context=null; throw error;}
  }
  #register(page,managed) {
    const existing=this.pages.get(page); if(existing) {if(managed) existing.managed=true;return existing;}
    for(const [id,tab] of this.tabs) if(this.tabs.size>=64&&tab.page.isClosed()) this.tabs.delete(id);
    if([...this.tabs.values()].filter(tab=>!tab.page.isClosed()).length>=16) {void page.close({runBeforeUnload:false}).catch(()=>{});return null;}
    const entry={id:'tab-'+randomUUID(),page,managed,refs:new Map(),frames:new Map(),frameIds:new WeakMap(),captures:new Map(),revision:0,dialog:null,pending:null,uncertain:null,freshRead:false,stamp:null};
    this.pages.set(page,entry); this.tabs.set(entry.id,entry);
    page.on('framenavigated',()=>this.#invalidate(entry)); page.on('close',()=>this.#invalidate(entry)); page.on('crash',()=>{this.#invalidate(entry);entry.uncertain={code:'TAB_CRASHED'};});
    page.on('dialog',dialog=>{entry.dialog=dialog;entry.dialogWake?.();});
    page.on('download',download=>{void download.cancel().catch(()=>{});});
    return entry;
  }
  #invalidate(entry) {
    entry.revision++; entry.freshRead=false;
    for(const ref of entry.refs.values()) void ref.handle.dispose().catch(()=>{});
    entry.refs.clear();entry.captures.clear();
    const stamp=entry.stamp;entry.stamp=null;
    if(stamp) void stamp.evaluate(state=>state.observer.disconnect()).catch(()=>{}).finally(()=>stamp.dispose().catch(()=>{}));
  }
  async #tabInfo(entry) {return {tab_id:entry.id,url:bounded(entry.page.url(),4000),title:entry.dialog?'':bounded(await entry.page.title().catch(()=>''),300),managed:entry.managed,closed:entry.page.isClosed(),dialog:!!entry.dialog,uncertain_delivery:!!entry.uncertain};}
  #ready(entry) {if(entry.dialog) fail('BROWSER_DIALOG_OPEN','Resolve this tab dialog first.');if(entry.uncertain) fail('RECONCILIATION_REQUIRED','Read this tab and reconcile its uncertain delivery first.');}
  async #tabs(args,entry,timeout,dispatched) {
    const operation=args.operation;
    if(operation==='new') {
      if([...this.tabs.values()].filter(tab=>!tab.page.isClosed()).length>=16) fail('TAB_LIMIT','Close an owned tab before opening another.');
      const url=browserUrl(args.url??'about:blank');
      dispatched();
      const page=await this.context.newPage(); entry=this.#register(page,true);
      if(!entry) fail('TAB_LIMIT','This new tab was closed because the owned-tab limit was reached.');
      try {await page.goto(url,{waitUntil:'domcontentloaded',timeout});} catch {entry.uncertain={code:'NAVIGATION_FAILED'};return {...await this.#tabInfo(entry),verification:'failed',delivery:'sent',reason:'Navigation was not confirmed. Inspect this exact tab; it was not recreated.'};}
      return {...await this.#tabInfo(entry),verification:'passed',delivery:'sent'};
    }
    if(operation==='info') return this.#tabInfo(entry);
    if(operation==='adopt') {browserUrl(entry.page.url());entry.managed=true;return this.#tabInfo(entry);}
    if(operation==='close') {dispatched();await entry.page.close({runBeforeUnload:false});return {tab_id:entry.id,closed:true,verification:'passed'};}
    if(operation==='reconcile') {
      if(!entry.uncertain||!entry.freshRead) fail('FRESH_READ_REQUIRED','Inspect this tab after its uncertain action before reconciling.');
      entry.uncertain=null;return {tab_id:entry.id,reconciled:true,note:args.recovery_note};
    }
    this.#ready(entry);
    if(operation==='activate') {await entry.page.bringToFront();return {...await this.#tabInfo(entry),activation_requested:true};}
    if(operation==='navigate') {const url=browserUrl(args.url);dispatched();await entry.page.goto(url,{waitUntil:'domcontentloaded',timeout});}
    if(operation==='back') {dispatched();await entry.page.goBack({waitUntil:'domcontentloaded',timeout});}
    if(operation==='forward') {dispatched();await entry.page.goForward({waitUntil:'domcontentloaded',timeout});}
    if(operation==='reload') {dispatched();await entry.page.reload({waitUntil:'domcontentloaded',timeout});}
    return {...await this.#tabInfo(entry),verification:'passed',delivery:'sent'};
  }
  #frame(entry,id) {
    if(!id) return entry.page.mainFrame();
    const frame=entry.frames.get(id);if(!frame||frame.isDetached()) fail('FRAME_NOT_OWNED','frame_id does not belong to this tab or has detached.');return frame;
  }
  #frameList(entry) {for(const [id,frame] of entry.frames) if(frame.isDetached()) entry.frames.delete(id);return entry.page.frames().slice(0,80).map(frame=>{let id=entry.frameIds.get(frame);if(!id){id='frame-'+randomUUID();entry.frameIds.set(frame,id);entry.frames.set(id,frame);} return {frame_id:id,name:bounded(frame.name(),100),url:bounded(frame.url(),2000),main:frame===entry.page.mainFrame()};});}
  async #resolve(entry,args,{root=false}={}) {
    if(args.ref) {
      const saved=entry.refs.get(args.ref);
      if(!saved||saved.expires<Date.now()||saved.revision!==entry.revision) fail('STALE_BROWSER_REF','Reference is expired or belongs to another tab/navigation. Read the chosen tab again.');
      const current=await saved.handle.evaluate(elementInfo).catch(()=>null);
      if(!current||identity(current)!==saved.identity) fail('STALE_BROWSER_REF','Element identity changed; no replacement target was selected.');
      return {handle:saved.handle,info:current,owned:false};
    }
    const spec=args.locator;const frame=this.#frame(entry,spec?.frame_id);
    let locator;
    if(!spec) {if(!root) fail('INVALID_ARGUMENT','An element target is required.');locator=frame.locator('body');}
    else if(spec.role) locator=frame.getByRole(spec.role,{...(spec.name!==undefined?{name:spec.name}:{}),exact:spec.exact??true});
    else if(spec.test_id) locator=frame.getByTestId(spec.test_id);
    else if(spec.text) locator=frame.getByText(spec.text,{exact:spec.exact??true});
    else locator=frame.locator(spec.selector);
    const count=await locator.count();
    if(count!==1) fail(count?'AMBIGUOUS_TARGET':'TARGET_NOT_FOUND',count?'Several elements match; narrow the locator. No first/nth target was silently chosen.':'No element matches this locator in the selected tab/frame.');
    const handle=await locator.elementHandle({timeout:1000});if(!handle) fail('STALE_BROWSER_REF','Target detached.');
    const info=await handle.evaluate(elementInfo);
    if(!info) {await handle.dispose();fail('STALE_BROWSER_REF','Target detached.');}
    return {handle,info,owned:true};
  }
  #ref(entry,handle,info) {const ref='bref-'+randomUUID();entry.refs.set(ref,{handle,identity:identity(info),expires:Date.now()+60000,revision:entry.revision});return ref;}
  async #read(entry,args) {
    for(const [ref,saved] of entry.refs) if(saved.expires<Date.now()) {entry.refs.delete(ref);void saved.handle.dispose().catch(()=>{});}
    if(entry.refs.size>=400) {for(const ref of entry.refs.values()) void ref.handle.dispose().catch(()=>{});entry.refs.clear();}
    const root=await this.#resolve(entry,args,{root:true});const limit=args.limit??50,max=args.max_chars??16000;
    try {
      if(root.info.password) fail('PASSWORD_FIELD','Password content is excluded.');
      const textData=await root.handle.evaluate((element,max)=>{const text=element.innerText??element.textContent??'';const scroll=element.tagName==='BODY'?element.ownerDocument.scrollingElement:element;return {text:text.slice(0,max+1),length:text.length,scroll:{x:scroll.scrollLeft,y:scroll.scrollTop,width:scroll.clientWidth,height:scroll.clientHeight,content_width:scroll.scrollWidth,content_height:scroll.scrollHeight}};},max);
      const result={...await this.#tabInfo(entry),source:'dom',text:bounded(textData.text,max),text_complete:textData.length<=max,scroll:textData.scroll,frames:this.#frameList(entry),frames_complete:entry.page.frames().length<=80,scope:'selected DOM root/frame; bounds are relative to that frame viewport; other frames are listed, not flattened',image_count:0};
      if(args.operation==='snapshot') {
        const selector='a[href],button,input:not([type=hidden]),textarea,select,[role],[contenteditable=true],summary';
        const handles=await root.handle.evaluateHandle((element,selector)=>{
          const found=[];const walk=root=>{if(root.shadowRoot)walk(root.shadowRoot);for(const child of root.querySelectorAll('*')) {if(child.matches(selector)) found.push(child);if(found.length>300)return;if(child.shadowRoot)walk(child.shadowRoot);if(found.length>300)return;}};
          walk(element);return found.slice(0,301);
        },selector);
        const candidates=[...(await handles.getProperties()).values()].map(handle=>handle.asElement()).filter(Boolean);await handles.dispose();
        const items=[];let scanned=0;
        for(const handle of candidates) {
          if(items.length>=limit||scanned++>=300) {await handle.dispose();continue;}
          const info=await handle.evaluate(elementInfo).catch(()=>null);
          if(!info||!info.visible) {await handle.dispose();continue;}
          const {href,tag,type,...publicInfo}=info;items.push({ref:this.#ref(entry,handle,info),...publicInfo});
        }
        result.items=items;result.controls_complete=candidates.length<=scanned&&items.length<limit;
      }
      entry.freshRead=true;return result;
    } finally {if(root.owned) await root.handle.dispose();}
  }
  async #act(entry,args,timeout,dispatched) {
    if(['click_point','mouse_move'].includes(args.action)) {
      const capture=entry.captures.get(args.capture_id);entry.captures.delete(args.capture_id);
      if(!capture||capture.expires<Date.now()||capture.revision!==entry.revision) fail('STALE_CAPTURE','Point token is expired, consumed or belongs to another tab.');
      const state=await this.#stamp(entry);
      if(JSON.stringify(state)!==JSON.stringify(capture.state)||args.x>=capture.width||args.y>=capture.height) fail('STALE_CAPTURE','Page/viewport/scroll changed after capture or coordinates are outside this image.');
      dispatched();if(args.action==='click_point') await entry.page.mouse.click(args.x,args.y);else await entry.page.mouse.move(args.x,args.y);
      entry.captures.clear();return {tab_id:entry.id,delivery:'sent',verification:'not_requested'};
    }
    const target=args.ref||args.locator?await this.#resolve(entry,args):null;
    try {
      if(target?.info.password) fail('PASSWORD_FIELD','Password fields are excluded from this plugin.');
      if(target?.info.type==='file') fail('ACTION_UNSUPPORTED','File uploads are not exposed by browser_act.');
      if(target?.info.href&&['click','double_click'].includes(args.action)) browserUrl(target.info.href);
      if(target?.info.disabled) fail('NOT_ACTIONABLE','The chosen target is disabled.');
      entry.captures.clear();
      if(args.action==='drag') {
        const destination=await this.#resolve(entry,{ref:args.to_ref});
        if(destination.info.password) fail('PASSWORD_FIELD','Password targets are excluded.');
        const sourceBox=await target.handle.boundingBox(),endBox=await destination.handle.boundingBox();
        const viewport=entry.page.viewportSize();
        if(!sourceBox||!endBox||[sourceBox,endBox].some(box=>box.x<0||box.y<0||box.x+box.width>viewport.width||box.y+box.height>viewport.height)) fail('NOT_ACTIONABLE','Scroll both drag endpoints fully into this tab viewport first.');
        dispatched();await entry.page.mouse.move(sourceBox.x+sourceBox.width/2,sourceBox.y+sourceBox.height/2);
        try {await entry.page.mouse.down();await entry.page.mouse.move(endBox.x+endBox.width/2,endBox.y+endBox.height/2,{steps:12});} finally {await entry.page.mouse.up().catch(()=>{});}
      } else {
        let dialogWake;const dialogOpened=new Promise(resolve=>{dialogWake=resolve;});entry.dialogWake=dialogWake;
        const execute=async()=>{
          dispatched();const opts={timeout};
          switch(args.action) {
            case 'click':await target.handle.click(opts);break;
            case 'double_click':await target.handle.dblclick(opts);break;
            case 'hover':await target.handle.hover(opts);break;
            case 'fill':await target.handle.fill(args.text,opts);break;
            case 'type':await target.handle.type(args.text,opts);break;
            case 'press':await target.handle.press(args.key.replace(/^Ctrl\+/,'Control+').replace(/^Esc$/,'Escape'),opts);break;
            case 'check':await target.handle.setChecked(true,opts);break;
            case 'uncheck':await target.handle.setChecked(false,opts);break;
            case 'select_option':await target.handle.selectOption({label:args.option},opts);break;
            case 'scroll': {
              const vertical=['up','down'].includes(args.direction),delta=(args.pixels??600)*(['up','left'].includes(args.direction)?-1:1);
              if(target) await target.handle.evaluate((element,{vertical,delta})=>element.scrollBy({left:vertical?0:delta,top:vertical?delta:0,behavior:'instant'}),{vertical,delta});
              else await entry.page.evaluate(({vertical,delta})=>window.scrollBy({left:vertical?0:delta,top:vertical?delta:0,behavior:'instant'}),{vertical,delta});
              break;
            }
            default:fail('INVALID_ARGUMENT','Unsupported browser action.');
          }
        };
        const action=execute();action.catch(()=>{});
        await Promise.race([action,dialogOpened]);entry.dialogWake=null;
        if(entry.dialog) {entry.pending=action;return {tab_id:entry.id,delivery:'sent',verification:'pending_dialog',dialog:{type:entry.dialog.type(),message:bounded(entry.dialog.message(),2000)}};}
      }
      return {tab_id:entry.id,delivery:'sent',verification:'not_requested',note:'Read the target state or use browser_wait to verify the requested effect.'};
    } finally {if(target?.owned) await target.handle.dispose().catch(()=>{});}
  }
  async #wait(entry,args,timeout) {
    const deadline=Date.now()+timeout;let actual=null;
    do {
      if(entry.page.isClosed()) fail('TAB_CLOSED','The selected tab closed while waiting.');
      if(args.condition==='url') actual=entry.page.url();
      else {
        let target;
        try {
          target=await this.#resolve(entry,args);if(target.info.password) fail('PASSWORD_FIELD','Password content is excluded.');
          actual=args.condition==='visible'?target.info.visible:args.condition==='hidden'?!target.info.visible:args.condition==='value'?target.info.value:args.condition==='checked'?target.info.checked:await target.handle.innerText();
        } catch(error) {if(args.condition==='hidden'&&error.code==='TARGET_NOT_FOUND') actual=true;else if(error.code==='TARGET_NOT_FOUND') actual=null;else throw error;}
        finally {if(target?.owned) await target.handle.dispose().catch(()=>{});}
      }
      const matches=['visible','hidden'].includes(args.condition)?actual===true:args.contains?typeof actual==='string'&&actual.includes(args.equals):actual===args.equals;
      if(matches) return {tab_id:entry.id,verification:'passed',condition:args.condition,value:typeof actual==='string'?bounded(actual,10000):actual};
      if(Date.now()>=deadline) break;await sleep(50);
    }while(true);
    return {tab_id:entry.id,verification:'failed',condition:args.condition,value:typeof actual==='string'?bounded(actual,10000):actual,reason:'Postcondition was not observed before timeout.'};
  }
  async #stamp(entry) {
    if(!entry.stamp) entry.stamp=await entry.page.evaluateHandle(()=>{const state={revision:0,observer:null};state.observer=new MutationObserver(()=>state.revision++);state.observer.observe(document,{subtree:true,attributes:true,childList:true,characterData:true});return state;});
    return entry.stamp.evaluate(state=>({revision:state.revision,x:scrollX,y:scrollY,width:innerWidth,height:innerHeight,url:location.href}));
  }
  async #capture(entry,timeout) {
    const state=await this.#stamp(entry),revision=entry.revision;
    // Keep the DOM intact: default caret-hiding/masking injects temporary styles and breaks
    // mutation validation. Browser password inputs render their normal masked characters.
    const bytes=await entry.page.screenshot({type:'png',fullPage:false,caret:'initial',timeout});
    if(entry.revision!==revision||JSON.stringify(state)!==JSON.stringify(await this.#stamp(entry))) fail('CAPTURE_CHANGED','The page changed during capture. No point token was issued.');
    const width=bytes.readUInt32BE(16),height=bytes.readUInt32BE(20);if(width>1600||height>1600) fail('CAPTURE_LIMIT','Viewport exceeded the browser image budget.');
    const capture_id='bcap-'+randomUUID();entry.captures.clear();entry.captures.set(capture_id,{state,revision,width,height,expires:Date.now()+10000});
    entry.freshRead=true;
    return {tab_id:entry.id,capture_id,width,height,image:bytes.toString('base64'),coordinate_system:'tab viewport CSS pixels; single use; 10s',memory_only:true};
  }
  async #dialog(entry,args,dispatched) {
    if(!entry.dialog) {if(args.operation==='inspect') return {tab_id:entry.id,dialog:null};fail('DIALOG_NOT_FOUND','This tab has no pending JavaScript dialog.');}
    const dialog=entry.dialog;
    if(args.operation==='inspect') return {tab_id:entry.id,dialog:{type:dialog.type(),message:bounded(dialog.message(),2000),default_value:bounded(dialog.defaultValue(),1000)}};
    if(args.prompt_text!==undefined&&dialog.type()!=='prompt') fail('INVALID_ARGUMENT','prompt_text requires a prompt dialog.');
    dispatched();
    if(args.operation==='accept') await dialog.accept(args.prompt_text);else await dialog.dismiss();
    entry.dialog=null;if(entry.pending) {const pending=entry.pending;entry.pending=null;await pending;}
    return {tab_id:entry.id,handled:true,action:args.operation,delivery:'sent'};
  }
  stop(code='USER_STOPPED') {this.interrupted=code;this.generation++;return this.#dispose(false);}
  async #dispose(endScope) {
    if(this.closing) {await this.closing;if(endScope){this.tabs.clear();this.pages=new WeakMap();this.scoped=false;this.sessionId=null;this.interrupted=null;}return;}
    const browser=this.browser,context=this.context;this.browser=null;this.context=null;
    for(const entry of this.tabs.values()) this.#invalidate(entry);
    this.closing=(async()=>{
      if(browser) {
        try {await browser.close();}
        catch {if(browser.isConnected()){this.browser=browser;this.context=context;fail('BROWSER_CLOSE_UNCONFIRMED','Owned browser closure was not confirmed. The browser scope remains locked.');}}
        if(browser.isConnected()){this.browser=browser;this.context=context;fail('BROWSER_CLOSE_UNCONFIRMED','Owned browser is still connected; scope was not released.');}
      }
    })();
    try {await this.closing;} finally {this.closing=null;if(endScope&&!this.browser?.isConnected()){this.tabs.clear();this.pages=new WeakMap();this.scoped=false;this.sessionId=null;this.interrupted=null;}}
  }
  async close() {this.generation++;await this.#dispose(true);if(!this.bridge.closed) await this.bridge.request('system',{operation:'control_end'}).catch(()=>{});}
}
