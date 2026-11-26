const str = (maxLength = 500) => ({ type: 'string', minLength: 1, maxLength });
const int = (minimum, maximum) => ({ type: 'integer', minimum, maximum });
const obj = (properties, required = []) => ({ type: 'object', properties, required, additionalProperties: false });
const id = str(120);
const locator = obj({ frame_id: id, role: str(60), name: str(500), exact: { type: 'boolean' }, test_id: str(300), selector: str(1000), text: str(500) });
const target = { ref: id, locator };
const timeout_ms = int(100, 10000);
const definitions = [
  ['browser_session', 'Connect to the running Chrome by default, using its current login. Chrome must enable and approve remote debugging. New work tabs only; existing personal tabs are not exposed. End/stop disconnects and closes only agent-created tabs. connection:isolated explicitly opts into a separate Chrome/Edge profile. handoff supports manual login/2FA.', obj({ operation: { type: 'string', enum: ['start','info','end','handoff','resume_handoff','list_profiles','clear_profile'] }, connection: {type:'string',enum:['existing','isolated'],default:'existing'}, browser: { type: 'string', enum: ['chrome','edge'] }, headless: { type: 'boolean' }, profile: { type: 'string', minLength: 1, maxLength: 64, pattern: '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$' }, reason: str(300) }, ['operation'])],
  ['browser_tabs', 'Create/list/info/navigate/back/forward/reload/activate/close owned tabs. Every existing-tab operation needs its returned tab_id; no active-tab fallback. Popups are quarantined until adopt. Reconcile uncertain delivery only after a fresh read and explicit recovery_note.', obj({ operation: { type: 'string', enum: ['new','list','info','navigate','back','forward','reload','activate','close','adopt','reconcile'] }, tab_id: id, url: str(4000), recovery_note: str(1000), timeout_ms }, ['operation'])],
  ['browser_read', 'Read DOM text and guarded element refs from exactly one managed tab/frame. Frames are listed explicitly. Password values excluded. Set include_text:false in snapshot for compact control-only reads. Refs expire on navigation/replacement/60s.', obj({ tab_id: id, operation: { type: 'string', enum: ['snapshot','text'] }, ...target, limit: int(1,100), max_chars: int(100,50000), include_text: { type: 'boolean' }, timeout_ms }, ['tab_id','operation'])],
  ['browser_act', 'One DOM/page action inside the specified tab, optionally auto-observing DOM changes via observe_after. Choose a guarded ref, unique locator, capture point, or Set-of-Marks mark. Duplicate matches fail. Unknown delivery requires fresh read + reconcile.', obj({ tab_id:id, action:{type:'string',enum:['click','double_click','hover','fill','type','press','check','uncheck','select_option','scroll','drag','click_point','mouse_move','click_mark']}, ...target, to_ref:id, text:{type:'string',maxLength:10000}, key:str(60), option:str(1000), direction:{type:'string',enum:['up','down','left','right']}, pixels:int(1,2000), capture_id:id, x:int(0,1599), y:int(0,1599), mark:int(1,60), observe_after:{type:'string',enum:['none','diff','snapshot']}, settle_ms:int(0,1500), timeout_ms }, ['tab_id','action'])],
  ['browser_wait', 'Wait for a tab URL, exact text/value/checked state, or target visibility. A timeout is a failed verification, never success. No navigation or input is retried.', obj({tab_id:id, condition:{type:'string',enum:['visible','hidden','text','value','checked','url']}, ...target, equals:{type:['string','boolean'],maxLength:10000}, contains:{type:'boolean'}, timeout_ms}, ['tab_id','condition'])],
  ['browser_capture', 'Return a bounded PNG of this tab viewport in RAM, with a 10s single-use token. Set annotate:true for numbered Set-of-Marks badges usable with browser_act click_mark. DOM-first; image is opt-in.', obj({tab_id:id,annotate:{type:'boolean'},max_marks:int(1,60),timeout_ms}, ['tab_id'])],
  ['browser_dialog', 'Inspect/accept/dismiss a JavaScript dialog belonging to this tab. No automatic acceptance. Page actions pause at a dialog; resolve it explicitly before continuing.', obj({tab_id:id,operation:{type:'string',enum:['inspect','accept','dismiss']},prompt_text:{type:'string',maxLength:2000}}, ['tab_id','operation'])]
];
export const browserTools = definitions.map(([name,description,inputSchema]) => ({name,description,inputSchema,annotations:{readOnlyHint:['browser_read','browser_wait'].includes(name),destructiveHint:name==='browser_act',openWorldHint:true}}));

export function validateBrowserCall(name, args) {
  const require = (...fields) => {for(const field of fields) if(args[field] === undefined) throw new Error(`${field} is required.`);};
  if (name === 'browser_session') {
    if(args.connection !== undefined && args.operation !== 'start') throw new Error('connection applies only to start.');
    if(args.operation === 'start' && args.connection !== 'isolated' && (args.headless === true || args.profile !== undefined || args.browser === 'edge')) throw new Error('Existing mode uses the running Chrome. Choose connection:isolated explicitly for headless, Edge or a named work profile.');
    if (args.operation !== 'start' && (args.browser !== undefined || args.headless !== undefined)) throw new Error('Browser/headless apply only to start.');
    if (args.profile !== undefined && !['start','clear_profile'].includes(args.operation)) throw new Error('profile applies only to start or clear_profile.');
    if (args.operation === 'clear_profile') require('profile');
    if (args.reason !== undefined && args.operation !== 'handoff') throw new Error('reason applies only to handoff.');
  }
  if (name === 'browser_tabs') {
    if (!['new','list'].includes(args.operation)) require('tab_id');
    if (['new','list'].includes(args.operation) && args.tab_id !== undefined) throw new Error('new/list do not take tab_id.');
    if (args.operation === 'navigate') require('url');
    if (args.url !== undefined && !['new','navigate'].includes(args.operation)) throw new Error('url requires new/navigate.');
    if (args.operation === 'reconcile') {require('recovery_note'); if(!args.recovery_note.trim()) throw new Error('A recovery note is required.');}
    else if (args.recovery_note !== undefined) throw new Error('recovery_note requires reconcile.');
  }
  if (args.locator !== undefined) {
    const choices = ['role','test_id','selector','text'].filter(key => args.locator[key] !== undefined);
    if (choices.length !== 1 || args.locator.name !== undefined && !args.locator.role || args.locator.exact !== undefined && !args.locator.role && !args.locator.text) throw new Error('locator needs exactly one role/test_id/selector/text filter; name applies to role.');
  }
  if (args.ref !== undefined && args.locator !== undefined) throw new Error('Choose ref or locator, not both.');
  if (name === 'browser_act') {
    const point = ['click_point','mouse_move'].includes(args.action);
    const isMark = args.action === 'click_mark';
    if (point) {require('capture_id','x','y'); if(args.ref || args.locator || args.mark !== undefined) throw new Error('Points use a capture token, not a DOM target.');}
    else if (isMark) {require('capture_id','mark'); if(args.ref || args.locator || args.x !== undefined || args.y !== undefined) throw new Error('click_mark uses capture_id and mark.');}
    else if (args.capture_id !== undefined || args.x !== undefined || args.y !== undefined || args.mark !== undefined) throw new Error('Point/mark fields require a point or click_mark action.');
    if (!point && !isMark && args.action !== 'scroll' && !args.ref && !args.locator) throw new Error('A guarded ref or locator is required.');
    if (['fill','type'].includes(args.action)) require('text'); else if(args.text !== undefined) throw new Error('text requires fill/type.');
    if (args.action === 'press') require('key'); else if(args.key !== undefined) throw new Error('key requires press.');
    if (args.action === 'select_option') require('option'); else if(args.option !== undefined) throw new Error('option requires select_option.');
    if (args.action === 'drag') require('ref','to_ref'); else if(args.to_ref !== undefined) throw new Error('to_ref requires drag.');
    if (args.action === 'scroll') require('direction'); else if(args.direction !== undefined || args.pixels !== undefined) throw new Error('direction/pixels require scroll.');
    if (args.key && !/^(?:(?:Control|Ctrl|Shift|Alt|Meta)\+){0,3}(?:[a-zA-Z0-9]|Enter|Tab|Escape|Esc|ArrowUp|ArrowDown|ArrowLeft|ArrowRight|Backspace|Delete|Home|End|PageUp|PageDown|Space|F(?:[1-9]|1\d|2[0-4]))$/.test(args.key)) throw new Error('Unsupported page key.');
    if (args.key) {
      const parts = args.key.split('+');
      const modifiers = parts.slice(0, -1).map(part => part === 'Ctrl' ? 'Control' : part);
      const finalKey = parts.at(-1).toLowerCase();
      if (new Set(modifiers).size !== modifiers.length) throw new Error('Duplicate modifier keys are unsupported.');
      if (((modifiers.includes('Control') || modifiers.includes('Meta')) && ['c','v','x','l','n','t','w','f4'].includes(finalKey))
        || (modifiers.includes('Alt') && ['tab','f4'].includes(finalKey))) {
        throw new Error('Use tab tools; global clipboard/browser shortcuts are excluded.');
      }
    }
  }
  if (name === 'browser_wait') {
    if(args.condition !== 'url' && !args.ref && !args.locator) throw new Error('This wait needs a target.');
    if(args.condition === 'url' && (args.ref || args.locator)) throw new Error('URL waits do not take an element target.');
    if(!['visible','hidden'].includes(args.condition)) require('equals');
    if(args.condition === 'checked' ? typeof args.equals !== 'boolean' : args.equals !== undefined && typeof args.equals !== 'string') throw new Error('equals has the wrong type.');
  }
  if(name === 'browser_dialog' && args.prompt_text !== undefined && args.operation !== 'accept') throw new Error('prompt_text applies only to accept.');
}
