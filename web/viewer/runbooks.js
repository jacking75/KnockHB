import { validText, textHash, randomId } from './features.js';
const guid=/^[a-f0-9]{32}$/;
const exact=(o,keys)=>o&&typeof o==='object'&&!Array.isArray(o)&&Object.keys(o).length===keys.length&&keys.every(k=>k in o);
export class RunbookSession {
  constructor(connection){this.connection=connection;this.generation=connection.generation??1;this.books=[];this.sent=0;this.received=0;this.pending=null;this.disposed=false;}
  owned(){const c=this.connection;return !this.disposed&&c.live()&&!c.recovering&&(c.generation??1)===this.generation;}
  ready(){const c=this.connection;return this.owned()&&c.dataReady&&c.permissions?.includes('runbook')&&c.channels.get('runbook')?.readyState==='open';}
  send(t,id,fields={}){
    if(!this.ready())return false;const c=this.connection,ch=c.channels.get('runbook');if(ch.bufferedAmount>65536)throw Error('Runbook 송신 상한이다.');
    try{ch.send(JSON.stringify({...fields,v:1,t,id,sessionId:c.sessionId,generation:c.generation,permissionGeneration:c.permissionGeneration,seq:++this.sent}));return true;}
    catch{c.end('Runbook 전송 실패다.',true);return false;}
  }
  request(bookId,detail){const book=this.books.find(b=>b.bookId===bookId);if(!book||this.pending||!['summary','counts'].includes(detail)||!this.ready())return false;const id=randomId();this.pending={id,at:this.connection.now()};if(!this.send('runbook-request',id,{bookId,bookVersion:book.bookVersion,hash:book.hash,args:{detail}})){this.pending=null;return false;}return true;}
  /** 대상 PC에서 PowerShell 명령을 실행한다. admin이면 SYSTEM(관리자) 권한으로 실행한다. */
  async command(script,admin){
    if(this.pending||!this.ready()||typeof script!=='string'||script.length<1||script.length>8192||!validText(script,8192,32768))return false;
    const hash=await textHash(script.replaceAll('\r\n','\n'));if(this.pending||!this.ready())return false;
    const id=randomId();this.pending={id,at:this.connection.now()};
    if(!this.send('runbook-request',id,{bookId:'command',bookVersion:1,hash,args:{script,admin:admin?'true':'false'}})){this.pending=null;return false;}return true;
  }
  cancel(){if(this.pending)this.send('runbook-cancel',this.pending.id);}
  async receive(m){
    if(!this.owned())return;
    const c=this.connection,common=['v','t','sessionId','generation','permissionGeneration','id','seq'];
    if(m.v!==1||m.sessionId!==c.sessionId||m.generation!==c.generation||m.permissionGeneration!==c.permissionGeneration||!guid.test(m.id)||m.seq!==this.received+1)throw Error('Runbook 바인딩·순서 오류다.');
    if(m.t==='runbook-catalog'){
      if(!exact(m,[...common,'books'])||!Array.isArray(m.books)||m.books.length<1||m.books.length>16||new Set(m.books.map(b=>b.bookId)).size!==m.books.length)throw Error('Runbook 목록 오류다.');
      for(const b of m.books){if(!exact(b,['bookId','bookVersion','hash','title','impact','requiresAdmin','source'])||!['network-summary','clear-dns-cache'].includes(b.bookId)||b.bookVersion!==1||b.requiresAdmin!==(b.bookId==='clear-dns-cache')||!validText(b.title,128)||!validText(b.impact,1024)||!validText(b.source,8192)||b.hash!==await textHash(b.source.replaceAll('\r\n','\n')))throw Error('Runbook 정의·hash 오류다.');}
      if(!this.owned())return;this.books=m.books;
    }else if(m.t==='runbook-result'){
      const r=m.result;if(!exact(m,[...common,'result'])||!this.pending||m.id!==this.pending.id||!exact(r,['state','exitCode','stdout','stderr','remainingImpact'])||!['denied','completed','failed','cancelled','timed-out'].includes(r.state)||r.exitCode!==null&&(!Number.isInteger(r.exitCode)||r.exitCode< -2147483648||r.exitCode>2147483647)||!validText(r.stdout,8192)||!validText(r.stderr)||new TextEncoder().encode(r.stdout+r.stderr).length>24576||!validText(r.remainingImpact,1024))throw Error('Runbook 결과 오류다.');this.pending=null;
    }else throw Error('Runbook 응답 방향 오류다.');
    this.received=m.seq;c.onData(m);
  }
  expire(){if(this.pending&&this.connection.now()-this.pending.at>=60000)this.connection.end('Runbook 응답 시간 초과다.',true);}
  clear(){this.disposed=true;this.books=[];this.pending=null;}
}
