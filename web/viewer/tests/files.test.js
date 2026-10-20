import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { Sha256 } from '../sha256.js';
import { safeFileName,FileSession,downloadHandle,maxDownloadBytes } from '../files.js';
test('파일 hash는 분할 경계와 빈 파일에서 표준 SHA-256과 같다',()=>{for(const size of [0,1,55,56,63,64,65,16387]){const bytes=Uint8Array.from({length:size},(_,i)=>i&255),h=new Sha256();for(let at=0;at<size;at+=13)h.update(bytes.subarray(at,at+13));assert.equal(h.hex(),createHash('sha256').update(bytes).digest('hex'));}});
test('파일 경로와 Windows 장치명은 거부한다',()=>{for(const name of ['../a','C:a','a:stream','a.','NUL.txt','COM¹.dat','a/b','a\\b'])assert.equal(safeFileName(name),false);assert.equal(safeFileName('한글😀.txt'),true);});
test('수락 전 본문과 다른 전송 ID를 거부한다',async()=>{const c={live:()=>true,dataReady:true,permissions:['file'],channels:new Map([['file',{readyState:'open',bufferedAmount:0}]]),sessionId:'s',permissionGeneration:1,now:()=>0,onData:()=>{}};const f=new FileSession(c);await assert.rejects(f.receive(new ArrayBuffer(33)));await assert.rejects(f.receive({v:1,t:'file-accept',id:'a'.repeat(32),sessionId:'s',generation:1,permissionGeneration:1,seq:1}));});

const first='a'.repeat(32),second='b'.repeat(32),sha=bytes=>createHash('sha256').update(bytes).digest('hex');
function fixture(){const sent=[],observed=[],ended=[];const c={live:()=>true,generation:1,dataReady:true,permissions:['file'],sessionId:'s',permissionGeneration:1,now:()=>0,onData:m=>observed.push(m),end:r=>ended.push(r),channels:new Map([['file',{readyState:'open',bufferedAmount:0,send:m=>sent.push(typeof m==='string'?JSON.parse(m):m)}]])};return {c,f:new FileSession(c),sent,observed,ended};}
function message(t,seq=1,id=first,fields={}){return {v:1,t,id,seq,sessionId:'s',generation:1,permissionGeneration:1,...fields};}
function offer(bytes,seq=1,id=first){return message('file-offer',seq,id,{name:'한글😀.dat',size:bytes.length,hash:sha(bytes)});}
function packet(bytes,sequence=0,id=first){const result=new Uint8Array(32+bytes.length),v=new DataView(result.buffer);result.set([82,65,70,84]);v.setUint16(4,1,true);result.set(id.match(/../g).map(x=>parseInt(x,16)),8);v.setUint32(24,sequence,true);v.setUint32(28,bytes.length,true);result.set(bytes,32);return result.buffer;}
function deferred(){let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};}

test('저장 API 없는 브라우저도 별도 수락과 검증 뒤 빈 파일·여러 청크를 다운로드한다',async()=>{
  for(const size of [0,16384*5+3]){
    const s=fixture(),bytes=Uint8Array.from({length:size},(_,i)=>i&255),saved=[];
    await s.f.receive(offer(bytes));assert.equal(s.sent.length,0);assert.equal(saved.length,0);
    await s.f.accept(downloadHandle('한글😀.dat',size,(name,blob)=>saved.push({name,blob})));
    for(let at=0;at<size;at+=16384)await s.f.receive(packet(bytes.subarray(at,at+16384),at/16384));
    assert.equal(saved.length,0);await s.f.receive(message('file-complete',2,first,{size,hash:sha(bytes)}));
    assert.equal(saved.length,1);assert.deepEqual(new Uint8Array(await saved[0].blob.arrayBuffer()),bytes);
    assert.equal(s.sent.at(-1).t,'file-received');assert.equal(s.f.current,null);
  }
});

test('대체 다운로드는 8MiB 상한·거절·hash 불일치·중복 청크에서 저장하지 않는다',async()=>{
  assert.throws(()=>downloadHandle('a',maxDownloadBytes+1));
  const s=fixture(),bytes=new Uint8Array([1,2,3]),saved=[];
  await s.f.receive(offer(bytes));await s.f.cancel('declined');assert.equal(s.sent.at(-1).t,'file-reject');
  await s.f.receive(offer(bytes,2,second));await s.f.accept(downloadHandle('a',3,(_,b)=>saved.push(b)));
  await s.f.receive(packet(new Uint8Array([1,2,4]),0,second));
  await assert.rejects(s.f.receive(packet(bytes,0,second)));
  await s.f.receive(message('file-complete',3,second,{size:3,hash:sha(bytes)}));
  assert.equal(saved.length,0);assert.equal(s.sent.at(-1).code,'hash-mismatch');assert.equal(s.f.current,null);
});

test('취소 뒤 완료된 upload hash는 이전 offer를 보내거나 다음 전송을 취소하지 않는다',async()=>{
  const s=fixture(),read=deferred(),entered=deferred();
  const uploading=s.f.offer({name:'old.dat',size:1,slice:()=>({arrayBuffer:()=>{entered.resolve();return read.promise;}})});
  await entered.promise;await s.f.cancel();await s.f.receive(offer(new Uint8Array(),1,second));const owner=s.f.current;
  read.resolve(new Uint8Array([1]).buffer);await uploading;
  assert.equal(s.f.current,owner);assert.equal(s.sent.length,0);s.f.clear();
});

test('중복 수락은 stream 하나만 열고 늦은 파일 선택 실패는 다음 전송에 영향을 주지 않는다',async()=>{
  const s=fixture(),opening=deferred(),entered=deferred();let count=0;
  await s.f.receive(offer(new Uint8Array()));
  const accepting=s.f.accept({createWritable:()=>{count++;entered.resolve();return opening.promise;}});await entered.promise;
  assert.equal(await s.f.accept({createWritable:()=>{count++;}}),false);assert.equal(count,1);
  await s.f.cancel();await s.f.receive(offer(new Uint8Array(),2,second));const owner=s.f.current;
  opening.reject(Error('cancelled picker'));assert.equal(await accepting,false);assert.equal(s.f.current,owner);assert.equal(s.sent.length,1);s.f.clear();
});

test('종료 후 도착한 파일 stream은 abort하고 수락·UI·성공을 보내지 않는다',async()=>{
  const s=fixture(),opening=deferred(),entered=deferred();let aborted=0;
  await s.f.receive(offer(new Uint8Array()));
  const accepting=s.f.accept({createWritable:()=>{entered.resolve();return opening.promise;}});await entered.promise;
  s.f.clear();opening.resolve({abort:async()=>aborted++});assert.equal(await accepting,false);
  assert.equal(aborted,1);assert.equal(s.sent.length,0);assert.equal(s.observed.length,1);
});

test('취소와 경합한 write 실패는 이전 stream만 정리하고 새 전송을 보존한다',async()=>{
  const s=fixture(),writing=deferred(),entered=deferred();let aborted=0;
  await s.f.receive(offer(new Uint8Array([1])));await s.f.accept({createWritable:async()=>({write:()=>{entered.resolve();return writing.promise;},abort:async()=>aborted++})});
  const receiving=s.f.receive(packet(new Uint8Array([1])));await entered.promise;const cancelling=s.f.cancel();
  await s.f.receive(offer(new Uint8Array(),2,second));const owner=s.f.current;writing.reject(Error('disk full'));
  await Promise.all([receiving,cancelling]);assert.equal(s.f.current,owner);assert.equal(aborted,1);assert.equal(s.sent.filter(x=>x.id===second).length,0);s.f.clear();
});

test('upload는 0바이트·credit 창을 넘는 파일도 수락 뒤에만 보내고 receipt 뒤 완료한다',async()=>{
  for(const [size,deny] of [[0,false],[16384*20+3,false],[3,true]]){
    const s=fixture(),bytes=Uint8Array.from({length:size},(_,i)=>i&255);let received=0,sequence=0,binaries=0,accepted=false;
    s.c.channels.get('file').send=data=>{
      if(typeof data==='string'){
        const m=JSON.parse(data);s.sent.push(m);
        if(m.t==='file-offer'){accepted=!deny;void s.f.receive(message(deny?'file-reject':'file-accept',++sequence,m.id,deny?{code:'declined'}:{}));}
        if(m.t==='file-complete')void s.f.receive(message('file-received',++sequence,m.id,{size,hash:sha(bytes)}));
      }else{
        assert.equal(accepted,true);const v=new DataView(data);assert.equal(v.getUint32(24,true),binaries++);received+=data.byteLength-32;
        assert.ok(s.f.current.sentChunks-s.f.current.ack<=16);
        if(binaries%4===0||received===size)void s.f.receive(message('file-progress',++sequence,s.f.current.id,{size:received,nextChunk:binaries}));
      }
    };
    await s.f.offer({name:'a.dat',size,slice:(start,end)=>new Blob([bytes.subarray(start,end)])});
    assert.equal(received,deny?0:size);assert.equal(s.f.current,null);assert.equal(s.ended.length,0);
    assert.equal(s.sent.some(m=>m.t==='file-complete'),!deny);
  }
});

test('멈춘 저장 작업은 I/O·정리 기한 뒤 세션을 실패로 종료한다',async t=>{
  t.mock.timers.enable({apis:['setTimeout']});
  const s=fixture(),entered=deferred(),cancelled=deferred();
  s.c.onData=m=>{s.observed.push(m);if(m.t==='file-status'&&m.text==='io-error')cancelled.resolve();};
  await s.f.receive(offer(new Uint8Array([1])));await s.f.accept({createWritable:async()=>({write:()=>{entered.resolve();return new Promise(()=>{});},abort:async()=>{}})});
  const receiving=s.f.receive(packet(new Uint8Array([1])));await entered.promise;
  t.mock.timers.tick(10000);await cancelled.promise;t.mock.timers.tick(2000);await receiving;
  assert.equal(s.f.current,null);assert.equal(s.ended.length,1);assert.equal(s.sent.some(m=>m.t==='file-received'),false);
});
