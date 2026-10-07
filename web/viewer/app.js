import { ViewerConnection } from './connection.js';

const el = Object.fromEntries(['join', 'session', 'join-form', 'code', 'pin', 'name', 'status', 'screen', 'stats', 'hangup']
  .map(id => [id, document.getElementById(id)]));
let current = null;
function status(text, error = false) { el.status.textContent = text; el.status.classList.toggle('error', error); }
function reset() { el.screen.srcObject = null; el.session.hidden = true; el.join.hidden = false; el.stats.textContent = ''; }
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
    onEnd: (reason, error) => { if (current !== connection) return; reset(); status(reason, error); }
  });
  current = connection; el.pin.value = '';
});
el.hangup.addEventListener('click', () => current?.end());
addEventListener('pagehide', () => current?.end());
