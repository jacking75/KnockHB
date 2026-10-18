'use strict';
// Preparation primitive only. Product service/Job/recovery ownership is NOT implemented here.
const {fork,spawn}=require('node:child_process');
const monoMs=()=>Number(process.hrtime.bigint()/1000000n);
class Deadline {
  constructor(totalMs,clock=monoMs){
    if(!Number.isSafeInteger(totalMs)||totalMs<=0||totalMs>900000)throw Error('Invalid global budget');
    this.clock=clock;this.start=clock();this.end=this.start+totalMs;this.last=this.start;
  }
  remaining(){const now=this.clock();if(!Number.isFinite(now)||now<this.last)throw Error('Monotonic clock regressed');this.last=now;return Math.max(0,this.end-now);}
  limit(ms){if(!Number.isSafeInteger(ms)||ms<=0)throw Error('Invalid call budget');return Math.min(ms,this.remaining());}
}
// The guardian owns a directly spawned leaf child, never an arbitrary PID supplied by a caller.
// Do not use for product helpers, services or descendant-producing commands until Job integration.
function runLeaf(file,args,{deadline,timeoutMs=10000,leaseMs=2000,maxOutputBytes=65536}={}){
  if(!(deadline instanceof Deadline))return Promise.reject(Error('Shared global deadline required'));
  const remaining=deadline.limit(timeoutMs);
  if(!remaining)return Promise.resolve({reason:'deadline',code:null,output:''});
  return new Promise((resolve,reject)=>{
    const guardian=fork(__filename,['--guardian'],{stdio:['ignore','ignore','ignore','ipc'],windowsHide:true});
    let result,beat,outer;
    const stop=()=>{clearInterval(beat);clearTimeout(outer);};
    guardian.on('error',e=>{stop();reject(e);});
    guardian.on('message',m=>{if(m?.type==='result')result=m.result;});
    guardian.on('exit',()=>{stop();resolve(result||{reason:'guardian-lost',code:null,output:''});});
    guardian.send({type:'start',file,args,budgetMs:remaining,deadlineAtMs:Math.min(deadline.end,monoMs()+remaining),leaseMs,maxOutputBytes});
    beat=setInterval(()=>{if(guardian.connected)guardian.send({type:'heartbeat'});},Math.max(10,Math.floor(leaseMs/4)));
    // Reporting timeout never asserts that external resources are clean.
    outer=setTimeout(()=>{if(guardian.connected)guardian.send({type:'stop'});},remaining);
  });
}
function guard(){
  let child,timer,started=false,stopping=false,reason='exit',output='',bytes=0,leaseAt=monoMs(),end;
  const stop=why=>{
    if(stopping)return;stopping=true;reason=why;
    if(child&&!child.killed)child.kill();
    else if(!child)finish(null);
  };
  const finish=code=>{
    clearInterval(timer);
    const result={reason,code,output};
    if(process.connected)process.send({type:'result',result},()=>process.disconnect());
  };
  process.on('disconnect',()=>stop('controller-lost'));
  process.on('message',m=>{
    if(m?.type==='heartbeat'){leaseAt=monoMs();return;}
    if(m?.type==='stop'){stop('deadline');return;}
    if(m?.type!=='start'||started){stop('invalid-command');return;}
    started=true;
    if(typeof m.file!=='string'||!Array.isArray(m.args)||!m.args.every(x=>typeof x==='string')||
      !Number.isSafeInteger(m.budgetMs)||m.budgetMs<1||m.budgetMs>900000||!Number.isSafeInteger(m.deadlineAtMs)||
      !Number.isSafeInteger(m.leaseMs)||m.leaseMs<40||m.leaseMs>10000||
      !Number.isSafeInteger(m.maxOutputBytes)||m.maxOutputBytes<1||m.maxOutputBytes>1048576){stop('invalid-command');return;}
    // hrtime uses the machine's monotonic clock; no UTC or fresh per-child budget.
    end=Math.min(m.deadlineAtMs,monoMs()+m.budgetMs);
    if(monoMs()>=end){stop('deadline');return;}
    child=spawn(m.file,m.args,{stdio:['ignore','pipe','pipe'],windowsHide:true,shell:false});
    const append=data=>{bytes+=data.length;if(bytes>m.maxOutputBytes){stop('output-limit');return;}output+=data.toString();};
    child.stdout.on('data',append);child.stderr.on('data',append);
    child.on('error',()=>{reason='spawn-error';});
    child.on('close',finish);
    timer=setInterval(()=>{const now=monoMs();if(now>=end)stop('deadline');else if(now-leaseAt>=m.leaseMs)stop('controller-lease-expired');},10);
  });
}
module.exports={Deadline,runLeaf};
if(require.main===module&&process.argv[2]==='--guardian')guard();
