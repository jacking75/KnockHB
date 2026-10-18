'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const {validatePlan,assessReadiness}=require('../../scripts/check-e2e-readiness.cjs');
const plan=require('./campaign-plan.json'),template=require('./readiness.template.json');
test('900-second selected LAN plan is valid and unverified readiness cannot ARM',()=>{
  validatePlan(plan);const result=assessReadiness(plan,template);assert.equal(result.runtimeArmAllowed,false);assert.equal(result.status,'BLOCKED_BEFORE_START');assert(result.missing.some(x=>x.includes('matching evidence')));
});
test('budget overflow, negative, retry and fake enabled controller are rejected',()=>{
  for(const edit of [{version:1},{totalSeconds:1800},{totalSeconds:901},{targetSeconds:900},{cleanupSeconds:0},{executionSeconds:1380},{reportSeconds:-1},{retryCount:1},{runtimeArmEnabled:true},{inventoryMaxAgeSeconds:86400}])assert.throws(()=>validatePlan({...plan,...edit}));
});
test('timeline gap, duplicate case/evidence and missing outcome evidence are rejected',()=>{
  for(const alter of [p=>p.cases[1].start++,p=>p.cases[1].id=p.cases[0].id,p=>p.cases[0].evidence=[],p=>p.cases[1].evidence=p.cases[0].evidence,p=>p.cases[2].end=301]){const p=structuredClone(plan);alter(p);assert.throws(()=>validatePlan(p));}
});
test('stale, future and different candidate evidence cannot count as readiness',()=>{
  const now=Date.parse('2026-10-08T12:00:00Z'),r={...template,candidateCommit:'a'.repeat(40),candidateTree:'b'.repeat(40),environmentId:'lab',evidence:plan.requiredEvidence.map(id=>({id,result:'PASS',candidateCommit:'c'.repeat(40),candidateTree:'b'.repeat(40),environmentId:'lab',observedAtUtc:'2026-10-08T11:00:00Z',artifactSha256:'d'.repeat(64)}))};
  for(const date of ['2026-10-06T11:00:00Z','2026-10-08T13:00:00Z','2026-10-08T11:00:00Z']){r.evidence.forEach(e=>{e.observedAtUtc=date;e.candidateCommit=date==='2026-10-08T11:00:00Z'?'c'.repeat(40):r.candidateCommit;});const result=assessReadiness(plan,r,{now,verifyArtifact:()=>true});assert.equal(result.missing.filter(x=>x.startsWith('verified matching evidence:')).length,plan.requiredEvidence.length);}
});
test('duplicate or tampered artifact evidence cannot be accepted',()=>{
  const now=Date.parse('2026-10-08T12:00:00Z'),r={...template,candidateCommit:'a'.repeat(40),candidateTree:'b'.repeat(40),environmentId:'lab',evidence:plan.requiredEvidence.map(id=>({id,result:'PASS',candidateCommit:'a'.repeat(40),candidateTree:'b'.repeat(40),environmentId:'lab',observedAtUtc:'2026-10-08T11:00:00Z',artifactSha256:'d'.repeat(64)}))};
  let calls=0;let result=assessReadiness(plan,r,{now,verifyArtifact:e=>{calls++;return e.id!=='native-media';}});assert.equal(calls,plan.requiredEvidence.length);assert(result.missing.includes('verified matching evidence: native-media'));assert(!result.missing.includes('verified matching evidence: viewer-observer'));
  r.evidence.push(r.evidence[0]);result=assessReadiness(plan,r,{now,verifyArtifact:()=>true});assert(result.missing.includes('duplicate readiness evidence'));assert.equal(result.runtimeArmAllowed,false);
});
test('filled booleans and matching references still cannot enable the missing runtime guard',()=>{
  const now=Date.parse('2026-10-08T12:00:00Z'),r={...template,candidateCommit:'a'.repeat(40),candidateTree:'b'.repeat(40),environmentId:'lab',inventoryObservedAtUtc:'2026-10-08T11:59:00Z',humanReviewed:true,runtimeGuardImplemented:true,runtimeGuardRehearsed:true,operator:'tester',recoveryOperator:'local',twoPcInventory:[1,2].map(id=>({id:String(id),role:'prepared',session:1,desktop:'default',imageHash:'x'})),selectedVariants:plan.cases.map(c=>({caseId:c.id,subcaseIds:['selected'],stepsArtifact:'test'})),priorSubcaseEvidence:['test'],resourceOwners:plan.cleanupOwners.map(id=>({id,ownerArtifact:'test',restoreArtifact:'test'})),evidence:plan.requiredEvidence.map(id=>({id,result:'PASS',candidateCommit:'a'.repeat(40),candidateTree:'b'.repeat(40),environmentId:'lab',observedAtUtc:'2026-10-08T11:00:00Z',artifactSha256:'d'.repeat(64)}))};
  const result=assessReadiness(plan,r,{now,verifyArtifact:()=>true});assert.equal(result.runtimeArmAllowed,false);assert.equal(result.missing.length,1);assert(result.missing[0].includes('runtime controller'));
});
