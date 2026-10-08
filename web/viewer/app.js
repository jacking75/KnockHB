import { ViewerConnection } from './connection.js';
import { ViewerInput } from './input.js';

const el = Object.fromEntries(['join', 'session', 'join-form', 'code', 'pin', 'name', 'status', 'screen', 'stats', 'hangup','remote-text','send-text','input-status','admin-request','admin-sas','admin-revoke','session-status']
  .map(id => [id, document.getElementById(id)]));
let current = null;
let input = null;
function status(text, error = false) { for(const target of [el.status,el['session-status']]){target.textContent=text;target.classList.toggle('error',error);} }
function reset() { input?.dispose();input=null;for(const id of ['remote-text','send-text','admin-request','admin-sas','admin-revoke'])el[id].disabled=true;el['input-status'].textContent='화면 보기';el.screen.srcObject = null; el.session.hidden = true; el.join.hidden = false; el.stats.textContent = ''; delete el.session.dataset.controlState; delete el.session.dataset.inputReady; }
el['join-form'].addEventListener('submit', event => {
  event.preventDefault();
  const code = el.code.value.replace(/\D/g, ''); const pin = el.pin.value;
  const name = el.name.value.trim();
  if (!/^\d{9}$/.test(code) || !/^\d{4}$/.test(pin) || name.length < 1 || name.length > 80) {
    status('세션 코드 9자리, PIN 4자리, 지원자 이름을 입력해야 한다.', true); return;
  }
  current?.end(); reset();
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
    onEnd: (reason, error) => { if (current !== connection) return; reset(); status(reason, error); }
  });
  current = connection; el.pin.value = '';
});
el.hangup.addEventListener('click', () => current?.end());
for(const [id,run] of [['admin-request',()=>current?.requestAdmin()],['admin-sas',()=>current?.adminAction('admin-sas')],['admin-revoke',()=>current?.adminAction('admin-revoke')]])
{
  el[id].addEventListener('pointerdown',event=>{event.preventDefault();if(!el[id].disabled)run();});
  el[id].addEventListener('click',event=>{if(event.detail===0&&!el[id].disabled)run();});
}
addEventListener('pagehide', () => current?.end());
