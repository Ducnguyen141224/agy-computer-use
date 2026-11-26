import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright-core';
import { annotatePng, fitPng } from './som-png.mjs';

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
  return {tag,type,role,name:name.slice(0,500),password,value,checked:typeof element.checked==='boolean'?element.checked:undefined,disabled:!!element.disabled||element.getAttribute('aria-disabled')==='true',visible:box.width>0&&box.height>0&&style.visibility!=='hidden'&&style.display!=='none',href:element.closest('a[href]')?.href??null,bounds:{x:Math.round(box.x),y:Math.round(box.y),width:Math.round(box.width),height:Math.round(box.height)}};
}
const identity = item => JSON.stringify([item.tag,item.type,item.role,item.name,item.href]);

export class BrowserController {
  constructor(bridge) {
    this.bridge=bridge; this.browser=null; this.context=null; this.scoped=false; this.sessionId=null;
    this.tabs=new Map(); this.pages=new WeakMap(); this.interrupted=null; this.generation=0; this.closing=null; this.refSeq=0;
    this.persistentProfile=null; this.starterPage=null; this.handoff=null; this.contextClosed=false; this.headless=false; this.channel=undefined;
    this.attached=false; this.connectionMode='existing';
  }
  get active() {return (this.browser ? this.browser.isConnected() : !this.contextClosed) && !!this.context;}
  #profilesRoot() {return join(process.env.LOCALAPPDATA ?? tmpdir(), 'agy-computer-use', 'profiles');}
  #profileDir(name) {
    if(!/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(name)) fail('INVALID_ARGUMENT','Invalid profile name.');
    return join(this.#profilesRoot(), name);
  }
  #listProfiles() {
    const root=this.#profilesRoot();
    if(!existsSync(root)) return [];
    return readdirSync(root, {withFileTypes:true}).filter(d=>d.isDirectory()&&/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(d.name)).map(d=>d.name).sort();
  }
  #clearProfile(name) {
    if(this.active && this.persistentProfile===name) fail('PROFILE_IN_USE','End the active session using this profile before clearing it.');
    const dir=this.#profileDir(name);
    const existed=existsSync(dir);
    if(existed) rmSync(dir, {recursive:true, force:true});
    return {profile:name, cleared:true, existed};
  }
  #summary() {return {session_id:this.sessionId,active:this.active,scoped:this.scoped,browser:this.channel,connection:this.connectionMode,headless:this.headless,profile:this.persistentProfile??null,persistent_profile:!!this.persistentProfile,handoff:this.handoff,tab_count:[...this.tabs.values()].filter(tab=>!tab.page.isClosed()).length,stop_reason:this.interrupted,transport:this.attached?'Chrome user-approved CDP; existing login':this.persistentProfile?'Playwright pipe; isolated agent work profile':'Playwright pipe; owned temporary browser profile',personal_profile_attached:this.attached&&this.active,existing_personal_tabs_exposed:false,desktop_fallback:false,memory_only_content:true};}
  async request(name,args,{signal}={}) {
    let entry=null, createdPage=null, mutating=false, timer, abort, cancelledCleanup, activityToken=null, activityGeneration=0, finishing=false, cancelled=null;
    const releaseActivity=async()=>{
      const token=activityToken;activityToken=null;
      if(token&&this.bridge.child&&this.bridge.generation===activityGeneration&&!this.bridge.closed) {
        let released=false;
        try {released=(await this.bridge.request('presentation',{operation:'end',token})).ok===true;} catch {}
        if(!released) this.bridge.invalidate();
      }
    };
    try {
      if(name==='browser_session' && args.operation==='info') return {ok:true,data:this.#summary()};
      if(name==='browser_session' && args.operation==='end') {await this.close(); return {ok:true,data:{...this.#summary(),ended:true}};}
      if(name==='browser_session' && args.operation==='list_profiles') return {ok:true,data:{profiles:this.#listProfiles(),personal_profile_attached:false}};
      if(name==='browser_session' && args.operation==='clear_profile') return {ok:true,data:this.#clearProfile(args.profile)};
      if(signal?.aborted) fail('CANCELLED','Browser request cancelled before execution.',{delivery:'not_sent'});
      if(name==='browser_tabs' && args.operation==='list') {
        this.#connected(); return {ok:true,data:{...this.#summary(),tabs:await Promise.all([...this.tabs.values()].filter(tab=>!tab.page.isClosed()).map(tab=>this.#tabInfo(tab)))}};
      }
      if(args.tab_id) {
        const allowUnmanaged=name==='browser_tabs' && ['adopt','close','info'].includes(args.operation);
        const allowNonHttp=allowUnmanaged || (name==='browser_tabs' && ['navigate','back','forward','reload','reconcile'].includes(args.operation));
        entry=this.#tab(args.tab_id, allowUnmanaged, allowNonHttp);
      }
      const isHeadless=(name==='browser_session' && args.operation==='start') ? args.headless===true : this.headless===true;
      const scope=await this.bridge.request('system',{operation:'control_start'},{signal});
      if(!scope.ok) {
        if(scope.error?.code==='USER_STOPPED') {this.stop('USER_STOPPED'); return scope;}
        if(!(isHeadless && scope.error?.code==='DESKTOP_UNAVAILABLE')) return scope;
      }
      if(this.bridge.userStopped) fail('USER_STOPPED','Control was stopped; resume only after a new explicit user request.');
      if(this.interrupted==='USER_STOPPED') this.interrupted=null; // Only the bridge's explicit control_resume can clear its latch.
      const timeout=args.timeout_ms??(name==='browser_session'?35000:5000);
      const dispatched=async()=>{
        if(finishing||cancelled||signal?.aborted) fail(cancelled??'CANCELLED','Request expired before input.',{delivery:'not_sent'});
        mutating=true;
        if(!this.headless&&!activityToken) {
          const activity=await this.bridge.request('presentation',{operation:'begin'},{signal});
          if(!activity.ok) fail(activity.error?.code??'PRESENTATION_UNAVAILABLE',activity.error?.message??'Control indicator unavailable. No new input was sent.');
          activityToken=activity.data.token;activityGeneration=this.bridge.generation;
          if(finishing||cancelled) {await releaseActivity();fail(cancelled??'CANCELLED','Request expired before input.');}
        }
        if(this.bridge.userStopped||this.interrupted==='USER_STOPPED') fail('USER_STOPPED','Escape stopped control; no new input was sent.');
        if(signal?.aborted) fail('CANCELLED','Request cancelled before input.');
        // Real Chrome can throttle background pages. Select only our work tab when
        // sending input; reads never switch the user's foreground tab.
        if(this.attached&&entry&&!entry.page.isClosed()) await entry.page.bringToFront();
        if(finishing||cancelled||signal?.aborted) fail(cancelled??'CANCELLED','Request expired before input.');
      };
      const cancellation=new Promise((_,reject)=>{
        const cancel=(code='CANCELLED')=>{
          cancelled=code;
          // Closing only the active mutating target or newly-created page prevents killing healthy tabs on read timeouts.
          const cleanup=entry
            ?(mutating||code==='USER_STOPPED'?entry.page.close({runBeforeUnload:false}):null)
            :(createdPage?createdPage.close({runBeforeUnload:false}):(name==='browser_session'||code==='USER_STOPPED'?this.stop(code):null));
          cancelledCleanup=cleanup?Promise.resolve(cleanup).catch(()=>{}):null;
          reject(new BrowserError(code,code==='BROWSER_TIMEOUT'?'Browser operation exceeded its time budget; no action was replayed.':'Browser operation was cancelled; no action was replayed.',{delivery:mutating?'unknown':'not_sent'}));
        };
        abort=()=>cancel('CANCELLED');
        signal?.addEventListener('abort',abort,{once:true});
        if(signal?.aborted) abort();
        timer=setTimeout(()=>cancel('BROWSER_TIMEOUT'),timeout+1000);
      });
      const work=(async()=>{
        if(cancelled||signal?.aborted) fail('CANCELLED','Browser request cancelled before execution.',{delivery:'not_sent'});
        if(name==='browser_session') {
          if(args.operation==='start') return this.#start(args);
          if(args.operation==='handoff') return this.#handoff(args);
          if(args.operation==='resume_handoff') return this.#resumeHandoff();
          fail('INVALID_ARGUMENT','Unsupported browser_session operation.');
        }
        this.#connected();
        if(name==='browser_tabs') return this.#tabs(args,entry,timeout,dispatched,async page=>{
          createdPage=page;
          if(finishing||cancelled||signal?.aborted) {await page.close({runBeforeUnload:false});fail(cancelled??'CANCELLED','Tab creation expired; the late work tab was closed.');}
        });
        if(entry.dialog && name!=='browser_dialog') fail('BROWSER_DIALOG_OPEN','Resolve this tab dialog explicitly before another page operation.');
        if(name==='browser_read') return this.#read(entry,args,timeout);
        if(name==='browser_capture') return this.#capture(entry,args,timeout);
        if(name==='browser_wait') return this.#wait(entry,args,timeout);
        if(name==='browser_dialog') {
          if(this.handoff?.active && ['accept','dismiss'].includes(args.operation)) fail('USER_HANDOFF_ACTIVE','User handoff is active. Call browser_session resume_handoff after user finishes.');
          return this.#dialog(entry,args,dispatched);
        }
        if(name==='browser_act') {
          if(this.handoff?.active) fail('USER_HANDOFF_ACTIVE','User handoff is active. Call browser_session resume_handoff after user finishes.');
          if(entry.uncertain) fail('RECONCILIATION_REQUIRED','Read this tab and explicitly reconcile before new input.',{delivery:'not_sent'});
          return this.#act(entry,args,timeout,dispatched);
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
      finishing=true;
      clearTimeout(timer);if(abort) signal?.removeEventListener('abort',abort);
      await releaseActivity();
      if(cancelledCleanup) await cancelledCleanup;
      if(['USER_STOPPED','NATIVE_CONTROL_LOST'].includes(this.interrupted)&&this.closing) await this.closing;
    }
  }
  #connected() {if(this.interrupted==='NATIVE_CONTROL_LOST') fail('NATIVE_CONTROL_LOST','Native Escape/control helper was lost. The owned browser was stopped; explicitly end/start the session.');if(!this.active) fail(this.interrupted==='USER_STOPPED'?'USER_STOPPED':'BROWSER_DISCONNECTED','No connected owned browser session. Start one explicitly; there is no desktop fallback.');}
  #tab(id, allowUnmanaged=false, allowNonHttpUrl=allowUnmanaged) {
    this.#connected(); const entry=this.tabs.get(id);
    if(!entry) fail('TAB_NOT_OWNED','tab_id does not belong to this browser session.');
    if(entry.page.isClosed()) fail('TAB_CLOSED','This tab is closed. It was not replaced with the active tab.');
    if(!entry.managed&&!allowUnmanaged) fail('POPUP_NOT_ADOPTED','Popup is quarantined. Adopt its returned tab_id explicitly before controlling it.');
    if(!allowNonHttpUrl) browserUrl(entry.page.url());
    return entry;
  }
  async #start(args) {
    if(this.scoped) fail('BROWSER_SESSION_EXISTS','End the existing browser session before starting another.');
    this.scoped=true; this.interrupted=null; this.handoff=null; this.contextClosed=false; const generation=++this.generation;
    this.channel=args.browser??'chrome'; this.headless=args.headless??false;
    this.connectionMode=args.connection??'existing';this.attached=this.connectionMode==='existing';
    this.persistentProfile=args.profile??null;
    let launched=null, persistentCtx=null;
    try {
      const channelName=this.channel==='edge'?'msedge':'chrome';
      const commonOpts={channel:channelName,headless:this.headless,chromiumSandbox:true,handleSIGINT:false,handleSIGTERM:false,handleSIGHUP:false,timeout:15000};
      if(this.attached) {
        let endpoint='chrome';
        // Fixture-only selector. Production never accepts arbitrary CDP endpoints/profile paths from MCP.
        if(process.env.AGY_CHROME_CDP_TEST_ENDPOINT&&/^[0-9a-f]{32}$/i.test(process.env.AGY_DESKTOP_TEST_SCOPE??'')) {
          const url=new URL(process.env.AGY_CHROME_CDP_TEST_ENDPOINT);
          if(url.protocol!=='http:'||!['127.0.0.1','localhost','[::1]'].includes(url.hostname)||url.username||url.password) fail('INVALID_ARGUMENT','Test CDP endpoint must be loopback HTTP.');
          endpoint=url.href;
        }
        try { launched=await chromium.connectOverCDP(endpoint,{noDefaults:true,timeout:30000}); }
        catch { fail('CHROME_CONNECTION_REQUIRED','Open chrome://inspect/#remote-debugging in your running Chrome, enable remote debugging and approve Chrome\'s connection prompt. No separate browser/profile was opened.',{settings_url:'chrome://inspect/#remote-debugging',fallback_opened:false}); }
        if(generation!==this.generation) {await launched.close();fail('CANCELLED','Chrome connection was cancelled.');}
        this.browser=launched;this.context=launched.contexts()[0];
        if(!this.context) fail('CHROME_CONTEXT_UNAVAILABLE','The running Chrome did not provide its default context.');
        this.starterPage=null;
      } else if(this.persistentProfile) {
        const profileDir=this.#profileDir(this.persistentProfile);
        mkdirSync(profileDir, {recursive:true});
        persistentCtx=await chromium.launchPersistentContext(profileDir,{...commonOpts,acceptDownloads:false,viewport:{width:1280,height:800},deviceScaleFactor:1,serviceWorkers:'block'});
        if(generation!==this.generation) {await persistentCtx.close(); fail('CANCELLED','Browser launch was cancelled.');}
        this.context=persistentCtx;
        this.browser=persistentCtx.browser();
        const existing=persistentCtx.pages();
        this.starterPage=existing[0]??null;
        for(const extra of existing.slice(1)) void extra.close({runBeforeUnload:false}).catch(()=>{});
      } else {
        launched=await chromium.launch(commonOpts);
        if(generation!==this.generation) {await launched.close(); fail('CANCELLED','Browser launch was cancelled.');}
        this.browser=launched;
        this.context=await launched.newContext({acceptDownloads:false,viewport:{width:1280,height:800},deviceScaleFactor:1,serviceWorkers:'block'});
        this.starterPage=null;
      }
      this.context.setDefaultTimeout(5000); this.context.setDefaultNavigationTimeout(5000);
      this.context.on('page',page=>{
        if(generation!==this.generation||!this.context) return;
        if(!this.attached) {if(page!==this.starterPage)this.#register(page,false);return;}
        void page.opener().then(opener=>{if(generation===this.generation&&this.context&&opener&&this.pages.has(opener)&&!page.isClosed())this.#register(page,false);}).catch(()=>{});
      });
      this.context.on('close',()=>{this.contextClosed=true;});
      this.browser?.on('disconnected',()=>{this.interrupted??='BROWSER_DISCONNECTED'; for(const tab of this.tabs.values()) this.#invalidate(tab);});
      this.sessionId='browser-'+randomUUID();
      return this.#summary();
    } catch(error) {
      if(persistentCtx) await persistentCtx.close().catch(()=>{});
      if(launched) await launched.close().catch(()=>{});
      this.browser=null;this.context=null;this.scoped=false;this.sessionId=null;this.persistentProfile=null;this.starterPage=null;this.attached=false;
      throw error;
    }
  }
  async #handoff(args) {
    this.#connected();
    const openTabs=[...this.tabs.values()].filter(tab=>!tab.page.isClosed());
    const focusTab=[...openTabs].reverse().find(t=>t.managed)??openTabs.at(-1);
    if(focusTab && !this.headless) await focusTab.page.bringToFront().catch(()=>{});
    await this.bridge.request('system',{operation:'control_end'}).catch(()=>{});
    this.handoff={active:true,reason:args.reason??'User handoff active'};
    return {...this.#summary(),instructions:'User handoff active. Wait for the user to finish, then call browser_session resume_handoff.'};
  }
  async #resumeHandoff() {
    this.#connected();
    this.handoff=null;
    for(const tab of this.tabs.values()) this.#invalidate(tab);
    const tabs=await Promise.all([...this.tabs.values()].filter(tab=>!tab.page.isClosed()).map(tab=>this.#tabInfo(tab)));
    return {...this.#summary(),handoff_resumed:true,tabs};
  }
  #register(page,managed) {
    const existing=this.pages.get(page); if(existing) {if(managed) existing.managed=true;return existing;}
    for(const [id,tab] of this.tabs) if(this.tabs.size>=64&&tab.page.isClosed()) this.tabs.delete(id);
    if([...this.tabs.values()].filter(tab=>!tab.page.isClosed()).length>=16) {void page.close({runBeforeUnload:false}).catch(()=>{});return null;}
    const entry={id:'tab-'+randomUUID(),page,managed,refs:new Map(),frames:new Map(),frameIds:new WeakMap(),captures:new Map(),revision:0,dialog:null,pending:null,uncertain:null,freshRead:false,stamp:null,lastSnapshot:null};
    this.pages.set(page,entry); this.tabs.set(entry.id,entry);
    page.on('framenavigated',()=>this.#invalidate(entry)); page.on('close',()=>this.#invalidate(entry)); page.on('crash',()=>{this.#invalidate(entry);entry.uncertain={code:'TAB_CRASHED'};});
    page.on('dialog',dialog=>{entry.dialog=dialog;entry.dialogWake?.();});
    page.on('download',download=>{void download.cancel().catch(()=>{});});
    return entry;
  }
  #invalidate(entry) {
    entry.revision++; entry.freshRead=false; entry.lastSnapshot=null;
    for(const ref of entry.refs.values()) void ref.handle.dispose().catch(()=>{});
    entry.refs.clear();entry.captures.clear();
    const stamp=entry.stamp;entry.stamp=null;
    if(stamp) void stamp.evaluate(state=>state.observer.disconnect()).catch(()=>{}).finally(()=>stamp.dispose().catch(()=>{}));
  }
  async #tabInfo(entry) {return {tab_id:entry.id,url:bounded(entry.page.url(),4000),title:entry.dialog?'':bounded(await entry.page.title().catch(()=>''),300),managed:entry.managed,closed:entry.page.isClosed(),dialog:!!entry.dialog,uncertain_delivery:!!entry.uncertain};}
  #ready(entry) {if(entry.dialog) fail('BROWSER_DIALOG_OPEN','Resolve this tab dialog first.');if(entry.uncertain) fail('RECONCILIATION_REQUIRED','Read this tab and reconcile its uncertain delivery first.');}
  async #tabs(args,entry,timeout,dispatched,onNewPage) {
    const operation=args.operation;
    if(operation==='new') {
      if([...this.tabs.values()].filter(tab=>!tab.page.isClosed()).length>=16) fail('TAB_LIMIT','Close an owned tab before opening another.');
      const url=browserUrl(args.url??'about:blank');
      await dispatched();
      let page=null;
      if(this.starterPage && !this.starterPage.isClosed()) { page=this.starterPage; this.starterPage=null; }
      else page=await this.context.newPage();
      await onNewPage?.(page); entry=this.#register(page,true);
      if(!entry) fail('TAB_LIMIT','This new tab was closed because the owned-tab limit was reached.');
      try {await page.goto(url,{waitUntil:'domcontentloaded',timeout});} catch {entry.uncertain={code:'NAVIGATION_FAILED'};return {...await this.#tabInfo(entry),verification:'failed',delivery:'sent',reason:'Navigation was not confirmed. Inspect this exact tab; it was not recreated.'};}
      return {...await this.#tabInfo(entry),verification:'passed',delivery:'sent'};
    }
    if(operation==='info') return this.#tabInfo(entry);
    if(operation==='adopt') {browserUrl(entry.page.url());entry.managed=true;return this.#tabInfo(entry);}
    if(operation==='close') {await dispatched();await entry.page.close({runBeforeUnload:false});return {tab_id:entry.id,closed:true,verification:'passed'};}
    if(operation==='reconcile') {
      if(!entry.uncertain||!entry.freshRead) fail('FRESH_READ_REQUIRED','Inspect this tab after its uncertain action before reconciling.');
      entry.uncertain=null;return {tab_id:entry.id,reconciled:true,note:args.recovery_note};
    }
    this.#ready(entry);
    if(operation==='activate') {await dispatched();await entry.page.bringToFront();return {...await this.#tabInfo(entry),activation_requested:true};}
    if(operation==='navigate') {const url=browserUrl(args.url);await dispatched();await entry.page.goto(url,{waitUntil:'domcontentloaded',timeout});}
    if(operation==='back') {await dispatched();await entry.page.goBack({waitUntil:'domcontentloaded',timeout});}
    if(operation==='forward') {await dispatched();await entry.page.goForward({waitUntil:'domcontentloaded',timeout});}
    if(operation==='reload') {await dispatched();await entry.page.reload({waitUntil:'domcontentloaded',timeout});}
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
    // Query already-matched nodes directly. elementHandle() polls animation frames, which
    // can be throttled in a normal background Chrome tab when noDefaults preserves user settings.
    const handles=await locator.elementHandles();
    if(handles.length!==1) {await Promise.all(handles.map(handle=>handle.dispose().catch(()=>{})));fail(handles.length?'AMBIGUOUS_TARGET':'STALE_BROWSER_REF','The unique target changed before it could be pinned.');}
    const handle=handles[0];
    const info=await handle.evaluate(elementInfo);
    if(!info) {await handle.dispose();fail('STALE_BROWSER_REF','Target detached.');}
    return {handle,info,owned:true};
  }
  #ref(entry,handle,info) {const ref='b'+(++this.refSeq);entry.refs.set(ref,{handle,identity:identity(info),expires:Date.now()+60000,revision:entry.revision});return ref;}
  async #read(entry,args) {
    for(const [ref,saved] of entry.refs) if(saved.expires<Date.now()) {entry.refs.delete(ref);void saved.handle.dispose().catch(()=>{});}
    if(entry.refs.size>=400) {for(const ref of entry.refs.values()) void ref.handle.dispose().catch(()=>{});entry.refs.clear();}
    const root=await this.#resolve(entry,args,{root:true});const limit=args.limit??50,max=args.max_chars??16000,includeText=args.include_text??true;
    try {
      if(root.info.password) fail('PASSWORD_FIELD','Password content is excluded.');
      const textData=await root.handle.evaluate((element,{max,includeText})=>{
        const scroll=element.tagName==='BODY'?element.ownerDocument.scrollingElement:element;
        const scrollBox={x:scroll.scrollLeft,y:scroll.scrollTop,width:scroll.clientWidth,height:scroll.clientHeight,content_width:scroll.scrollWidth,content_height:scroll.scrollHeight};
        if(!includeText) return {text:'',length:0,scroll:scrollBox};
        const text=element.innerText??element.textContent??'';
        return {text:text.slice(0,max+1),length:text.length,scroll:scrollBox};
      },{max,includeText});
      const result={...await this.#tabInfo(entry),source:'dom',text:includeText?bounded(textData.text,max):'',text_complete:includeText?textData.length<=max:false,...(includeText?{}:{text_omitted:true}),scroll:textData.scroll,frames:this.#frameList(entry),frames_complete:entry.page.frames().length<=80,scope:'selected DOM root/frame; bounds are relative to that frame viewport; other frames are listed, not flattened',image_count:0};
      if(args.operation==='snapshot') {
        const selector='a[href],button,input:not([type=hidden]),textarea,select,[role],[contenteditable=true],summary';
        const batch=await root.handle.evaluateHandle((rootEl,{selector,limit})=>{
          const items=[]; let scanned=0, overflow=false;
          const readInfo=el=>{
            if(!el.isConnected) return null;
            const tag=el.tagName.toLowerCase(), type=(el.getAttribute('type')??'').toLowerCase();
            const role=el.getAttribute('role')||({button:'button',a:'link',textarea:'textbox',select:'combobox',summary:'button',input:type==='checkbox'?'checkbox':type==='radio'?'radio':['button','submit','reset'].includes(type)?'button':'textbox'}[tag]??tag);
            const labelled=(el.getAttribute('aria-labelledby')??'').split(/\s+/).map(id=>el.ownerDocument.getElementById(id)?.textContent??'').join(' ').trim();
            const name=el.getAttribute('aria-label')||labelled||[...(el.labels??[])].map(label=>label.innerText).join(' ').trim()||(['button','a','summary'].includes(tag)?el.innerText:'')||el.getAttribute('placeholder')||el.getAttribute('title')||'';
            const password=tag==='input'&&type==='password';
            const value=password?undefined:(tag==='input'||tag==='textarea'||tag==='select')?String(el.value??'').slice(0,500):undefined;
            const box=el.getBoundingClientRect(); const style=getComputedStyle(el);
            if(!(box.width>0&&box.height>0&&style.visibility!=='hidden'&&style.display!=='none')) return null;
            return {tag,type,role,name:name.slice(0,500),password,value,checked:typeof el.checked==='boolean'?el.checked:undefined,disabled:!!el.disabled||el.getAttribute('aria-disabled')==='true',visible:true,href:el.closest('a[href]')?.href??null,bounds:{x:Math.round(box.x),y:Math.round(box.y),width:Math.round(box.width),height:Math.round(box.height)}};
          };
          const walk=node=>{
            if(node.shadowRoot) walk(node.shadowRoot);
            for(const child of node.querySelectorAll('*')) {
              if(overflow) return;
              if(child.matches(selector)) {
                if(scanned>=300) {overflow=true; return;}
                scanned++;
                const info=readInfo(child);
                if(info) {
                  if(items.length>=limit) {overflow=true; return;}
                  items.push({el:child,info});
                }
              }
              if(child.shadowRoot) walk(child.shadowRoot);
            }
          };
          walk(rootEl);
          return {elements:items.map(item=>item.el),meta:{infos:items.map(item=>item.info),complete:!overflow}};
        },{selector,limit});
        const metaHandle=await batch.getProperty('meta');
        const {infos,complete}=await metaHandle.jsonValue();
        await metaHandle.dispose();
        const elementsHandle=await batch.getProperty('elements');
        const props=await elementsHandle.getProperties();
        await elementsHandle.dispose();
        await batch.dispose();
        const items=[];
        const snapMap=new Map();
        for(let i=0;i<infos.length;i++) {
          const prop=props.get(String(i));
          const handle=prop?.asElement()??null;
          if(!handle) {await prop?.dispose().catch(()=>{}); continue;}
          const info=infos[i];
          const item={ref:this.#ref(entry,handle,info),role:info.role,name:info.name};
          if(info.value!==undefined) item.value=info.value;
          if(info.checked!==undefined) item.checked=info.checked;
          if(info.disabled) item.disabled=true;
          if(info.password) item.password=true;
          item.bounds=info.bounds;
          items.push(item);
          const key=item.role+'|'+item.name+'|'+item.bounds.x+','+item.bounds.y;
          if(!snapMap.has(key)) snapMap.set(key, item);
        }
        for(const [k,prop] of props) {
          const idx=Number(k);
          if(!Number.isInteger(idx)||idx<0||idx>=infos.length) await prop.dispose().catch(()=>{});
        }
        result.items=items; result.controls_complete=complete;
        entry.lastSnapshot={url:entry.page.url(),items:snapMap};
      }
      entry.freshRead=true;return result;
    } finally {if(root.owned) await root.handle.dispose();}
  }
  async #observeAfter(entry, args) {
    const mode=args.observe_after;
    if(mode!=='diff' && mode!=='snapshot') return {};
    const settle=args.settle_ms??100;
    if(settle>0) await sleep(settle);
    if(entry.page.isClosed() || entry.dialog) return {};
    try {
      browserUrl(entry.page.url());
      const prev=entry.lastSnapshot;
      const snap=await this.#read(entry,{operation:'snapshot',limit:50,include_text:false});
      if(mode==='diff' && prev && prev.url===snap.url) {
        const added=[], changed=[], seenKeys=new Set();
        for(const item of snap.items) {
          const key=item.role+'|'+item.name+'|'+item.bounds.x+','+item.bounds.y;
          seenKeys.add(key);
          const old=prev.items.get(key);
          if(!old) added.push(item);
          else if(old.value!==item.value || old.checked!==item.checked || old.disabled!==item.disabled) changed.push(item);
        }
        let removedCount=0;
        for(const oldKey of prev.items.keys()) if(!seenKeys.has(oldKey)) removedCount++;
        return {observation:{mode:'diff',url:snap.url,title:snap.title,scroll:snap.scroll,added,changed,removed_count:removedCount,unchanged_count:snap.items.length-added.length-changed.length,controls_complete:snap.controls_complete}};
      }
      return {observation:{mode:'full',url:snap.url,title:snap.title,scroll:snap.scroll,items:snap.items,controls_complete:snap.controls_complete}};
    } catch(err) {
      return {observation_error:err.code??'OBSERVATION_FAILED'};
    }
  }
  async #act(entry,args,timeout,dispatched) {
    if(['click_point','mouse_move','click_mark'].includes(args.action)) {
      const capture=entry.captures.get(args.capture_id);entry.captures.delete(args.capture_id);
      if(!capture||capture.expires<Date.now()||capture.revision!==entry.revision) fail('STALE_CAPTURE','Point token is expired, consumed or belongs to another tab.');
      const state=await this.#stamp(entry);
      if(JSON.stringify(state)!==JSON.stringify(capture.state)) fail('STALE_CAPTURE','Page/viewport/scroll changed after capture.');
      let x=args.x, y=args.y;
      if(args.action==='click_mark') {
        const mark=capture.marks?.get(args.mark);
        if(!mark) fail('INVALID_ARGUMENT',`Mark ${args.mark} does not exist in this capture.`);
        x=mark.x; y=mark.y;
      } else if(x>=capture.width||y>=capture.height) {
        fail('STALE_CAPTURE','Coordinates are outside this image.');
      }
      if(args.action!=='click_mark') {x=x*(capture.scaleX??1);y=y*(capture.scaleY??1);}
      await dispatched();
      if(args.action==='mouse_move') await entry.page.mouse.move(x,y);
      else await entry.page.mouse.click(x,y);
      entry.captures.clear();
      const obs=await this.#observeAfter(entry,args);
      return {tab_id:entry.id,delivery:'sent',verification:'not_requested',...obs};
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
        const viewport=entry.page.viewportSize()??await entry.page.evaluate(()=>({width:innerWidth,height:innerHeight}));
        if(!sourceBox||!endBox||[sourceBox,endBox].some(box=>box.x<0||box.y<0||box.x+box.width>viewport.width||box.y+box.height>viewport.height)) fail('NOT_ACTIONABLE','Scroll both drag endpoints fully into this tab viewport first.');
        await dispatched();await entry.page.mouse.move(sourceBox.x+sourceBox.width/2,sourceBox.y+sourceBox.height/2);
        try {await entry.page.mouse.down();await entry.page.mouse.move(endBox.x+endBox.width/2,endBox.y+endBox.height/2,{steps:12});} finally {await entry.page.mouse.up().catch(()=>{});}
      } else {
        let dialogWake;const dialogOpened=new Promise(resolve=>{dialogWake=resolve;});entry.dialogWake=dialogWake;
        const execute=async()=>{
          await dispatched();const opts={timeout};
          switch(args.action) {
            case 'click':await target.handle.click(opts);break;
            case 'double_click':await target.handle.dblclick(opts);break;
            case 'hover':await target.handle.hover(opts);break;
            case 'fill':await target.handle.fill(args.text,opts);break;
            case 'type':await target.handle.type(args.text,opts);break;
            case 'press':await target.handle.press(args.key.split('+').map(part=>({Ctrl:'Control',Esc:'Escape'}[part]??part)).join('+'),opts);break;
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
      const obs=await this.#observeAfter(entry,args);
      return {tab_id:entry.id,delivery:'sent',verification:'not_requested',note:'Read the target state or use browser_wait to verify the requested effect.',...obs};
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
  async #capture(entry,args,timeout) {
    const maxMarks=args.max_marks??40;
    const rawMarks=args.annotate ? await entry.page.evaluate(maxMarks=>{
      const selector='a[href],button,input:not([type=hidden]),textarea,select,[role],[contenteditable=true],summary';
      const out=[], seen=[];
      const vw=window.innerWidth, vh=window.innerHeight;
      const walk=node=>{
        if(out.length>=maxMarks) return;
        if(node.shadowRoot) walk(node.shadowRoot);
        for(const el of node.querySelectorAll('*')) {
          if(out.length>=maxMarks) return;
          if(el.matches(selector)) {
            const box=el.getBoundingClientRect(), style=getComputedStyle(el);
            if(box.width>=6&&box.height>=6&&style.visibility!=='hidden'&&style.display!=='none') {
              const x0=Math.max(0,box.left), y0=Math.max(0,box.top), x1=Math.min(vw,box.right), y1=Math.min(vh,box.bottom);
              if(x1-x0>=6&&y1-y0>=6) {
                const cx=Math.round((x0+x1)/2), cy=Math.round((y0+y1)/2);
                if(!seen.some(p=>Math.abs(p.x-cx)<=5&&Math.abs(p.y-cy)<=5)) {
                  seen.push({x:cx,y:cy});
                  const tag=el.tagName.toLowerCase(), type=(el.getAttribute('type')??'').toLowerCase();
                  const role=el.getAttribute('role')||({button:'button',a:'link',textarea:'textbox',select:'combobox',summary:'button',input:type==='checkbox'?'checkbox':type==='radio'?'radio':['button','submit','reset'].includes(type)?'button':'textbox'}[tag]??tag);
                  const labelled=(el.getAttribute('aria-labelledby')??'').split(/\s+/).map(id=>document.getElementById(id)?.textContent??'').join(' ').trim();
                  const name=(el.getAttribute('aria-label')||labelled||[...(el.labels??[])].map(l=>l.innerText).join(' ').trim()||(['button','a','summary'].includes(tag)?el.innerText:'')||el.getAttribute('placeholder')||el.getAttribute('title')||'').slice(0,120);
                  out.push({mark:out.length+1,role,name,cx,cy,bounds:{x:Math.round(x0),y:Math.round(y0),width:Math.round(x1-x0),height:Math.round(y1-y0)}});
                }
              }
            }
          }
          if(el.shadowRoot) walk(el.shadowRoot);
        }
      };
      walk(document.body||document.documentElement);
      return out;
    }, maxMarks) : null;
    const state=await this.#stamp(entry),revision=entry.revision;
    // Keep the DOM intact: default caret-hiding/masking injects temporary styles and breaks
    // mutation validation. Browser password inputs render their normal masked characters.
    let bytes=await entry.page.screenshot({type:'png',fullPage:false,caret:'initial',scale:'css',timeout});
    if(entry.revision!==revision||JSON.stringify(state)!==JSON.stringify(await this.#stamp(entry))) fail('CAPTURE_CHANGED','The page changed during capture. No point token was issued.');
    const fitted=fitPng(bytes);bytes=fitted.image;
    const width=fitted.width,height=fitted.height,scaleX=state.width/width,scaleY=state.height/height;
    if(rawMarks&&rawMarks.length>0) bytes=annotatePng(bytes,rawMarks.map(mark=>({...mark,bounds:{x:Math.round(mark.bounds.x/scaleX),y:Math.round(mark.bounds.y/scaleY),width:Math.round(mark.bounds.width/scaleX),height:Math.round(mark.bounds.height/scaleY)}})));
    const capture_id='bcap-'+randomUUID();entry.captures.clear();
    entry.captures.set(capture_id,{state,revision,width,height,scaleX,scaleY,expires:Date.now()+10000,marks:rawMarks?new Map(rawMarks.map(m=>[m.mark,{x:m.cx,y:m.cy,role:m.role,name:m.name}])):null});
    entry.freshRead=true;
    return {tab_id:entry.id,capture_id,width,height,source_viewport:{width:state.width,height:state.height},...(rawMarks?{marks:rawMarks.map(m=>({mark:m.mark,role:m.role,name:m.name}))}:{}),image:bytes.toString('base64'),coordinate_system:'returned image pixels mapped to this tab viewport; single use; 10s',memory_only:true};
  }
  async #dialog(entry,args,dispatched) {
    if(!entry.dialog) {if(args.operation==='inspect') return {tab_id:entry.id,dialog:null};fail('DIALOG_NOT_FOUND','This tab has no pending JavaScript dialog.');}
    const dialog=entry.dialog;
    if(args.operation==='inspect') return {tab_id:entry.id,dialog:{type:dialog.type(),message:bounded(dialog.message(),2000),default_value:bounded(dialog.defaultValue(),1000)}};
    if(args.prompt_text!==undefined&&dialog.type()!=='prompt') fail('INVALID_ARGUMENT','prompt_text requires a prompt dialog.');
    await dispatched();
    entry.dialog=null;
    let dialogWake; const dialogOpened=new Promise(resolve=>{dialogWake=resolve;}); entry.dialogWake=dialogWake;
    try {
      if(args.operation==='accept') await dialog.accept(args.prompt_text);else await dialog.dismiss();
      if(entry.pending) {
        const pending=entry.pending; entry.pending=null;
        try { await Promise.race([pending, dialogOpened]); }
        catch(err) { if(err?.name!=='TimeoutError') throw err; }
      }
    } finally { entry.dialogWake=null; }
    if(entry.dialog) return {tab_id:entry.id,handled:true,action:args.operation,delivery:'sent',verification:'pending_dialog',dialog:{type:entry.dialog.type(),message:bounded(entry.dialog.message(),2000)}};
    return {tab_id:entry.id,handled:true,action:args.operation,delivery:'sent'};
  }
  stop(code='USER_STOPPED') {this.interrupted=code;this.generation++;return this.#dispose(false);}
  async #dispose(endScope) {
    if(this.closing) {await this.closing;if(endScope){this.tabs.clear();this.pages=new WeakMap();this.scoped=false;this.sessionId=null;this.interrupted=null;this.persistentProfile=null;this.starterPage=null;this.handoff=null;}return;}
    const browser=this.browser,context=this.context,isPersistent=!!this.persistentProfile,isAttached=this.attached;
    this.browser=null;this.context=null;this.starterPage=null;
    for(const entry of this.tabs.values()) this.#invalidate(entry);
    this.closing=(async()=>{
      const target=isPersistent?context:browser;
      if(target) {
        if(isAttached) {
          const pages=[...this.tabs.values()].map(tab=>tab.page).filter(page=>!page.isClosed());
          await Promise.allSettled(pages.map(page=>page.close({runBeforeUnload:false})));
          if(pages.some(page=>!page.isClosed())) {this.browser=browser;this.context=context;fail('BROWSER_CLOSE_UNCONFIRMED','An agent work tab did not close. Personal Chrome was left open; the session remains scoped.');}
        }
        try {await target.close();}
        catch {if(browser?.isConnected()){this.browser=browser;this.context=context;fail('BROWSER_CLOSE_UNCONFIRMED','Owned browser closure was not confirmed. The browser scope remains locked.');}}
        if(browser?.isConnected()){this.browser=browser;this.context=context;fail('BROWSER_CLOSE_UNCONFIRMED','Owned browser is still connected; scope was not released.');}
      }
    })();
    try {await this.closing;} finally {this.closing=null;if(endScope&&!this.browser?.isConnected()){this.tabs.clear();this.pages=new WeakMap();this.scoped=false;this.sessionId=null;this.interrupted=null;this.persistentProfile=null;this.handoff=null;}}
  }
  async close() {this.generation++;await this.#dispose(true);if(!this.bridge.closed) await this.bridge.request('system',{operation:'control_end'}).catch(()=>{});}
}
