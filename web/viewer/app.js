import { ViewerConnection, retryable } from './connection.js';
import { ViewerInput } from './input.js';
import { validText, randomId } from './features.js';
import { downloadHandle, maxDownloadBytes } from './files.js';

const ids=['join','session','join-form','code','name','status','screen','stats','hangup','remote-text','send-text','input-status','admin-request','admin-sas','session-status','reconnect-status',
  'chat-panel','chat-history','chat-text','chat-send','clipboard-panel','clipboard-text','clipboard-send','clipboard-get','clipboard-copy','feature-status','diagnostics-panel','diagnostics-content','diagnostics-status',
  'file-panel','file-source','file-send','file-status','file-accept','file-reject','file-cancel',
  'media-controls','monitor-list','monitor-choice','monitor-select','quality-mode','quality-status',
  'runbook-panel','runbook-choice','runbook-detail','runbook-preview','runbook-send','runbook-cancel','runbook-result',
  'command-text','command-admin','command-send','command-cancel','command-result'];
const el=Object.fromEntries(ids.map(id=>[id,document.getElementById(id)]));
let current=null,input=null,lastDiagnostics=0,commandPending=false;

function status(text,error=false){for(const target of [el.status,el['session-status']]){target.textContent=text;target.classList.toggle('error',error);}}
function reset(){
  input?.dispose();input=null;
  for(const id of ['remote-text','send-text','admin-request','admin-sas'])el[id].disabled=true;
  el['input-status'].textContent='연결 중';el.screen.srcObject=null;el.stats.textContent='';
  delete el.session.dataset.controlState;delete el.session.dataset.inputReady;
}
function resetFeatures(){
  lastDiagnostics=0;commandPending=false;
  for(const id of ['chat-history','feature-status','diagnostics-content','runbook-preview','runbook-result','quality-status'])el[id].textContent='';
  el['chat-text'].value=el['clipboard-text'].value=el['file-source'].value='';
  el['monitor-choice'].replaceChildren();el['runbook-choice'].replaceChildren();el['clipboard-copy'].disabled=true;
}
function previewRunbook(){const book=current?.runbooks.books.find(b=>b.bookId===el['runbook-choice'].value);el['runbook-preview'].textContent=book?`${book.title}\n버전 ${book.bookVersion} · SHA256 ${book.hash}\n${book.requiresAdmin?'관리자 권한으로 실행한다.\n':''}${book.impact}\n\n${book.source}`:'';}
function appendChat(text){el['chat-history'].textContent=(el['chat-history'].textContent+text+'\n').slice(-8192);el['chat-history'].scrollTop=el['chat-history'].scrollHeight;}
function showDiagnostics(snapshot){
  lastDiagnostics=Date.now();el['diagnostics-status'].textContent='마지막 수신: '+new Date(lastDiagnostics).toLocaleTimeString();
  const bytes=n=>(n/1048576).toFixed(1)+' MiB',list=(values,format)=>values===null?'N/A':values.length?values.map(format).join('\n'):'없음';
  el['diagnostics-content'].textContent=[`CPU: ${snapshot.cpuPercent===null?'N/A':snapshot.cpuPercent.toFixed(1)+'%'}`,
    `메모리: ${snapshot.memory?bytes(snapshot.memory.availableBytes)+' 사용 가능 / '+bytes(snapshot.memory.totalBytes):'N/A'}`,
    '고정 디스크:\n'+list(snapshot.disks,d=>`${d.name} ${bytes(d.freeBytes)} 여유 / ${bytes(d.totalBytes)}`),
    '활성 인터페이스 누적 합계 (가상 인터페이스 중복 가능): '+(snapshot.network?`수신 ${bytes(snapshot.network.receivedBytes)} / 송신 ${bytes(snapshot.network.sentBytes)}`:'N/A'),
    '최근 오류·경고:\n'+list(snapshot.errors,e=>`${e.utc} ${e.provider} #${e.eventId} ${e.level}`),
    '시작 항목 이름:\n'+list(snapshot.startup,s=>s),'서비스:\n'+list(snapshot.services,s=>`${s.name}: ${s.state}`)].join('\n\n');
}
function showResult(target,r){target.textContent=`${r.state} · 종료 코드 ${r.exitCode??'없음'}\n${r.stdout}${r.stderr?'\n[stderr]\n'+r.stderr:''}${r.remainingImpact?'\n'+r.remainingImpact:''}`;}

function updateFeatures(connection,message){
  const runbookBusy=!!connection.runbooks.pending;
  el['runbook-send'].disabled=!connection.runbooks.ready()||runbookBusy||!connection.runbooks.books.length;el['runbook-cancel'].disabled=!runbookBusy||commandPending;
  el['command-send'].disabled=!connection.runbooks.ready()||runbookBusy;el['command-cancel'].disabled=!runbookBusy||!commandPending;
  if(message.t==='runbook-catalog'){el['runbook-choice'].replaceChildren(...message.books.map(b=>{const option=document.createElement('option');option.value=b.bookId;option.textContent=b.title;return option;}));previewRunbook();}
  if(message.t==='runbook-result'){showResult(commandPending?el['command-result']:el['runbook-result'],message.result);commandPending=false;el['command-send'].disabled=false;el['command-cancel'].disabled=true;}
  // 관리자 모드에서도 모니터를 바꿀 수 있다. 관리자 모드 전환 중에만 잠시 막는다.
  const switching=!!connection.adminRequestId;
  el['monitor-select'].disabled=el['monitor-choice'].disabled=switching;
  if(message.t==='monitor-list'){el['monitor-choice'].replaceChildren(...message.monitors.map(m=>{const option=document.createElement('option');option.value=m.identity;option.textContent=`${m.identity} ${m.width}×${m.height}`;option.selected=m.identity===connection.monitor?.identity;return option;}));}
  if(message.t==='monitor-state')status(`${message.monitor.identity} 화면을 공유한다.`);
  if(message.t==='quality-state'){const q=message.quality;el['quality-status'].textContent=`설정: ${q.fps} fps / ${q.bitrateKbps} kbps / 최대 ${q.longEdge}×${q.shortEdge}`;}
  el['file-send'].disabled=!connection.files.ready()||!!connection.files.current;
  if(message.t==='file-offer'){el['file-status'].textContent=`대상 PC가 ${message.name} (${message.size} bytes)를 보냈다.`;el['file-accept'].disabled=el['file-reject'].disabled=false;}
  if(message.t==='file-status'){el['file-status'].textContent=message.text;el['file-accept'].disabled=el['file-reject'].disabled=true;}
  el['file-cancel'].disabled=!connection.files.current;
  if(message.t==='data-ready'){el.join.hidden=true;el.session.hidden=false;connection.controlCommand('monitor-list',{},randomId());}
  el['chat-send'].disabled=!connection.features.ready('chat');
  for(const id of ['clipboard-send','clipboard-get'])el[id].disabled=!connection.features.ready('clipboard');
  if(message.t==='chat')appendChat('대상자: '+message.text);
  if(message.t==='clipboard-text'){el['clipboard-text'].value=message.text;el['clipboard-copy'].disabled=false;el['feature-status'].textContent='대상 PC의 텍스트를 읽었다. 복사 버튼으로 가져올 수 있다.';}
  if(message.t==='clipboard-applied')el['feature-status'].textContent='대상 PC 클립보드에 적용했다.';
  if(message.t==='clipboard-rejected')el['feature-status'].textContent=({'clipboard-busy':'대상 PC의 클립보드가 사용 중이다.','too-large':'텍스트 크기 한도를 초과했다.','unavailable':'클립보드를 사용할 수 없다.'})[message.code];
  if(message.t==='telemetry')showDiagnostics(message.snapshot);
  if(message.t==='feature-status')el['feature-status'].textContent=message.text;
}

// ---- 접속과 자동 재접속 ----------------------------------------------------
let autoRetry=false,retryTimer=null,attempts=0;
function remember(code,name){try{localStorage.setItem('ra-target',JSON.stringify({code,name}));}catch{}}
try{const saved=JSON.parse(localStorage.getItem('ra-target')||'null');if(saved){el.code.value=saved.code||'';el.name.value=saved.name||'';}}catch{}
el['join-form'].addEventListener('submit',event=>{
  event.preventDefault();
  const code=el.code.value.replace(/\D/g,'');const name=el.name.value.trim()||'지원자';
  if(!/^\d{9}$/.test(code)){status('지원 ID 9자리를 입력해야 한다.',true);return;}
  remember(code,el.name.value.trim());autoRetry=true;attempts=0;clearTimeout(retryTimer);
  connect(code,name);
});
function connect(code,name){
  current?.end();reset();resetFeatures();
  if(location.protocol!=='https:'&&location.hostname!=='localhost'&&location.hostname!=='127.0.0.1'){status('터널링 서버의 HTTPS Viewer 주소를 열어야 한다.',true);return;}
  let connection;
  connection=new ViewerConnection({base:location.origin,code,displayName:name,
    isCurrent:()=>current===connection,
    onState:status,
    onTrack:event=>{el.screen.srcObject=event.streams[0]||new MediaStream([event.track]);el.join.hidden=true;el.session.hidden=false;},
    onStats:text=>{el.stats.textContent=text;},
    onData:message=>{
      if(message.t==='data-ready'){attempts=0;el['reconnect-status'].textContent='';}
      updateFeatures(connection,message);
      if(message.t==='input-paused'||message.t==='admin-state'||message.t==='monitor-state')input?.clearLocalState();
      if(message.t==='admin-state')status(message.state==='active'?'관리자 모드 · UAC·잠금 화면과 관리자 창도 조작할 수 있다.':message.state==='denied'?'관리자 모드를 시작하지 못했다. 일반 권한 창만 조작한다.':'관리자 모드를 준비 중이다.');
      // 연결되면 바로 관리자 모드로 들어간다. 일반 창과 관리자 창을 한 경로로 조작한다.
      connection.requestInitialAdmin();
      el.session.dataset.controlState=message.t;
      const ready=connection.canControl();el.session.dataset.inputReady=String(ready);
      // A click on the text button blurs the video and starts an input reset. Keep the button usable while only the
      // reset ACK is pending so the click queues the text (ViewerInput.resumed sends it).
      el['remote-text'].disabled=el['send-text'].disabled=!ready&&!connection.pendingReset;
      el['admin-request'].disabled=!ready||!!connection.adminGeneration;
      el['admin-sas'].disabled=!ready||!connection.adminGeneration;
      if(ready){
        input??=new ViewerInput(connection,el.screen,el['remote-text'],el['send-text']);
        input.resumed();
        el['input-status'].textContent=connection.adminGeneration?'관리자 제어 중 · 화면을 클릭한 뒤 키를 입력한다':'원격 제어 · 화면을 클릭한 뒤 키를 입력한다';
      }
      else el['input-status'].textContent='입력 일시 중단';
    },
    onEnd:(reason,error)=>{
      if(current!==connection)return;reset();status(reason,error);
      // 대상 PC는 지원자가 나가면 1초 안에 같은 지원 ID로 다시 대기한다. 이어받기로 밀려난 경우만 멈춘다.
      if(autoRetry&&retryable(connection.errorCode)){
        attempts++;const delay=Math.min(10000,2000*Math.max(1,attempts>3?attempts-2:1));
        el['reconnect-status'].textContent=`재접속 대기 · ${attempts}회째 · ${Math.round(delay/1000)}초 뒤`;
        status(reason+' 다시 접속한다.',false);
        retryTimer=setTimeout(()=>{if(autoRetry&&current===connection)connect(code,name);},delay);
      }
      else{autoRetry=false;el.session.hidden=true;el.join.hidden=false;el['reconnect-status'].textContent='';resetFeatures();}
    }
  });
  current=connection;
}
el.hangup.addEventListener('click',()=>{autoRetry=false;clearTimeout(retryTimer);current?.end('지원자가 세션을 끝냈다.');el.session.hidden=true;el.join.hidden=false;});

// ---- 기능 버튼 ---------------------------------------------------------------
const command=(t,fields={})=>current?.controlCommand(t,fields,randomId());
el['runbook-choice'].addEventListener('change',previewRunbook);
el['runbook-send'].addEventListener('click',()=>{if(current?.runbooks.request(el['runbook-choice'].value,el['runbook-detail'].value)){commandPending=false;el['runbook-result'].textContent='대상 PC에서 실행 중이다.';el['runbook-send'].disabled=true;el['runbook-cancel'].disabled=false;}});
el['runbook-cancel'].addEventListener('click',()=>current?.runbooks.cancel());
el['command-send'].addEventListener('click',async()=>{
  const connection=current,script=el['command-text'].value;if(!connection||!script.trim())return;
  if(await connection.runbooks.command(script,el['command-admin'].checked)){if(current!==connection)return;commandPending=true;el['command-result'].textContent='대상 PC에서 실행 중이다. (최대 20초)';el['command-send'].disabled=true;el['command-cancel'].disabled=false;}
  else if(current===connection)el['command-result'].textContent='명령 길이(8192자)와 실행 상태를 확인해야 한다.';
});
el['command-cancel'].addEventListener('click',()=>current?.runbooks.cancel());
el['monitor-list'].addEventListener('click',()=>command('monitor-list'));
el['monitor-select'].addEventListener('click',()=>{if(el['monitor-choice'].value)command('monitor-select',{monitorIdentity:el['monitor-choice'].value});});
el['quality-mode'].addEventListener('change',()=>command('quality-mode',{mode:el['quality-mode'].value}));
el['file-send'].addEventListener('click',()=>{const c=current,file=el['file-source'].files[0];if(file&&c)c.files.offer(file).then(ok=>{if(!ok&&current===c)el['file-status'].textContent='파일 이름·256MiB 한도·전송 상태를 확인해야 한다.';}).catch(()=>{if(current===c)c.end('파일 전송 실패다.',true);});});
el['file-accept'].addEventListener('click',async()=>{const c=current,offer=c?.files.current;if(!offer||offer.phase!=='offered')return;el['file-accept'].disabled=true;try{if(!globalThis.showSaveFilePicker&&offer.size>maxDownloadBytes){await c.files.cancel('too-large',offer);if(current===c)el['file-status'].textContent='이 브라우저의 다운로드 한도는 8MiB다.';return;}const handle=globalThis.showSaveFilePicker?await showSaveFilePicker({suggestedName:offer.name}):downloadHandle(offer.name,offer.size);if(current===c&&c.files.current===offer)await c.files.accept(handle);}catch{if(current===c)await c.files.cancel('declined',offer);}});
el['file-reject'].addEventListener('click',()=>current?.files.cancel('declined'));
el['file-cancel'].addEventListener('click',()=>current?.files.cancel());
el['chat-send'].addEventListener('click',()=>{const text=el['chat-text'].value;if(current?.features.chat(text)){appendChat('나: '+text);el['chat-text'].value='';}});
for(const [id,action] of [['clipboard-send','set'],['clipboard-get','get']])el[id].addEventListener('click',async()=>{
  const connection=current;try{if(!await connection?.features.clipboard(action,action==='set'?el['clipboard-text'].value:''))if(current===connection)el['feature-status'].textContent='전송 상태·텍스트 크기·요청 빈도를 확인해야 한다.';}catch{if(current===connection)el['feature-status'].textContent='클립보드 전송 준비에 실패했다.';}
});
el['clipboard-copy'].addEventListener('click',async()=>{const connection=current;try{if(!connection?.features.ready('clipboard')||!validText(el['clipboard-text'].value,8192,16384))return;await navigator.clipboard.writeText(el['clipboard-text'].value);if(current===connection)el['feature-status'].textContent='이 PC의 클립보드에 복사했다.';}catch{if(current===connection)el['feature-status'].textContent='브라우저가 복사를 허용하지 않았다. 텍스트를 직접 선택해 복사할 수 있다.';}});
setInterval(()=>{if(current?.live()&&current.dataReady&&(!lastDiagnostics||Date.now()-lastDiagnostics>=5000))el['diagnostics-status'].textContent=lastDiagnostics?'진단 수신이 지연됐다. 마지막 수신: '+new Date(lastDiagnostics).toLocaleTimeString():'진단 수신 대기 중이다.';},1000);
for(const [id,run] of [['admin-request',()=>current?.requestAdmin()],['admin-sas',()=>current?.adminAction('admin-sas')]])
{
  el[id].addEventListener('pointerdown',event=>{event.preventDefault();if(!el[id].disabled)run();});
  el[id].addEventListener('click',event=>{if(event.detail===0&&!el[id].disabled)run();});
}
addEventListener('pagehide',()=>{autoRetry=false;current?.end();});
