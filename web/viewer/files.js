import { Sha256 } from './sha256.js';
const max=256*1024*1024,chunk=16384,guid=/^[a-f0-9]{32}$/,hash=/^[a-f0-9]{64}$/;
export function safeFileName(name){return typeof name==='string'&&name.length>0&&name.length<=128&&name.isWellFormed()&&!/[\x00-\x1f\x7f-\x9f<>:"/\\|?*]/u.test(name)&&!/[. ]$/.test(name)&&!/^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$/i.test(name.split('.')[0].replace(/ +$/,''));}
export class FileSession {
  constructor(c){this.c=c;this.generation=c.generation??1;this.sent=0;this.received=0;this.current=null;this.seen=new Set();this.closed=new Map();}
  ready(){return this.c.live()&&!this.c.recovering&&(this.c.generation??1)===this.generation&&this.c.dataReady&&this.c.permissions?.includes('file')&&this.c.channels.get('file')?.readyState==='open';}
  send(t,fields,id=this.current?.id){if(!this.ready())throw Error('파일 채널 상태 오류다.');const ch=this.c.channels.get('file');if(ch.bufferedAmount>262144)throw Error('파일 송신 상한이다.');ch.send(JSON.stringify({...fields,v:1,t,id,sessionId:this.c.sessionId,generation:(this.c.generation??1),permissionGeneration:this.c.permissionGeneration,seq:++this.sent}));}
  remember(id){if(!guid.test(id)||this.seen.has(id))throw Error('파일 ID 재생이다.');this.seen.add(id);if(this.seen.size>256)this.seen.delete(this.seen.values().next().value);}
  live(owner){return this.ready()&&this.current===owner&&!owner.cancelled;}
  status(owner,state){this.c.onData({t:'file-status',id:owner.id,text:state,size:owner.size,name:owner.name});}
  async wait(owner,predicate,ms=10000){const until=this.c.now()+ms;while(!predicate()){if(!this.live(owner))throw Error('파일 전송 취소다.');if(this.c.now()>=until)throw Error('파일 응답 시간 초과다.');await new Promise(resolve=>setTimeout(resolve,10));}if(!this.live(owner))throw Error('파일 전송 취소다.');}
  async offer(file){if(!this.ready()||this.current||!safeFileName(file.name)||file.size>max)return false;
    const owner={id:crypto.randomUUID().replaceAll('-',''),name:file.name,size:file.size,outgoing:true,phase:'hash',at:this.c.now(),started:this.c.now(),sentChunks:0,ack:0};this.remember(owner.id);this.current=owner;
    try{const h=new Sha256();for(let at=0;at<file.size;at+=chunk){if(!this.live(owner))throw Error('취소다.');h.update(new Uint8Array(await file.slice(at,at+chunk).arrayBuffer()));}owner.hash=h.hex();owner.phase='offered';owner.at=this.c.now();this.send('file-offer',{name:owner.name,size:owner.size,hash:owner.hash});this.status(owner,'대상자의 저장 승인을 기다린다.');await this.wait(owner,()=>owner.phase==='sending',30000);
      for(let at=0;at<file.size;at+=chunk){await this.wait(owner,()=>this.c.channels.get('file').bufferedAmount<65536&&owner.sentChunks-owner.ack<16);const bytes=new Uint8Array(await file.slice(at,at+chunk).arrayBuffer());if(!this.live(owner))throw Error('취소다.');const packet=new Uint8Array(32+bytes.length),view=new DataView(packet.buffer);packet.set([82,65,70,84]);view.setUint16(4,1,true);packet.set(owner.id.match(/../g).map(x=>parseInt(x,16)),8);view.setUint32(24,owner.sentChunks++,true);view.setUint32(28,bytes.length,true);packet.set(bytes,32);this.c.channels.get('file').send(packet.buffer);owner.at=this.c.now();}
      await this.wait(owner,()=>this.c.channels.get('file').bufferedAmount<65536);owner.phase='receipt';this.send('file-complete',{size:owner.size,hash:owner.hash});await this.wait(owner,()=>owner.phase==='completed');this.finish(owner,'전송 완료다.');
    }catch{if(this.current===owner)await this.cancel('io-error');}return true;
  }
  async accept(handle){const owner=this.current;if(!owner||owner.outgoing||owner.phase!=='offered')return false;
    try{owner.writer=await handle.createWritable({keepExistingData:false});if(!this.live(owner)){await owner.writer.abort();return false;}owner.hashState=new Sha256();owner.bytes=0;owner.next=0;owner.phase='receiving';owner.at=this.c.now();this.send('file-accept',{});return true;}
    catch{await this.cancel('io-error');return false;}
  }
  async receive(value){const owner=this.current;if(value instanceof ArrayBuffer){if(value.byteLength<33||value.byteLength>32+chunk)throw Error('청크 크기 오류다.');const b=new Uint8Array(value),v=new DataView(value),id=[...b.slice(8,24)].map(x=>x.toString(16).padStart(2,'0')).join('');if(this.closed.has(id))return;
      if(!owner||owner.outgoing||owner.phase!=='receiving'||id!==owner.id||v.getUint32(0)!==0x52414654||v.getUint16(4,true)!==1||v.getUint16(6,true)!==0||v.getUint32(24,true)!==owner.next||v.getUint32(28,true)!==b.length-32||b.length-32!==Math.min(chunk,owner.size-owner.bytes))throw Error('파일 청크 계약 오류다.');
      try{await owner.writer.write(b.subarray(32));if(!this.live(owner))return;owner.hashState.update(b.subarray(32));owner.bytes+=b.length-32;owner.next++;owner.at=this.c.now();if(owner.next%4===0||owner.bytes===owner.size)this.send('file-progress',{size:owner.bytes,nextChunk:owner.next});}catch{await this.cancel('io-error');}return;
    }
    const m=value,c=this.c,common=['v','t','id','sessionId','generation','permissionGeneration','seq'];
    if(!this.ready()||m.sessionId!==c.sessionId||m.generation!==(c.generation??1)||m.permissionGeneration!==c.permissionGeneration||m.seq!==this.received+1||!guid.test(m.id))throw Error('파일 envelope 오류다.');
    const fields=m.t==='file-offer'?['name','size','hash']:['file-complete','file-received'].includes(m.t)?['size','hash']:m.t==='file-progress'?['size','nextChunk']:['file-reject','file-cancel'].includes(m.t)?['code']:m.t==='file-accept'?[]:null;
    if(!fields||Object.keys(m).some(k=>![...common,...fields].includes(k))||fields.some(k=>!(k in m)))throw Error('파일 필드 오류다.');this.received++;
    if(m.t==='file-offer'){if(!safeFileName(m.name)||!Number.isSafeInteger(m.size)||m.size<0||m.size>max||!hash.test(m.hash))throw Error('파일 제안 오류다.');this.remember(m.id);if(owner){this.send('file-reject',{code:'busy'},m.id);return;}this.current={...m,outgoing:false,phase:'offered',at:c.now(),started:c.now()};c.onData(m);return;}
    if(!owner||owner.id!==m.id){const old=this.closed.get(m.id);if(old&&(m.t==='file-cancel'||m.t==='file-received'&&old.outgoing&&m.size===old.size&&m.hash===old.hash))return;throw Error('다른 파일 응답이다.');}
    if(['file-cancel','file-reject'].includes(m.t)){if(!['declined','busy','timeout','io-error','hash-mismatch','cancelled','too-large'].includes(m.code))throw Error('파일 종료 코드 오류다.');if(owner.writer)await owner.writer.abort();this.finish(owner,m.code);return;}
    if(m.t==='file-accept'){if(!owner.outgoing||owner.phase!=='offered')throw Error('파일 수락 순서 오류다.');owner.phase='sending';owner.at=c.now();return;}
    if(m.t==='file-progress'){if(!owner.outgoing||!['sending','receipt'].includes(owner.phase)||!Number.isSafeInteger(m.nextChunk)||m.nextChunk<=owner.ack||m.nextChunk>owner.sentChunks||m.size!==Math.min(owner.size,m.nextChunk*chunk))throw Error('파일 credit 오류다.');owner.ack=m.nextChunk;owner.at=c.now();return;}
    if(m.size!==owner.size||m.hash!==owner.hash)throw Error('파일 확정 메타데이터 오류다.');
    if(m.t==='file-received'){if(!owner.outgoing||owner.phase!=='receipt')throw Error('파일 receipt 오류다.');owner.phase='completed';return;}
    if(owner.outgoing||owner.phase!=='receiving')throw Error('파일 complete 순서 오류다.');
    if(owner.bytes!==owner.size||owner.hashState.hex()!==owner.hash){await this.cancel('hash-mismatch');return;}
    try{await owner.writer.close();if(!this.live(owner))return;this.send('file-received',{size:owner.size,hash:owner.hash});this.finish(owner,'수신 완료다.');}catch{await this.cancel('io-error');}
  }
  finish(owner,text){owner.cancelled=true;this.closed.set(owner.id,{outgoing:owner.outgoing,size:owner.size,hash:owner.hash});if(this.closed.size>256)this.closed.delete(this.closed.keys().next().value);if(this.current===owner)this.current=null;this.status(owner,text);}
  async cancel(code='cancelled'){const owner=this.current;if(!owner)return;owner.cancelled=true;try{if(this.ready())this.send(!owner.outgoing&&owner.phase==='offered'?'file-reject':'file-cancel',{code},owner.id);if(owner.writer)await owner.writer.abort();}finally{this.finish(owner,code);}}
  expire(){const o=this.current;if(o&&((this.c.now()-o.at>(o.phase==='offered'?30000:10000))||this.c.now()-o.started>600000))this.cancel('timeout').catch(()=>this.c.end('파일 취소 실패다.',true));}
  clear(){const o=this.current;if(o){o.cancelled=true;o.writer?.abort().catch(()=>{});}this.current=null;this.seen.clear();this.closed.clear();}
}
