const permissionNames=Object.freeze(['screen','control','admin','file','clipboard','diagnostics','runbook','chat']);
const guid=/^[a-f0-9]{32}$/;
export function permissionMask(permissions) {
  if(!Array.isArray(permissions)||!permissions.length||permissions.length>8)throw Error('승인 권한 목록 오류다.');
  let mask=0,last=-1;
  for(const name of permissions){const index=permissionNames.indexOf(name);if(index<=last)throw Error('미지·중복·순서 오류 권한이다.');last=index;mask|=1<<index;}
  if((mask&2)&&!(mask&1)||(mask&4)&&(mask&3)!==3)throw Error('승인 의존성 오류다.');return mask;
}
export function featureChannels(permissions) {
  const mask=permissionMask(permissions),result=['control'];
  if(mask&2)result.push('input','pointer');if(mask&8)result.push('file');if(mask&144)result.push('chat');if(mask&32)result.push('telemetry');if(mask&64)result.push('runbook');return result;
}
export function validText(text,max=4096,bytes=32768) {
  return typeof text==='string'&&text.length<=max&&!text.includes('\0')&&text.isWellFormed()&&new TextEncoder().encode(text).length<=bytes;
}
export async function textHash(text) {
  const bytes=await globalThis.crypto.subtle.digest('SHA-256',new TextEncoder().encode(text));
  return [...new Uint8Array(bytes)].map(b=>b.toString(16).padStart(2,'0')).join('');
}
function keys(value,expected) {
  if(!value||typeof value!=='object'||Array.isArray(value)||Object.keys(value).some(k=>!expected.includes(k)))throw Error('진단 필드 오류다.');
  if(expected.some(k=>!(k in value)))throw Error('진단 필드 누락이다.');
}
const count=n=>Number.isSafeInteger(n)&&n>=0;
const name=s=>typeof s==='string'&&s.length>0&&s.length<=128&&!/[\x00-\x1f\x7f-\x9f\\/]/u.test(s);
const utc=s=>typeof s==='string'&&s.length<=40&&/^\d{4}-\d{2}-\d{2}T.+(?:Z|\+00:00)$/.test(s)&&Number.isFinite(Date.parse(s));
export function validateSnapshot(s) {
  keys(s,['utc','cpuPercent','memory','disks','network','errors','startup','services']);
  if(!utc(s.utc)||s.cpuPercent!==null&&(!Number.isFinite(s.cpuPercent)||s.cpuPercent<0||s.cpuPercent>100))throw Error('진단 시간·CPU 오류다.');
  if(s.memory!==null){keys(s.memory,['totalBytes','availableBytes']);if(!count(s.memory.totalBytes)||!count(s.memory.availableBytes)||s.memory.availableBytes>s.memory.totalBytes)throw Error('메모리 오류다.');}
  if(s.network!==null){keys(s.network,['receivedBytes','sentBytes']);if(!count(s.network.receivedBytes)||!count(s.network.sentBytes))throw Error('네트워크 오류다.');}
  for(const [field,max,check] of [
    ['disks',128,d=>{keys(d,['name','totalBytes','freeBytes']);return /^[A-Za-z]:$/.test(d.name)&&count(d.totalBytes)&&count(d.freeBytes)&&d.freeBytes<=d.totalBytes;}],
    ['errors',20,e=>{keys(e,['provider','eventId','utc','level']);return name(e.provider)&&count(e.eventId)&&e.eventId<=0x3fffffff&&utc(e.utc)&&['error','warning'].includes(e.level);}],
    ['startup',128,name],
    ['services',128,e=>{keys(e,['name','state']);return name(e.name)&&['Running','Stopped','Paused','StartPending','StopPending','ContinuePending','PausePending'].includes(e.state);}]
  ])if(s[field]!==null&&(!Array.isArray(s[field])||s[field].length>max||!s[field].every(check)))throw Error('진단 목록 오류다.');
  return s;
}
export class FeatureSession {
  constructor(connection){this.connection=connection;this.generation=connection.generation??1;this.received={chat:0,telemetry:0};this.sent=0;this.pending=new Map();this.seen=new Set();this.quotas=new Map();}
  allowed(permission){const c=this.connection;return c.live()&&!c.recovering&&(c.generation??1)===this.generation&&c.dataReady&&c.permissions?.includes(permission);}
  ready(permission){return this.allowed(permission)&&this.connection.channels.get(permission==='diagnostics'?'telemetry':'chat')?.readyState==='open';}
  quota(type,max){const now=this.connection.now(),old=this.quotas.get(type);if(!old||now-old.start>=1000){this.quotas.set(type,{start:now,count:1});return true;}if(old.count>=max)return false;old.count++;return true;}
  send(t,fields={},id=globalThis.crypto.randomUUID().replaceAll('-','')) {
    const permission=t==='chat'?'chat':'clipboard',c=this.connection,ch=c.channels.get('chat');
    if(!this.ready(permission)||!guid.test(id))return false;
    if(!this.quota(permission,permission==='chat'?5:2)){c.onData({t:'feature-status',text:'요청 빈도를 줄여야 한다.'});return false;}
    if(ch.bufferedAmount>65536){c.end('보조 채널 송신 상한이다.',true);return false;}
    try{const json=JSON.stringify({...fields,v:1,t,sessionId:c.sessionId,generation:(c.generation??1),permissionGeneration:c.permissionGeneration,id,seq:this.sent+1});if(new TextEncoder().encode(json).length>30720){c.onData({t:'feature-status',text:'텍스트의 JSON 크기 한도를 초과했다.'});return false;}ch.send(json);this.sent++;return true;}
    catch{c.end('보조 채널 전송 실패다.',true);return false;}
  }
  chat(text){return validText(text)&&text.length>0&&this.send('chat',{text});}
  async clipboard(action,text='') {
    if(!this.ready('clipboard')||!['get','set'].includes(action)||this.pending.size>=16||!validText(text,8192,16384))return false;
    const c=this.connection,id=globalThis.crypto.randomUUID().replaceAll('-',''),hash=action==='set'?await textHash(text):undefined;
    if(!this.ready('clipboard')||this.pending.size>=16)return false;
    this.pending.set(id,{action,hash,at:c.now()});
    if(!this.send(`clipboard-${action}`,action==='set'?{text,hash}:{},id)){this.pending.delete(id);return false;}return true;
  }
  async receive(label,message) {
    const c=this.connection,common=['v','t','sessionId','generation','permissionGeneration','id','seq'];
    if(message.sessionId!==c.sessionId||message.generation!==(c.generation??1)||message.permissionGeneration!==c.permissionGeneration||!guid.test(message.id)||!Number.isSafeInteger(message.seq)||message.seq!==this.received[label]+1||this.seen.has(message.id))throw Error('보조 채널 바인딩·재생 오류다.');
    const fields=message.t==='chat'?['text']:message.t==='telemetry'?['snapshot']:message.t==='clipboard-text'?['text','hash']:message.t==='clipboard-applied'?['hash']:message.t==='clipboard-rejected'?['code']:null;
    if(!fields||Object.keys(message).some(k=>![...common,...fields].includes(k))||fields.some(k=>!(k in message)))throw Error('보조 채널 필드 오류다.');
    if(message.t==='telemetry') {if(label!=='telemetry'||!c.permissions.includes('diagnostics'))throw Error('진단 방향·권한 오류다.');validateSnapshot(message.snapshot);}
    else {
      if(label!=='chat')throw Error('채팅 채널 방향 오류다.');
      if(message.t==='chat'){if(!c.permissions.includes('chat')||!validText(message.text)||!message.text.length)throw Error('채팅 권한·본문 오류다.');}
      else {
        const pending=this.pending.get(message.id);
        if(!c.permissions.includes('clipboard')||!pending||c.now()-pending.at>=2000)throw Error('클립보드 요청 결합 오류다.');
        if(message.t==='clipboard-text'){if(pending.action!=='get'||!validText(message.text,8192,16384)||message.hash!==await textHash(message.text))throw Error('클립보드 hash 오류다.');}
        else if(message.t==='clipboard-applied'){if(pending.action!=='set'||message.hash!==pending.hash)throw Error('클립보드 적용 응답 오류다.');}
        else if(!['clipboard-busy','too-large','unavailable'].includes(message.code))throw Error('클립보드 거부 코드 오류다.');
        if(!this.allowed('clipboard'))return;this.pending.delete(message.id);
      }
    }
    if(!this.allowed(message.t==='telemetry'?'diagnostics':message.t==='chat'?'chat':'clipboard'))return;
    this.received[label]=message.seq;this.seen.add(message.id);if(this.seen.size>256)this.seen.delete(this.seen.values().next().value);
    c.onData(message);
  }
  expire(){if([...this.pending.values()].some(p=>this.connection.now()-p.at>=2000))this.connection.end('클립보드 응답 시간 초과다.',true);}
  clear(){this.pending.clear();this.seen.clear();this.quotas.clear();}
}
