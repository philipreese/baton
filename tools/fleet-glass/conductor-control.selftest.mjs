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
const controllable={...row,stopEligible:true,takeoverEligible:true};
const controlledSnapshot=(operation,body)=>({observedAt:snapshot().observedAt,conductors:[{
  ...controllable,state:operation==='stop'?'stopped':'taken-over',stopEligible:false,
  holder:operation==='stop'?row.holder:body.destinationHolder,claimGeneration:operation==='stop'?row.claimGeneration:'successor',
  historicalProvider:true,destinationAddress:body.destinationAddress,
  controls:[controlResult(operation,body)],actions:[{tag:'issued',state:'issued'}]
}]});
const controlResult=(operation,body)=>({receipt:{operation,request:body,issuer:'operator@example.test',
  resultHolder:operation==='takeover'?body.destinationHolder:body.holder,
  resultGeneration:operation==='takeover'?'successor':body.claimGeneration},cleanup:'complete'});
for(const operation of ['stop','takeover']){
  let body,reads=0,writes=0;
  const tested=createConductorControl(async(path,options)=>{
    if(path==='/conductors') {reads++;return ok(body?controlledSnapshot(operation,body):{...snapshot(),conductors:[controllable]});}
    writes++;body=JSON.parse(options.body);
    assert.equal(path,`/conductor/${operation}`);
    assert.equal(body.requestId,'exact-request');
    assert.equal(body.reason,'operator reason');
    assert.ok(!('issuer' in body));
    return ok(controlResult(operation,body));
  },render,text=>{confirmation=text;return true;},()=> 'exact-request');
  await tested.refresh();
  await tested[operation](0,{reason:' operator reason ',destinationHolder:'successor',destinationAddress:'javascript:alert(1)<img src=x>'});
  assert.equal(reads,3,'a fresh pre-confirmation GET and post-receipt GET are required');
  assert.equal(writes,1);
  assert.match(latest.message,/applied and confirmed/);
  assert.match(confirmation,operation==='stop'?/cannot Resume/:/No replacement hosted session/);
  const rendered=conductorControlHtml(latest.snapshot,'',false,esc);
  assert.ok(!rendered.includes('data-conductor-resume'));
  assert.ok(rendered.includes('Historical provider'));
  assert.ok(rendered.includes('request exact-request'));
  assert.ok(rendered.includes('Action issued: issued'));
  if(operation==='takeover'){
    assert.ok(rendered.includes('javascript:alert(1)&lt;img'));
    assert.ok(!rendered.includes('href='),'owner address must be inert escaped text');
  }
}
for(const failure of ['lost',403,409,500,'stale-get','bad-receipt','bad-final-get']){
  let body,reads=0,writes=0;
  const tested=createConductorControl(async(path,options)=>{
    if(path==='/conductors'){
      reads++;
      if(failure==='stale-get' && reads===2) return ok({...snapshot(),conductors:[{...controllable,claimGeneration:'foreign'}]});
      if(body && failure!=='bad-final-get') return ok(controlledSnapshot('stop',body));
      return ok({...snapshot(),conductors:[controllable]});
    }
    writes++;body=JSON.parse(options.body);
    if(failure==='lost') throw new Error('lost acknowledgement after fence');
    if(typeof failure==='number') return {ok:false,status:failure};
    return ok(controlResult('stop',failure==='bad-receipt'?{...body,requestId:'foreign'}:body));
  },render,()=>true,()=> 'retained-lost-request');
  await tested.refresh();await tested.stop(0,{reason:'reason'});
  assert.equal(writes,failure==='stale-get'?0:1);
  assert.doesNotMatch(latest.message,/applied and confirmed/);
  if(failure==='lost'){
    assert.match(latest.message,/unknown/);
    await tested.refresh();
    assert.match(latest.message,/Exact historical stop receipt found/);
    assert.equal(writes,1,'lost response reconciliation must never repeat a POST');
  }
}
let staleInputsWrites=0;
const emptyReason=createConductorControl(async path=>{
  if(path==='/conductors') return ok({...snapshot(),conductors:[controllable]});
  staleInputsWrites++;return ok(null);
},render,()=>true);
await emptyReason.refresh();await emptyReason.stop(0,{reason:' '});
await emptyReason.takeover(0,{reason:'reason',destinationHolder:row.holder,destinationAddress:'address'});
for(const destinationHolder of ['CORP\\alice','/alice','owner:/alice']){
  await emptyReason.takeover(0,{reason:'reason',destinationHolder,destinationAddress:'address'});
}
assert.equal(staleInputsWrites,0);
const priorAcquisition=conductorControlHtml({...snapshot(),conductors:[{...controllable,state:'prior-acquisition',historicalProvider:true,stopEligible:false,takeoverEligible:false,resumeEligible:false}]},'',false,esc);
assert.ok(priorAcquisition.includes('Retained provider from a prior acquisition'));
assert.ok(!priorAcquisition.includes('Ownership transferred'));
assert.ok(!priorAcquisition.includes('data-conductor-resume'));
let holdCases=0;
for(const operation of ['hold','unhold']){
  for(const state of ['attached','frozen','detached']){
    let body,writes=0;
    const initial={...controllable,state,held:operation==='unhold',controlRevision:7,holdEligible:operation==='hold',unholdEligible:operation==='unhold'};
    const current=()=>({...initial,held:operation==='hold',controlRevision:8,controls:[controlResult(operation,body)],admissionWait:'Next scheduler reconciliation; independent blockers remain.'});
    const tested=createConductorControl(async(path,options)=>{
      if(path==='/conductors') return ok({...snapshot(),conductors:[body?current():initial]});
      writes++;body=JSON.parse(options.body);
      assert.equal(path,`/conductor/${operation}`);
      assert.equal(body.expectedControlRevision,7);
      assert.ok(!('issuer' in body));
      return ok(controlResult(operation,body));
    },render,text=>{confirmation=text;return true;},()=> 'hold-request');
    await tested.refresh();await tested[operation](0,{reason:'operator reason'});
    assert.equal(writes,1);
    assert.match(latest.message,/applied and confirmed/);
    assert.equal(latest.snapshot.conductors[0].state,state,'Unhold cannot imply restored conversation health or attachment');
    const rendered=conductorControlHtml(latest.snapshot,'',false,esc);
    assert.ok(rendered.includes(`Historical ${operation} receipt`));
    assert.match(confirmation,operation==='hold'?/retain ownership and conversation/:/Detached, frozen, grants/);
    holdCases++;
  }
}
for(const failure of ['lost','newer-hold','stale-revision','changed-attachment']){
  let body,reads=0,writes=0;
  const initial={...controllable,state:'held',held:true,controlRevision:1,unholdEligible:true};
  const tested=createConductorControl(async(path,options)=>{
    if(path==='/conductors'){
      reads++;
      const changed=failure==='stale-revision' && reads===2?{controlRevision:3}:failure==='changed-attachment' && reads===2?{attachmentId:'new'}:{};
      return ok({...snapshot(),conductors:[body?{...initial,controlRevision:3,controls:[controlResult('unhold',body)]}:{...initial,...changed}]});
    }
    writes++;body=JSON.parse(options.body);
    if(failure==='lost') throw new Error('lost after durable receipt');
    return ok(controlResult('unhold',body));
  },render,()=>true,()=> 'unhold-B');
  await tested.refresh();await tested.unhold(0,{reason:'reason'});
  assert.equal(writes,['stale-revision','changed-attachment'].includes(failure)?0:1);
  assert.doesNotMatch(latest.message,/applied and confirmed/,'receipt B does not establish state after Hold C');
  if(failure==='lost'){
    await tested.refresh();
    assert.match(latest.message,/historical unhold receipt/);
    assert.equal(latest.snapshot.conductors[0].held,true);
    assert.equal(writes,1,'refresh must never automatically repeat the POST');
  }
  holdCases++;
}
const pendingVsIssued=conductorControlHtml({...snapshot(),conductors:[{...controllable,state:'held',held:true,unholdEligible:true,
  controls:[{receipt:{operation:'hold',appliedAt:'2026-10-10T07:00:00Z',issuer:'operator@example.test',request:{reason:'wait for source'}}}],
  actions:[{tag:'received',state:'received-response-pending-decision',holder:'owner',nextTrigger:'Unhold'}, {tag:'issued',state:'in-flight',holder:'owner'}]}]},'',false,esc);
assert.ok(pendingVsIssued.includes('received-response-pending-decision'));
assert.ok(pendingVsIssued.includes('Action issued: in-flight'));
assert.ok(pendingVsIssued.includes('2026-10-10T07:00:00Z'));
assert.ok(pendingVsIssued.includes('operator@example.test'));
assert.ok(pendingVsIssued.includes('wait for source'));
console.log(`Conductor controls: shipped render, exact mutation, refusal, uncertainty, refresh and click wiring passed; ${holdCases} additional Hold/Unhold scenarios passed.`);
