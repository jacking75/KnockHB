'use strict';
// File-only planning check. Never launches UI, services, UAC, input, or a network probe.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const requiredCases=['consent-view','input','local-stop'];
const hash40=/^[a-f0-9]{40}$/,hash64=/^[a-f0-9]{64}$/;
function validatePlan(p){
  if(!p||p.version!==2||p.runtimeArmEnabled!==false)throw Error('Only version 2 blocked preparation plans are accepted; legacy 1800-second plans are retired');
  for(const key of ['totalSeconds','executionSeconds','cleanupSeconds','recoveryReserveSeconds','reportSeconds','retryCount','inventoryMaxAgeSeconds','evidenceMaxAgeSeconds'])if(!Number.isSafeInteger(p[key])||p[key]<0)throw Error('Invalid integer budget: '+key);
  if(p.totalSeconds!==900||p.targetSeconds!==600||p.executionSeconds!==300||p.cleanupSeconds!==240||p.recoveryReserveSeconds!==300||p.reportSeconds!==60||p.retryCount!==0||p.executionSeconds+p.cleanupSeconds+p.recoveryReserveSeconds+p.reportSeconds!==p.totalSeconds)throw Error('The entire campaign must fit 300+240+300+60 seconds, target 600 seconds, with no retries');
  if(p.inventoryMaxAgeSeconds!==900||p.evidenceMaxAgeSeconds!==86400||!Array.isArray(p.cases)||p.cases.length!==requiredCases.length)throw Error('Inventory/evidence freshness or selected cases drift');
  let end=0;const evidenceIds=new Set();
  for(let i=0;i<p.cases.length;i++){
    const c=p.cases[i];if(c.id!==requiredCases[i]||c.start!==end||!Number.isSafeInteger(c.end)||c.end<=c.start||c.end-c.start>300)throw Error('Case gap/overlap/order/unbounded slot');
    if(!Array.isArray(c.validation)||!c.validation.length||!c.validation.every(x=>/^V\d{2}$/.test(x))||!Array.isArray(c.final)||!c.final.length||!c.final.every(x=>/^F\d{2}$/.test(x))||!Array.isArray(c.evidence)||!c.evidence.length)throw Error('Case acceptance mapping/evidence missing');
    for(const e of c.evidence){if(typeof e!=='string'||!e||evidenceIds.has(e))throw Error('Duplicate/missing observation ID');evidenceIds.add(e);}end=c.end;
  }
  if(end!==p.executionSeconds||p.cases[0].end!==120||p.cases[1].end!==240)throw Error('Selected LAN scope deadline drift');
  for(const key of ['requiredEvidence','cleanupOwners'])if(!Array.isArray(p[key])||!p[key].length||p[key].some(x=>typeof x!=='string'||!x)||new Set(p[key]).size!==p[key].length)throw Error('Evidence/cleanup requirements missing or duplicate');
  return p;
}
function assessReadiness(p,r,{now=Date.now(),verifyArtifact=()=>false}={}){
  validatePlan(p);const missing=['R30-05 runtime controller/product cleanup integration and external return are not accepted; file checks cannot authorize Arm'];
  const check=(ok,label)=>{if(!ok)missing.push(label);};
  check(r?.version===2,'readiness version');check(hash40.test(r?.candidateCommit||''),'candidate commit');check(hash40.test(r?.candidateTree||''),'candidate tree');
  check(typeof r?.environmentId==='string'&&r.environmentId.length>0,'environment ID');check(r?.humanReviewed===true,'actual operator review');
  check(r?.runtimeGuardImplemented===true&&r?.runtimeGuardRehearsed===true,'independent deadline/cleanup rehearsal');check(!!r?.operator&&!!r?.recoveryOperator,'operators');
  const recent=(s,max)=>{const at=Date.parse(s);return Number.isFinite(at)&&at<=now&&now-at<=max*1000;};
  check(recent(r?.inventoryObservedAtUtc,p.inventoryMaxAgeSeconds),'fresh actual inventory');
  check(Array.isArray(r?.twoPcInventory)&&r.twoPcInventory.length===2&&r.twoPcInventory.every(x=>x?.id&&x?.role&&x?.session&&x?.desktop&&x?.imageHash),'two PC/session/desktop inventory');
  check(Array.isArray(r?.selectedVariants)&&r.selectedVariants.length===p.cases.length&&p.cases.every(c=>r.selectedVariants.filter(x=>x.caseId===c.id&&x.subcaseIds?.length&&x.stepsArtifact).length===1),'fixed variants and exact steps');
  check(Array.isArray(r?.priorSubcaseEvidence)&&r.priorSubcaseEvidence.length>0,'prior unselected F subcase coverage');
  check(Array.isArray(r?.resourceOwners)&&p.cleanupOwners.every(id=>r.resourceOwners.filter(x=>x.id===id&&x.ownerArtifact&&x.restoreArtifact).length===1),'complete owned cleanup/return plan');
  const entries=Array.isArray(r?.evidence)?r.evidence:[];
  check(new Set(entries.map(e=>e.id)).size===entries.length,'duplicate readiness evidence');
  for(const id of p.requiredEvidence){
    const all=entries.filter(e=>e?.id===id);const e=all[0];check(all.length===1&&e?.result==='PASS'&&e.candidateCommit===r?.candidateCommit&&e.candidateTree===r?.candidateTree&&e.environmentId===r?.environmentId&&recent(e.observedAtUtc,p.evidenceMaxAgeSeconds)&&hash64.test(e.artifactSha256||'')&&verifyArtifact(e), 'verified matching evidence: '+id);
  }
  return {status:'BLOCKED_BEFORE_START',planValid:true,runtimeArmAllowed:false,missing,scope:p.profile,executionSeconds:p.executionSeconds,totalSeconds:p.totalSeconds};
}
function readJson(file){const stat=fs.statSync(file);if(stat.size>262144)throw Error('JSON size limit');return JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''));}
function main(args){
  if(args.length!==3)throw Error('Usage: node scripts/check-e2e-readiness.cjs <plan.json> <readiness.json> <new .artifacts/output.json>');
  const root=path.resolve(__dirname,'..'),artifacts=path.join(root,'.artifacts'),output=path.resolve(args[2]);
  if(!output.startsWith(artifacts+path.sep)||fs.existsSync(output))throw Error('New output inside .artifacts required');
  const p=readJson(args[0]),r=readJson(args[1]);
  const verifyArtifact=e=>{
    try{const file=path.resolve(root,e.artifactPath);if(!file.startsWith(artifacts+path.sep))return false;
      let current=root;for(const part of path.relative(root,file).split(path.sep)){current=path.join(current,part);if(fs.lstatSync(current).isSymbolicLink())return false;}
      if(!fs.realpathSync(file).startsWith(fs.realpathSync(artifacts)+path.sep)||!fs.statSync(file).isFile()||fs.statSync(file).size>1048576)return false;
      return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')===e.artifactSha256;
    }catch{return false;}
  };
  const result=assessReadiness(p,r,{verifyArtifact});fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(result,null,2),{encoding:'utf8',flag:'wx'});
  process.stdout.write(JSON.stringify({status:result.status,planValid:result.planValid,missing:result.missing.length,output})+'\n');process.exitCode=2;
}
module.exports={validatePlan,assessReadiness};if(require.main===module){try{main(process.argv.slice(2));}catch(e){process.stderr.write(e.message+'\n');process.exitCode=1;}}
