import { ViewerConnection } from './connection.js';
import { ViewerInput } from './input.js';
import { validText } from './features.js';

const el = Object.fromEntries(['join', 'session', 'join-form', 'code', 'pin', 'name', 'status', 'screen', 'stats', 'hangup','remote-text','send-text','input-status','admin-request','admin-sas','admin-revoke','session-status','chat-panel','chat-history','chat-text','chat-send','clipboard-panel','clipboard-text','clipboard-send','clipboard-get','clipboard-copy','feature-status','diagnostics-panel','diagnostics-content','diagnostics-status']
  .map(id => [id, document.getElementById(id)]));
let current = null;
let input = null;
let lastDiagnostics=0;
function status(text, error = false) { for(const target of [el.status,el['session-status']]){target.textContent=text;target.classList.toggle('error',error);} }
function reset() { input?.dispose();input=null;for(const id of ['remote-text','send-text','admin-request','admin-sas','admin-revoke'])el[id].disabled=true;el['input-status'].textContent='화면 보기';el.screen.srcObject = null; el.session.hidden = true; el.join.hidden = false; el.stats.textContent = ''; delete el.session.dataset.controlState; delete el.session.dataset.inputReady; }
function resetFeatures(){lastDiagnostics=0;for(const id of ['chat-panel','clipboard-panel','diagnostics-panel'])el[id].hidden=true;for(const id of ['chat-history','feature-status','diagnostics-content'])el[id].textContent='';el['chat-text'].value=el['clipboard-text'].value='';el['clipboard-copy'].disabled=true;}
function appendChat(text){el['chat-history'].textContent=(el['chat-history'].textContent+text+'\n').slice(-8192);el['chat-history'].scrollTop=el['chat-history'].scrollHeight;}
function showDiagnostics(snapshot) {
  lastDiagnostics=Date.now();el['diagnostics-status'].textContent='마지막 수신: '+new Date(lastDiagnostics).toLocaleTimeString();
  const bytes=n=>(n/1048576).toFixed(1)+' MiB',list=(values,format)=>values===null?'N/A':values.length?values.map(format).join('\n'):'없음';
  el['diagnostics-content'].textContent=[`CPU: ${snapshot.cpuPercent===null?'N/A':snapshot.cpuPercent.toFixed(1)+'%'}`,
    `메모리: ${snapshot.memory?bytes(snapshot.memory.availableBytes)+' 사용 가능 / '+bytes(snapshot.memory.totalBytes):'N/A'}`,
    '고정 디스크:\n'+list(snapshot.disks,d=>`${d.name} ${bytes(d.freeBytes)} 여유 / ${bytes(d.totalBytes)}`),
    '활성 인터페이스 누적 합계 (가상 인터페이스 중복 가능): '+(snapshot.network?`수신 ${bytes(snapshot.network.receivedBytes)} / 송신 ${bytes(snapshot.network.sentBytes)}`:'N/A'),
    '최근 오류·경고:\n'+list(snapshot.errors,e=>`${e.utc} ${e.provider} #${e.eventId} ${e.level}`),
    '시작 항목 이름:\n'+list(snapshot.startup,s=>s),'서비스:\n'+list(snapshot.services,s=>`${s.name}: ${s.state}`)].join('\n\n');
}
function updateFeatures(connection,message) {
  if(message.t==='data-ready'){el.join.hidden=true;el.session.hidden=false;el.screen.hidden=!connection.permissions.includes('screen');}
  for(const [permission,panel,buttons] of [['chat','chat-panel',['chat-send']],['clipboard','clipboard-panel',['clipboard-send','clipboard-get']],['diagnostics','diagnostics-panel',[]]]){
    el[panel].hidden=!connection.permissions?.includes(permission);for(const id of buttons)el[id].disabled=!connection.features.ready(permission);
  }
  if(message.t==='chat')appendChat('대상자: '+message.text);
  if(message.t==='clipboard-text'){el['clipboard-text'].value=message.text;el['clipboard-copy'].disabled=false;el['feature-status'].textContent='대상 PC의 텍스트를 읽었다. 복사 버튼으로 가져올 수 있다.';}
  if(message.t==='clipboard-applied')el['feature-status'].textContent='대상 PC 클립보드에 적용했다.';
  if(message.t==='clipboard-rejected')el['feature-status'].textContent=({'clipboard-busy':'대상 PC의 클립보드가 사용 중이다.','too-large':'텍스트 크기 한도를 초과했다.','unavailable':'클립보드를 사용할 수 없다.'})[message.code];
  if(message.t==='telemetry')showDiagnostics(message.snapshot);
  if(message.t==='feature-status')el['feature-status'].textContent=message.text;
}
el['join-form'].addEventListener('submit', event => {
  event.preventDefault();
  const code = el.code.value.replace(/\D/g, ''); const pin = el.pin.value;
  const name = el.name.value.trim();
  if (!/^\d{9}$/.test(code) || !/^\d{4}$/.test(pin) || name.length < 1 || name.length > 80) {
    status('세션 코드 9자리, PIN 4자리, 지원자 이름을 입력해야 한다.', true); return;
  }
  current?.end(); reset();resetFeatures();
  if (!/^https?:$/.test(location.protocol)) { status('시그널링 서버에서 제공하는 Viewer 주소를 열어야 한다.', true); return; }
  let connection;
  connection = new ViewerConnection({ base: location.origin, code, pin, displayName: name,
    forceRelay: new URLSearchParams(location.search).get('relay') === '1',
    isCurrent: () => current === connection,
    onState: status,
    onTrack: event => {
      el.screen.srcObject = event.streams[0] || new MediaStream([event.track]);
      el.join.hidden = true; el.session.hidden = false;
    },
    onStats: text => { el.stats.textContent = text; },
    onData: message => {
      updateFeatures(connection,message);
      if(message.t==='input-paused'||message.t==='admin-state')input?.clearLocalState();
      if(message.t==='admin-state')status(message.state==='active'?'관리자 지원 중 · 보안 화면은 대상자가 즉시 종료하거나 30초 뒤 종료된다.':message.state==='denied'?'대상자가 관리자 지원을 거부했다.':'대상자의 별도 관리자 승인을 기다리는 중이다. 입력을 일시 중단한다.');
      el.session.dataset.controlState = message.t;
      const ready=connection.canControl();el.session.dataset.inputReady=String(ready);
      el['remote-text'].disabled=!ready;el['send-text'].disabled=!ready;
      el['admin-request'].disabled=!ready||!connection.permissions?.includes('admin')||!!connection.adminGeneration;
      el['admin-sas'].disabled=!ready||!connection.adminGeneration;el['admin-revoke'].disabled=!ready||!connection.adminGeneration;
      if(ready) {
        input??=new ViewerInput(connection,el.screen,el['remote-text'],el['send-text']);
        el['remote-text'].disabled=false;el['send-text'].disabled=false;el['input-status'].textContent=connection.adminGeneration?'관리자 제어 · 최대 2분':'원격 제어 · 화면을 클릭한 뒤 키를 입력한다';
      }
      else if(connection.permissions?.includes('control'))el['input-status'].textContent='입력 일시 중단';
    },
    onEnd: (reason, error) => { if (current !== connection) return; reset();resetFeatures(); status(reason, error); }
  });
  current = connection; el.pin.value = '';
});
el.hangup.addEventListener('click', () => current?.end());
el['chat-send'].addEventListener('click',()=>{const text=el['chat-text'].value;if(current?.features.chat(text)){appendChat('나: '+text);el['chat-text'].value='';}});
for(const [id,action] of [['clipboard-send','set'],['clipboard-get','get']])el[id].addEventListener('click',async()=>{
  const connection=current;try{if(!await connection?.features.clipboard(action,action==='set'?el['clipboard-text'].value:''))if(current===connection)el['feature-status'].textContent='전송 상태·텍스트 크기·요청 빈도를 확인해야 한다.';}catch{if(current===connection)el['feature-status'].textContent='클립보드 전송 준비에 실패했다.';}
});
el['clipboard-copy'].addEventListener('click',async()=>{const connection=current;try{if(!connection?.features.ready('clipboard')||!validText(el['clipboard-text'].value,8192,16384))return;await navigator.clipboard.writeText(el['clipboard-text'].value);if(current===connection)el['feature-status'].textContent='이 PC의 클립보드에 복사했다.';}catch{if(current===connection)el['feature-status'].textContent='브라우저가 복사를 허용하지 않았다. 텍스트를 직접 선택해 복사할 수 있다.';}});
setInterval(()=>{if(current?.live()&&current.permissions?.includes('diagnostics')&&(!lastDiagnostics||Date.now()-lastDiagnostics>=5000))el['diagnostics-status'].textContent=lastDiagnostics?'진단 수신이 지연됐다. 마지막 수신: '+new Date(lastDiagnostics).toLocaleTimeString():'진단 수신 대기 중이다.';},1000);
for(const [id,run] of [['admin-request',()=>current?.requestAdmin()],['admin-sas',()=>current?.adminAction('admin-sas')],['admin-revoke',()=>current?.adminAction('admin-revoke')]])
{
  el[id].addEventListener('pointerdown',event=>{event.preventDefault();if(!el[id].disabled)run();});
  el[id].addEventListener('click',event=>{if(event.detail===0&&!el[id].disabled)run();});
}
addEventListener('pagehide', () => current?.end());
