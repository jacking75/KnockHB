'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const {fork}=require('node:child_process');
const path=require('node:path');
const {Deadline,runLeaf}=require('../../scripts/e2e-deadline.cjs');
test('shared monotonic budget cannot reset or exceed 900 seconds',()=>{
  let tick=100;const d=new Deadline(900000,()=>tick);
  tick+=899995;assert.equal(d.limit(10000),5);tick+=5;assert.equal(d.remaining(),0);
  tick--;assert.throws(()=>d.remaining());assert.throws(()=>new Deadline(900001));
});
test('UTC changes do not extend a monotonic deadline',()=>{
  let tick=0;const d=new Deadline(100,()=>tick),old=Date.now;
  try{Date.now=()=>-999999999;tick=50;assert.equal(d.remaining(),50);Date.now=()=>999999999;tick=100;assert.equal(d.remaining(),0);}finally{Date.now=old;}
});
test('leaf success, failed launch, hang, output flood and expired budget', {timeout:15000},async()=>{
  const ok=await runLeaf(process.execPath,['-e','process.stdout.write("observed")'],{deadline:new Deadline(4000)});
  assert.equal(ok.reason,'exit');assert.equal(ok.code,0);assert.equal(ok.output,'observed');
  const bad=await runLeaf('does-not-exist-e2e-leaf',[],{deadline:new Deadline(4000)});assert.equal(bad.reason,'spawn-error');
  const hang=await runLeaf(process.execPath,['-e','setInterval(()=>{},1000)'],{deadline:new Deadline(4000),timeoutMs:300});assert.equal(hang.reason,'deadline');
  const flood=await runLeaf(process.execPath,['-e','process.stdout.write("x".repeat(10000))'],{deadline:new Deadline(4000),maxOutputBytes:128});assert.equal(flood.reason,'output-limit');
  let now=0;const d=new Deadline(1,()=>now);now=1;assert.equal((await runLeaf(process.execPath,[],{deadline:d})).reason,'deadline');
});
test('independent guardian terminates its leaf when controller heartbeats stop',{timeout:5000},async()=>{
  const guard=fork(path.resolve(__dirname,'../../scripts/e2e-deadline.cjs'),['--guardian'],{stdio:['ignore','ignore','ignore','ipc'],windowsHide:true});
  const result=await new Promise((resolve,reject)=>{
    let observed;guard.on('error',reject);guard.on('message',m=>{observed=m.result;});guard.on('exit',()=>resolve(observed));
    guard.send({type:'start',file:process.execPath,args:['-e','setInterval(()=>{},1000)'],budgetMs:3000,deadlineAtMs:Number(process.hrtime.bigint()/1000000n)+3000,leaseMs:200,maxOutputBytes:100});
  });
  assert.equal(result.reason,'controller-lease-expired');
});
