import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { Sha256 } from '../sha256.js';
import { safeFileName,FileSession } from '../files.js';
test('파일 hash는 분할 경계와 빈 파일에서 표준 SHA-256과 같다',()=>{for(const size of [0,1,55,56,63,64,65,16387]){const bytes=Uint8Array.from({length:size},(_,i)=>i&255),h=new Sha256();for(let at=0;at<size;at+=13)h.update(bytes.subarray(at,at+13));assert.equal(h.hex(),createHash('sha256').update(bytes).digest('hex'));}});
test('파일 경로와 Windows 장치명은 거부한다',()=>{for(const name of ['../a','C:a','a:stream','a.','NUL.txt','COM¹.dat','a/b','a\\b'])assert.equal(safeFileName(name),false);assert.equal(safeFileName('한글😀.txt'),true);});
test('수락 전 본문과 다른 전송 ID를 거부한다',async()=>{const c={live:()=>true,dataReady:true,permissions:['file'],channels:new Map([['file',{readyState:'open',bufferedAmount:0}]]),sessionId:'s',permissionGeneration:1,now:()=>0,onData:()=>{}};const f=new FileSession(c);await assert.rejects(f.receive(new ArrayBuffer(33)));await assert.rejects(f.receive({v:1,t:'file-accept',id:'a'.repeat(32),sessionId:'s',generation:1,permissionGeneration:1,seq:1}));});
