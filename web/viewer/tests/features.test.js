import test from 'node:test';
import assert from 'node:assert/strict';
import { PERMISSIONS,CHANNELS,FeatureSession,textHash,validateSnapshot } from '../features.js';
import { ViewerConnection } from '../connection.js';
import {readFileSync} from 'node:fs';
test('shared C#/JS full permission and channel contract',()=>{const fixture=JSON.parse(readFileSync(new URL('../../../tests/fixtures/features.json',import.meta.url),'utf8'));assert.deepEqual([...PERMISSIONS],fixture.permissions);assert.deepEqual([...CHANNELS],fixture.channels);});
const id='0123456789abcdef0123456789abcdef';
function session(permissions=['clipboard']) {
  const received=[],sent=[],ended=[];
  const connection={live:()=>!ended.length,dataReady:true,permissions,sessionId:'sid',permissionGeneration:1,now:()=>0,
    channels:new Map([['chat',{readyState:'open',bufferedAmount:0,send:text=>sent.push(JSON.parse(text))}]]),onData:m=>received.push(m),end:reason=>ended.push(reason)};
  return {features:new FeatureSession(connection),connection,received,sent,ended};
}
function envelope(t,seq=1){return {v:1,t,sessionId:'sid',generation:1,permissionGeneration:1,id,seq};}
test('clipboard responses require the exact pending direction and SHA256 before exposing text',async()=>{
  const s=session();s.features.pending.set(id,{action:'get',at:0});const text='한글😀';
  await assert.rejects(()=>s.features.receive('chat',{...envelope('clipboard-text'),text,hash:'0'.repeat(64)}));assert.equal(s.received.length,0);
  await s.features.receive('chat',{...envelope('clipboard-text'),text,hash:await textHash(text)});assert.equal(s.received[0].text,text);
  const hash=await textHash(text);await assert.rejects(()=>s.features.receive('chat',{...envelope('clipboard-text',2),text,hash}));
});
test('clipboard cancellation after asynchronous hashing cannot send or expose stale data',async()=>{
  const s=session(),pending=s.features.clipboard('set','secret');s.connection.live=()=>false;
  assert.equal(await pending,false);assert.equal(s.sent.length,0);
  s.connection.live=()=>true;s.features.pending.set(id,{action:'get',at:0});const hash=await textHash('response');
  const receiving=s.features.receive('chat',{...envelope('clipboard-text'),text:'response',hash});s.connection.live=()=>false;
  await receiving;assert.equal(s.received.length,0);
});
test('chat permission does not permit clipboard and bounded rate prevents queue flooding',()=>{
  const s=session(['chat']);for(let i=0;i<5;i++)assert.equal(s.features.chat('<script>literal</script>'),true);
  assert.equal(s.features.chat('extra'),false);assert.equal(s.sent.length,5);assert.equal(s.features.ready('clipboard'),false);
  assert.equal(s.features.chat('\ud800'),false);assert.equal(s.features.chat('\0'),false);
});
test('JSON escaping limits clipboard payload without consuming the reliable sequence',async()=>{
  const s=session();assert.equal(await s.features.clipboard('set','\u0001'.repeat(8192)),false);assert.equal(s.sent.length,0);
  assert.equal(await s.features.clipboard('set','한'.repeat(5000)),true);assert.equal(s.sent[0].seq,1);assert.equal(s.features.pending.size,1);
});
test('telemetry fields omit secret metadata and reject unsafe numbers, arbitrary paths and oversized lists',()=>{
  const value={utc:new Date().toISOString(),cpuPercent:null,memory:null,disks:null,network:null,errors:null,startup:null,services:null};
  assert.equal(validateSnapshot(value),value);
  for(const bad of [{...value,ip:'127.0.0.1'},{...value,cpuPercent:101},{...value,memory:{totalBytes:Number.MAX_SAFE_INTEGER+1,availableBytes:1}},{...value,startup:['C:\\secret']},{...value,errors:Array(21).fill({})}])assert.throws(()=>validateSnapshot(bad));
});
