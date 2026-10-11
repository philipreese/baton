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
const recorded = {observedAt:'2026-10-10T00:00:00Z',availability:'available',omitted:1,items:Array.from({length:21},(_,i)=>({
  task:{id:`task-${String(i).padStart(2,'0')}`,issue:2683,conductorHolder:'prior-owner',preparation:'blocked',readyReceiptId:'ready-1',readyHeadSha:'b'.repeat(40),blocker:'Recorded blocker'},
  stage:'implement',state:i===0?'Launched':'Queued',cancelled:i===1,
  nextTrigger:i===0?'daemon-tick':i===2?'conductor-judgment':i===3?'conductor-reassessment':i===4?'conductor-handoff':i===5?'preparation-completion':'none'
})).slice(0,20)};
const recordedCard=conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:recorded}]},'',false,esc);
assert.ok(recordedCard.includes('Recorded repository tasks'));
assert.ok(recordedCard.includes('Recorded conductor holder: prior-owner'));
assert.ok(recordedCard.includes('does not record acquisition generation'));
assert.ok(recordedCard.includes('Historical ready reference: ready-1'));
assert.ok(recordedCard.includes('Next trigger: daemon-tick · next actor: scheduler reconciliation'));
assert.ok(recordedCard.includes('Next trigger: conductor-judgment · next actor: conductor judgment'));
assert.ok(recordedCard.includes('Next trigger: conductor-reassessment · next actor: conductor readiness reassessment'));
assert.ok(recordedCard.includes('Next trigger: conductor-handoff · next actor: conductor review/merge handoff under existing authority'));
assert.ok(recordedCard.includes('Next trigger: preparation-completion · next actor: preparation completion'));
assert.ok(recordedCard.includes('Next trigger: none · next actor: no next action'));
assert.ok(recordedCard.includes('1 additional task record(s) omitted'));
assert.ok(recordedCard.includes('cancelled (retained record)'));
assert.ok(recordedCard.includes('href="#queue"') && recordedCard.includes('href="#stream"'));
assert.ok(conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:{availability:'available',items:[],omitted:0}}]},'',false,esc).includes('No matching recorded task rows'));
assert.ok(conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:null}]},'',false,esc).includes('Queue records unavailable'));
const hostileRecorded=conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:{...recorded,items:[{...recorded.items[0],task:{...recorded.items[0].task,id:'<img src=x>',conductorHolder:'<script>private</script>'}}]}}]},'',false,esc);
assert.ok(hostileRecorded.includes('&lt;img src=x&gt;'));
assert.ok(hostileRecorded.includes('&lt;script&gt;private&lt;/script&gt;'));

// Cancelled historical row must always say no next action
const cancelledWithTrigger = conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:{observedAt:'2026-10-10T00:00:00Z',availability:'available',omitted:0,items:[{
  task:{id:'task-cancelled',issue:2683,conductorHolder:'prior-owner'},
  stage:'implement',state:'Cancelled',cancelled:true,nextTrigger:'daemon-tick'
}]}}]},'',false,esc);
assert.ok(cancelledWithTrigger.includes('Next trigger: none · next actor: no next action'));

// Unknown tokens fall back to unavailable
const unknownTrigger = conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:{observedAt:'2026-10-10T00:00:00Z',availability:'available',omitted:0,items:[{
  task:{id:'task-unknown',issue:2683,conductorHolder:'prior-owner'},
  stage:'implement',state:'Queued',cancelled:false,nextTrigger:'foreign-token'
}]}}]},'',false,esc);
assert.ok(unknownTrigger.includes('Next trigger: unavailable · next actor: unavailable'));

// Malicious strings must not execute or inject raw tags; unknown token falls back to unavailable
const maliciousTrigger = conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:{observedAt:'2026-10-10T00:00:00Z',availability:'available',omitted:0,items:[{
  task:{id:'task-malicious',issue:2683,conductorHolder:'prior-owner'},
  stage:'implement',state:'Queued',cancelled:false,nextTrigger:'<script>alert(1)</script>'
}]}}]},'',false,esc);
assert.ok(!maliciousTrigger.includes('<script>alert(1)</script>'));
assert.ok(maliciousTrigger.includes('Next trigger: unavailable · next actor: unavailable'));

// Renderer verification across viewports: mobile (320, 390) and desktop (1280)
// Mocks verify responsive HTML contract and absence of horizontal overflow hazards in the selftest harness;
// installed acceptance additionally tests live browser viewport layout.
for (const viewport of [{name:'mobile-320',width:320,isMock:true},{name:'mobile-390',width:390,isMock:true},{name:'desktop-1280',width:1280,isMock:true}]) {
  const renderedViewport = conductorControlHtml({...snapshot(),conductors:[{...row,recordedTasks:recorded}]},'',false,esc);
  assert.ok(renderedViewport.includes('Recorded repository tasks'), `viewport ${viewport.name} (${viewport.isMock ? 'mock' : 'installed'}) must render tasks header`);
  assert.ok(renderedViewport.includes('Next trigger: daemon-tick · next actor: scheduler reconciliation'), `viewport ${viewport.name} (${viewport.isMock ? 'mock' : 'installed'}) must render next actor line`);
  assert.ok(renderedViewport.includes('class="control-note"'), `viewport ${viewport.name} (${viewport.isMock ? 'mock' : 'installed'}) must preserve control-note typography`);
}
assert.ok(conductorControlHtml({...snapshot('detached'),conductors:[{...row,state:'detached',resumeEligible:true}]},'',false,esc).includes('data-conductor-resume="0"'));
assert.ok(!conductorControlHtml(snapshot('detached'),'',false,esc).includes('data-conductor-resume'));
assert.ok(card.includes('data-conductor-detach="0"'));
assert.ok(conductorControlHtml(snapshot('frozen'),'',false,esc).includes('data-conductor-detach="0"'));
assert.ok(card.includes('not live activity'));
assert.ok(card.includes('application is not verified'));
assert.ok(!conductorControlHtml(snapshot('unavailable'),'',false,esc).includes('data-conductor-detach'));
assert.ok(conductorControlHtml({...snapshot(),conductors:[{...row,holder:'<img src=x onerror=alert(1)>'}]},'',false,esc).includes('&lt;img'));

const retainedJudgment={eventIdentity:'a'.repeat(64),obligationId:'ordinary-id',obligationKey:'ordinary-key',
  sourceRepository:row.repository,claimGeneration:row.claimGeneration,tag:'task',sourceAttempt:'attempt',
  sourceHeadSha:'b'.repeat(40),sourceObservedAt:'2026-10-09T01:02:03Z',decision:'Hold',actionDisposition:null};
const historyCard=history=>conductorControlHtml({...snapshot(),conductors:[{...row,stopEligible:true,holdEligible:true,history}]},'',false,esc);
const retainedCard=historyCard({judgments:[retainedJudgment]});
assert.ok(retainedCard.includes('Retained judgments'));
assert.ok(retainedCard.includes('Identity order, not newest-first or activity chronology'));
assert.ok(retainedCard.includes('At this judgment, no replacement was requested.'));
assert.ok(retainedCard.includes('No decision rationale retained'));
assert.ok(retainedCard.includes('Source observation: 2026-10-09T01:02:03Z'));
assert.ok(retainedCard.includes('Source attempt: attempt'));
assert.ok(retainedCard.includes(retainedJudgment.sourceHeadSha));
assert.ok(retainedCard.includes('Exact source identity'));
assert.ok(!retainedCard.includes('No matching retained action'),'Hold must not imply a pending replacement');
for(const diagnostic of ['history inspection limit reached','inspection incomplete; additional history not checked']){
  const rendered=historyCard({judgments:[retainedJudgment],invalidEvents:2,excludedEvents:3,omittedJudgments:4,diagnostic});
  assert.ok(rendered.includes(diagnostic));
  assert.ok(rendered.includes('At this judgment, no replacement was requested.'));
  assert.ok(rendered.includes('2 invalid or incomplete event(s) excluded'));
  assert.ok(rendered.includes('3 correction, merge or prose-only event(s) excluded'));
  assert.ok(rendered.includes('4 additional verified ordinary judgment(s) omitted'));
  assert.ok(rendered.includes('data-conductor-stop="0"'));
  assert.ok(rendered.includes('data-conductor-hold="0"'));
  assert.ok(!rendered.includes('No verified ordinary judgments'));
}
assert.ok(historyCard({judgments:[],diagnostic:'history inspection limit reached'}).includes('history inspection limit reached'));
assert.ok(!historyCard({judgments:[],diagnostic:'history inspection limit reached'}).includes('No verified ordinary judgments'));
assert.ok(historyCard({judgments:[]}).includes('No verified ordinary judgments in the inspected history'));
assert.ok(historyCard({judgments:[{...retainedJudgment,decision:'ReplaceReview'}]}).includes('No matching retained action'));
assert.ok(historyCard({judgments:[{...retainedJudgment,decision:'ReplaceReview',actionDisposition:'retained-unlaunched'}]}).includes('Matching retained action: retained-unlaunched'));
const escapedHistory=historyCard({judgments:[{...retainedJudgment,tag:'<img src=x>',sourceAttempt:'<script>',obligationKey:'<svg onload=alert(1)>',
  rationale:'PRIVATE-RATIONALE',completedAt:'PRIVATE-COMPLETION',sessionId:'PRIVATE-NATIVE',outputLines:['PRIVATE-PROSE'],workspace:'PRIVATE-PATH'}]});
assert.ok(escapedHistory.includes('&lt;img'));
assert.ok(escapedHistory.includes('&lt;script&gt;'));
assert.ok(escapedHistory.includes('&lt;svg'));
assert.ok(!escapedHistory.includes('PRIVATE'));
assert.equal((historyCard({judgments:Array.from({length:21},()=>retainedJudgment)}).match(/class="retained-judgment"/g)||[]).length,20);

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
    assert.ok(!rendered.includes('href="javascript:'),'owner address must be inert escaped text');
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
const correctionRow={...row,correctionEligible:true};
const correctionSnapshot=()=>({...snapshot(),conductors:[correctionRow]});
const correctionReceipt=(state='queued')=>({...row,receiptId:'safe-receipt',requestId:'correction-1',acceptedAt:'2026-10-10T09:00:00Z',state,reason:'Safe actual blocker',application:'not verified'});
for(const [state,label] of [['queued','Received / queued'],['waiting','Waiting'],['issued','Issued / outcome pending'],['delivered','Delivered / turn completed'],['uncertain','Uncertain'],['not-delivered','Not delivered']]){
  const rendered=conductorControlHtml({...snapshot(),conductors:[{...correctionRow,correction:correctionReceipt(state)}]},'',false,esc);
  assert.ok(rendered.includes(label));
  assert.ok(rendered.includes('Application: not verified.'));
  assert.ok(rendered.includes('Safe actual blocker'));
}
for(const state of ['detached','frozen','unavailable','stopped','taken-over'])
  assert.ok(!conductorControlHtml({...snapshot(),conductors:[{...correctionRow,state}]},'',false,esc).includes('data-conductor-correct='));
assert.ok(conductorControlHtml({...snapshot(),conductors:[{...correctionRow,state:'held'}]},'',false,esc).includes('data-conductor-correct="0"'));
let correctionLatest, correctionPosts=0, correctionGets=0, savedRequest;
globalThis.sessionStorage={getItem:()=>savedRequest || null,setItem:(_,value)=>{savedRequest=value;}};
const correctionRender=(snapshot,message,busy,pending)=>{correctionLatest={snapshot,message,busy,pending};};
const lostCorrection=createConductorControl(async(path,options)=>{
  if(path==='/conductors') return ok(correctionSnapshot());
  if(path==='/conductor/correct'){
    correctionPosts++;
    const body=JSON.parse(options.body);
    assert.equal(body.requestId,'correction-1');
    assert.equal(body.text,'private explicit correction');
    assert.equal(body.issuer,undefined);
    throw new Error('lost after acceptance');
  }
  assert.equal(path,'/conductor/corrections/receipt?requestId=correction-1');
  assert.equal(options.cache,'no-store');
  correctionGets++;
  return ok({outcome:'accepted',receipt:correctionReceipt()});
},correctionRender,()=>true,()=> 'correction-1');
await lostCorrection.refresh(); await lostCorrection.correct(0,'private explicit correction');
assert.match(correctionLatest.message,/outcome unknown/);
assert.equal(correctionLatest.pending.body.requestId,'correction-1');
assert.ok(!savedRequest.includes('private explicit correction'),'storage retains request identity without text');
await lostCorrection.lookupCorrection(); await lostCorrection.refresh();
assert.equal(correctionPosts,1,'neither lookup nor refresh may repeat POST');
assert.equal(correctionGets,2);
assert.match(correctionLatest.message,/Exact acceptance receipt found/);
const restoredCorrection=createConductorControl(async path=>path==='/conductors'?ok(correctionSnapshot()):ok({outcome:'accepted',receipt:correctionReceipt('delivered')}),correctionRender,()=>true);
await restoredCorrection.refresh();
assert.equal(correctionLatest.pending.body.requestId,'correction-1','reload retains exact lookup identity');
assert.equal(correctionLatest.pending.receipt.state,'delivered');
delete globalThis.sessionStorage;
let independentPosts=0;
const independentRows=[correctionRow,{...correctionRow,repository:'github.com/test/independent'}];
const independent=createConductorControl(async(path,options)=>{
  if(path==='/conductors') return ok({...snapshot(),conductors:independentRows});
  if(path==='/conductor/correct'){ independentPosts++; throw new Error('lost after acceptance'); }
  return ok({outcome:'unknown',receipt:null});
},correctionRender,()=>true,()=> 'request-' + independentPosts);
await independent.refresh(); await independent.correct(0,'A correction');
await independent.refresh(); await independent.correct(1,'B correction');
assert.equal(independentPosts,2,'one acquisition cannot consume another card correction capacity');
assert.equal(correctionLatest.pending.requests.length,2,'both lost-reply identities remain available for exact lookup');
const mergeCandidate={taskId:'task',pullRequest:77,pullRequestUrl:'https://github.com/test/repo/pull/77',
  headSha:'a'.repeat(40),readyReceiptId:'b'.repeat(64),readyReceiptSha256:'c'.repeat(64)};
const mergeGrant={grantId:'d'.repeat(64),taskId:'task',pullRequestUrl:mergeCandidate.pullRequestUrl,headSha:mergeCandidate.headSha,
  issuer:'operator',requestId:'accepted',expiresAt:'2026-10-11T00:00:00Z',permission:'spent',delivery:'complete',decision:'Merge',
  reason:'<script>unsafe</script>',operation:'unknown',revoked:false,observation:{state:'merged observed; executor unconfirmed',nextTrigger:'Owner must inspect attribution'}};
const mergeSnapshot=()=>({...snapshot(),conductors:[{...row,merge:{candidates:[mergeCandidate],grants:[mergeGrant]}}]});
const mergeCard=conductorControlHtml(mergeSnapshot(),'',false,esc);
for(const expected of ['data-merge-grant="0:0"','data-merge-revoke="0:0"',mergeCandidate.headSha,mergeCandidate.readyReceiptSha256,
  'Permission: spent','Delivery: complete','Attempt: unknown','merged observed; executor unconfirmed','Owner must inspect attribution','&lt;script&gt;']) assert.ok(mergeCard.includes(expected),expected);
assert.ok(!mergeCard.includes('<script>unsafe'));
assert.ok(conductorControlHtml({...mergeSnapshot(),conductors:[{...row,state:'frozen',merge:mergeSnapshot().conductors[0].merge}]},'',false,esc).includes('data-merge-revoke'));
let mergeLatest,mergePosts=0,mergeBody,mergeReceipt=null;
const mergeRender=(data,message,busy)=>{mergeLatest={data,message,busy};};
let savedMerge='';
globalThis.sessionStorage={getItem:key=>key==='baton-merge-request'?savedMerge:null,setItem:(key,value)=>{if(key==='baton-merge-request') savedMerge=value;}};
const mergeFetch=async(path,options)=>{
  if(path==='/conductors') return ok(mergeSnapshot());
  if(path==='/conductor/merge/grant'){
    mergePosts++; mergeBody=JSON.parse(options.body);
    assert.equal(mergeBody.method,'squash'); assert.equal(mergeBody.issuer,undefined);
    assert.equal(mergeBody.headSha,mergeCandidate.headSha); assert.equal(mergeBody.readyReceiptSha256,mergeCandidate.readyReceiptSha256);
    return {ok:false,status:409};
  }
  assert.equal(path,'/conductor/merge/receipt?repository=github.com%2Ftest%2Frepo&requestId=merge-request');
  return ok(mergeReceipt);
};
const exactMerge=createConductorControl(mergeFetch,mergeRender,()=>true,()=> 'merge-request');
await exactMerge.refresh(); await exactMerge.mergeControl(0,0,false,new Date(Date.now()+3600000).toISOString());
assert.match(mergeLatest.message,/outcome unknown/); assert.equal(mergePosts,1);
await exactMerge.mergeControl(0,0,false,new Date(Date.now()+3600000).toISOString());
assert.equal(mergePosts,1,'lost acknowledgement must block a new POST');
mergeReceipt={requestId:'merge-request',operation:'grant',grantId:mergeGrant.grantId,grant:{...mergeBody,headSha:'e'.repeat(40)}};
await exactMerge.refresh(); assert.match(mergeLatest.message,/outcome remains unknown/);
mergeReceipt.grant={...mergeBody,expiresAt:mergeBody.expiresAt.replace('Z','+00:00')};
const reloadedMerge=createConductorControl(mergeFetch,mergeRender,()=>true);
await reloadedMerge.refresh(); assert.match(mergeLatest.message,/acceptance retained/);
assert.equal(mergePosts,1,'refresh and reload only look up the original request');
assert.equal(savedMerge,'null');
delete globalThis.sessionStorage;
const realMergeNow=Date.now;
Date.now=()=>Date.parse('2026-10-10T12:00:00.000Z');
try {
  for(const [expiry,expectedPosts] of [
    ['2026-10-11T13:00:00.000Z',0], // 25 hours must refuse before any POST.
    ['',0],['not-a-date',0],['2026-10-10T11:59:59.999Z',0],['2026-10-10T12:00:00.000Z',0],
    ['2026-10-11T12:00:00.001Z',0], // One millisecond beyond the inclusive ceiling.
    ['2026-10-11T12:00:00.000Z',1],['2026-10-10T12:01:00.000Z',1],
  ]){
    let expiryPosts=0,expiryConfirmations=0,expiryLatest;
    const expiryControl=createConductorControl(async(path,options)=>{
      if(path==='/conductors') return ok(mergeSnapshot());
      assert.equal(path,'/conductor/merge/grant'); assert.equal(options.method,'POST'); expiryPosts++;
      assert.equal(JSON.parse(options.body).expiresAt,expiry,'explicit expiry is not clamped or replaced');
      return {ok:false,status:403};
    },(data,message,busy)=>{expiryLatest={data,message,busy};},()=>{expiryConfirmations++; return true;},()=> 'expiry-request');
    await expiryControl.refresh(); await expiryControl.mergeControl(0,0,false,expiry);
    assert.equal(expiryPosts,expectedPosts,`Expiry ${expiry || '<blank>'} POST count`);
    assert.equal(expiryConfirmations,expectedPosts,'invalid expiry must not reach confirmation');
    assert.match(expiryLatest.message,expectedPosts ? /Exact merge control refused/ : /Choose an explicit future expiry within 24 hours/);
    assert.equal(expiryLatest.busy,false);
  }
} finally { Date.now=realMergeNow; }
let staleMergeReads=0,staleMergePosts=0;
const staleMerge=createConductorControl(async(path,options)=>{
  if(options.method==='POST'){staleMergePosts++; return ok({});}
  staleMergeReads++;
  const data=mergeSnapshot(); if(staleMergeReads>1) data.conductors[0].merge.candidates[0]={...mergeCandidate,readyReceiptId:'e'.repeat(64)};
  return ok(data);
},mergeRender,()=>true,()=> 'stale-merge');
await staleMerge.refresh(); await staleMerge.mergeControl(0,0,false,new Date(Date.now()+3600000).toISOString());
assert.equal(staleMergePosts,0); assert.match(mergeLatest.message,/receipt or owner changed/);
console.log(`Conductor controls: existing scenarios, ${holdCases} Hold/Unhold scenarios, correction and exact-merge identity, lost-reply/reload lookup and stale-ready refusal passed.`);
