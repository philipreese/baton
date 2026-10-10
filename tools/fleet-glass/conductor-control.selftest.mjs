import {readFileSync} from 'node:fs';
import assert from 'node:assert/strict';

const html = readFileSync(new URL('./glass.html', import.meta.url), 'utf8');
const extractControls = text => text.split(/\/\/ >>> CONDUCTOR-CONTROL-BEGIN\r?\n/)[1]?.split('// <<< CONDUCTOR-CONTROL-END')[0];
const source = extractControls(html);
assert.ok(source, 'tests must execute the actual shipped controls');
for (const newline of ['\n', '\r\n']) {
  const checkout = html.replace(/\r?\n/g, newline);
  assert.equal(extractControls(checkout)?.replace(/\r\n/g, '\n'), source.replace(/\r\n/g, '\n'),
    'shipped controls must be extracted identically from LF and CRLF checkouts');
}
const {createConductorControl,conductorControlHtml,wireConductorControl} = new Function(`${source}; return {createConductorControl,conductorControlHtml,wireConductorControl};`)();
const row = {repository:'github.com/test/repo',holder:'owner',claimGeneration:'generation',attachmentId:'attachment',state:'attached',adapter:'codex',model:'test',effort:'high',permissions:'Read only'};
const snapshot = (state='attached') => ({observedAt:'2026-10-10T00:00:00Z',conductors:[{...row,state}]});
const ok = value => ({ok:true,status:200,json:async()=>value});
const esc = value => String(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const card = conductorControlHtml(snapshot(),'',false,esc);
assert.ok(conductorControlHtml({...snapshot('detached'),conductors:[{...row,state:'detached',resumeEligible:true}]},'',false,esc).includes('data-conductor-resume="0"'));
assert.ok(!conductorControlHtml(snapshot('detached'),'',false,esc).includes('data-conductor-resume'));
assert.ok(card.includes('data-conductor-detach="0"'));
assert.ok(conductorControlHtml(snapshot('frozen'),'',false,esc).includes('data-conductor-detach="0"'));
assert.ok(card.includes('not live activity'));
assert.ok(card.includes('not available here yet'));
assert.ok(!conductorControlHtml(snapshot('unavailable'),'',false,esc).includes('data-conductor-detach'));
assert.ok(conductorControlHtml({...snapshot(),conductors:[{...row,holder:'<img src=x onerror=alert(1)>'}]},'',false,esc).includes('&lt;img'));

let latest, calls=[];
const render = (snapshot,message,busy) => {latest={snapshot,message,busy};};
let readCount=0;
const control=createConductorControl(async(path,options)=>{
  calls.push({path,options});
  return path==='/conductors' ? ok(snapshot(readCount++ ? 'detached':'attached')) : ok(null);
},render,()=>true);
await control.refresh();
await control.detach(0);
assert.equal(calls.length,3);
assert.equal(calls[1].path,'/conductor/detach');
assert.deepEqual(JSON.parse(calls[1].options.body),{repository:row.repository,holder:row.holder,claimGeneration:row.claimGeneration,attachmentId:row.attachmentId});
assert.equal(calls[0].options.cache,'no-store');
assert.match(latest.message,/delivery is detached/);
assert.equal(latest.busy,false);

for(const status of [403,409,500]){
  const rejected=createConductorControl(async path=>path==='/conductors'?ok(snapshot()):{ok:false,status},render,()=>true);
  await rejected.refresh(); await rejected.detach(0);
  assert.equal(latest.snapshot,null);
  assert.match(latest.message,status===500?/unknown/:/refused/);
}
const uncertain=createConductorControl(async path=>{
  if(path==='/conductors') return ok(snapshot());
  throw new Error('connection lost after possible commit');
},render,()=>true);
await uncertain.refresh(); await uncertain.detach(0);
assert.match(latest.message,/unknown/);
assert.equal(latest.snapshot,null);

let reads=0;
const acceptedButUnreadable=createConductorControl(async path=>{
  if(path==='/conductor/detach') return ok(null);
  if(reads++) throw new Error('offline');
  return ok(snapshot());
},render,()=>true);
await acceptedButUnreadable.refresh(); await acceptedButUnreadable.detach(0);
assert.match(latest.message,/accepted, but/);
assert.equal(latest.snapshot,null);

const malformed=createConductorControl(async()=>ok({observedAt:'bad',conductors:[]}),render,()=>true);
await malformed.refresh();
assert.equal(latest.snapshot,null);
assert.match(latest.message,/Cannot read/);

let declinedCalls=0;
const declined=createConductorControl(async()=>{declinedCalls++;return ok(snapshot());},render,()=>false);
await declined.refresh(); await declined.detach(0);
assert.equal(declinedCalls,1,'declining confirmation must not send a write');

let releaseWrite, writeCount=0;
const overlapping=createConductorControl(async path=>{
  if(path==='/conductors') return ok(snapshot());
  writeCount++;
  await new Promise(resolve=>{releaseWrite=resolve;});
  return ok(null);
},render,()=>true);
await overlapping.refresh();
const firstDetach=overlapping.detach(0);
await overlapping.detach(0);
assert.equal(writeCount,1,'double click must not submit a duplicate write');
releaseWrite(); await firstDetach;
assert.match(latest.message,/settings differ/,'an accepted request is not proof that current state is detached');

let handler;
const clicked=[];
wireConductorControl({addEventListener:(_,fn)=>handler=fn},{refresh:async()=>clicked.push('refresh'),detach:async n=>clicked.push(n)});
handler({target:{closest:selector=>selector==='[data-conductor-detach]'?{dataset:{conductorDetach:'0'}}:null}});
assert.deepEqual(clicked,[0]);
const newId='a'.repeat(32);
const detachedSnapshot=(eligible=true)=>({...snapshot('detached'),conductors:[{...row,state:'detached',resumeEligible:eligible}]});
assert.ok(!conductorControlHtml(detachedSnapshot(false),'',false,esc).includes('data-conductor-resume'));
assert.ok(!conductorControlHtml({...snapshot('frozen'),conductors:[{...row,state:'frozen',resumeEligible:true}]},'',false,esc).includes('data-conductor-resume'));
assert.ok(conductorControlHtml(detachedSnapshot(),'',true,esc).includes('data-conductor-resume="0" disabled'));
let confirmation, resumeReads=0, resumeWrites=0;
const resumedRow={...row,attachmentId:newId,state:'attached'};
const resumeControl=createConductorControl(async(path,options)=>{
  if(path==='/conductors') return ok(resumeReads++ ? {...snapshot(),conductors:[resumedRow]} : detachedSnapshot());
  resumeWrites++;
  assert.equal(path,'/conductor/resume');
  assert.deepEqual(JSON.parse(options.body),{repository:row.repository,holder:row.holder,claimGeneration:row.claimGeneration,attachmentId:row.attachmentId});
  assert.equal(options.cache,'no-store');
  return ok(resumedRow);
},render,text=>{confirmation=text;return true;});
await resumeControl.refresh(); await resumeControl.resume(0);
assert.equal(resumeWrites,1);
assert.equal(resumeReads,2);
assert.match(confirmation,/Only future handoffs/);
assert.match(confirmation,/not be replayed/);
assert.match(latest.message,/resumed for future handoffs only/);
for(const change of ['attachmentId','holder','claimGeneration','repository','state','unreadable','receipt']){
  let count=0;
  const mismatch=createConductorControl(async path=>{
    if(path!=='/conductors') return ok(change==='receipt'?{...resumedRow,attachmentId:row.attachmentId}:resumedRow);
    if(!count++) return ok(detachedSnapshot());
    if(change==='unreadable') throw new Error('offline');
    return ok({...snapshot(),conductors:[{...resumedRow,[change]:'foreign'}]});
  },render,()=>true);
  await mismatch.refresh(); await mismatch.resume(0);
  assert.doesNotMatch(latest.message,/delivery is resumed/,'success requires the new receipt and fresh GET to agree exactly');
  assert.match(latest.message,/accepted, but/);
}
for(const failure of [403,409,500,'lost']){
  let writes=0;
  const refused=createConductorControl(async path=>{
    if(path==='/conductors') return ok(detachedSnapshot());
    writes++;
    if(failure==='lost') throw new Error('lost after possible commit');
    return {ok:false,status:failure};
  },render,()=>true);
  await refused.refresh(); await refused.resume(0); await refused.resume(0);
  assert.equal(writes,1,'unknown/refused requests are never automatically replayed');
  assert.equal(latest.snapshot,null);
  assert.match(latest.message,failure===403?/not authorized/:failure===409?/busy, stale/:/unknown/);
}
let blockedWrites=0;
const ineligible=createConductorControl(async path=>{
  if(path==='/conductors') return ok(detachedSnapshot(false));
  blockedWrites++; return ok(resumedRow);
},render,()=>true);
await ineligible.refresh(); await ineligible.resume(0);
assert.equal(blockedWrites,0);
let resumeHandler;
wireConductorControl({addEventListener:(_,fn)=>resumeHandler=fn},{resume:async n=>clicked.push(`resume-${n}`)});
resumeHandler({target:{closest:selector=>selector==='[data-conductor-resume]'?{dataset:{conductorResume:'0'}}:null}});
assert.deepEqual(clicked,[0,'resume-0']);
assert.ok(html.includes('wireConductorControl(conductorPanel, conductorControl);'));
assert.ok(html.includes('void conductorControl.refresh();'));
console.log('Conductor controls: shipped render, exact mutation, refusal, uncertainty, refresh and click wiring passed.');
