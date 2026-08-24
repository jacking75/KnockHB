// RemoteAssist 웹 뷰어 (T1.5)
//
// 하는 일:
//   1. 시그널링 WebSocket에 viewer 역할로 참가 (코드 + PIN)
//   2. Host의 offer를 받아 answer를 만들고 ICE를 교환
//   3. ontrack 으로 받은 화면을 <video> 에 붙인다
//
// 와이어 포맷의 기준은 docs/protocol.md 다. 이 코드가 아니다.
//
// 아직 없는 것: 입력 전송(T2.2), 연결 경로 표시(T4.3).

const SIGNALING_HTTP = location.origin.startsWith('http')
  ? location.origin
  : 'http://localhost:25001';

const ICE_SERVERS = [{ urls: 'stun:stun.l.google.com:19302' }];

const el = {
  join: document.getElementById('join'),
  session: document.getElementById('session'),
  form: document.getElementById('join-form'),
  code: document.getElementById('code'),
  pin: document.getElementById('pin'),
  status: document.getElementById('status'),
  video: document.getElementById('screen'),
  stats: document.getElementById('stats'),
  hangup: document.getElementById('hangup'),
};

let ws = null;
let pc = null;
let statsTimer = null;

el.form.addEventListener('submit', (e) => {
  e.preventDefault();

  // 418-207-935 처럼 입력해도 되도록 숫자만 남긴다.
  const code = el.code.value.replace(/\D/g, '');
  const pin = el.pin.value.replace(/\D/g, '');

  if (code.length !== 9 || pin.length < 4) {
    setStatus('세션 코드 9자리와 PIN을 정확히 입력하세요.', true);
    return;
  }

  connect(code, pin);
});

el.hangup?.addEventListener('click', () => teardown('세션을 종료했습니다.'));

function setStatus(text, isError = false) {
  el.status.textContent = text;
  el.status.classList.toggle('error', isError);
}

function connect(code, pin) {
  setStatus('시그널링 서버에 접속하는 중…');

  const wsUrl = SIGNALING_HTTP.replace(/^http/, 'ws') + '/ws';
  ws = new WebSocket(wsUrl);

  ws.onopen = () => {
    ws.send(JSON.stringify({ type: 'join', room: code, role: 'viewer', pin }));
  };

  ws.onerror = () => setStatus('시그널링 서버에 연결할 수 없습니다.', true);

  ws.onclose = () => {
    if (!pc) setStatus('시그널링 연결이 끊어졌습니다.', true);
  };

  ws.onmessage = async (event) => {
    const msg = JSON.parse(event.data);

    switch (msg.type) {
      case 'joined':
        setStatus(msg.peerPresent
          ? '대상 PC와 연결하는 중…'
          : '대상 PC를 기다리는 중…');
        break;

      case 'peer-joined':
        setStatus('대상 PC와 연결하는 중…');
        break;

      case 'offer':
        await handleOffer(msg.sdp, code);
        break;

      case 'ice':
        if (msg.ice && pc) {
          try {
            await pc.addIceCandidate(msg.ice);
          } catch (err) {
            console.warn('ICE 후보 추가 실패', err);
          }
        }
        break;

      case 'peer-left':
      case 'bye':
        teardown('대상 PC와의 연결이 종료되었습니다.');
        break;

      case 'error':
        setStatus(describeError(msg), true);
        ws.close();
        break;
    }
  };
}

function describeError(msg) {
  const known = {
    'not-found': '없는 세션 코드입니다.',
    'expired': '세션이 만료되었습니다. 대상자에게 새 코드를 요청하세요.',
    'bad-pin': 'PIN이 올바르지 않습니다.',
    'slot-taken': '이미 다른 지원자가 접속해 있습니다.',
    'bad-role': '잘못된 요청입니다.',
    'bad-request': '잘못된 요청입니다.',
  };
  return known[msg.code] || msg.reason || '접속에 실패했습니다.';
}

async function handleOffer(sdp, room) {
  pc = new RTCPeerConnection({ iceServers: ICE_SERVERS });

  pc.ontrack = (event) => {
    el.video.srcObject = event.streams[0] || new MediaStream([event.track]);
    el.join.hidden = true;
    el.session.hidden = false;
    startStats();
  };

  pc.onicecandidate = (event) => {
    if (event.candidate && ws?.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({
        type: 'ice',
        room,
        ice: {
          candidate: event.candidate.candidate,
          sdpMid: event.candidate.sdpMid,
          sdpMLineIndex: event.candidate.sdpMLineIndex,
        },
      }));
    }
  };

  pc.onconnectionstatechange = () => {
    if (pc.connectionState === 'failed') {
      teardown('연결에 실패했습니다. 네트워크를 확인하세요.');
    }
  };

  await pc.setRemoteDescription({ type: 'offer', sdp });
  const answer = await pc.createAnswer();
  await pc.setLocalDescription(answer);

  ws.send(JSON.stringify({ type: 'answer', room, sdp: answer.sdp }));
}

// 상태바: 해상도·FPS·비트레이트·연결 경로.
// 경로(P2P/Relay) 표시는 T4.3에서 제대로 다루지만, 지금도 후보 타입으로 대략 보여 준다.
function startStats() {
  if (statsTimer) clearInterval(statsTimer);

  let lastBytes = 0;
  let lastTime = 0;

  statsTimer = setInterval(async () => {
    if (!pc) return;

    const report = await pc.getStats();
    let line = [];

    report.forEach((s) => {
      if (s.type === 'inbound-rtp' && s.kind === 'video') {
        if (s.frameWidth) line.push(`${s.frameWidth}x${s.frameHeight}`);
        if (s.framesPerSecond) line.push(`${s.framesPerSecond} fps`);

        if (lastTime) {
          const kbps = ((s.bytesReceived - lastBytes) * 8) / (s.timestamp - lastTime);
          line.push(`${kbps.toFixed(0)} kbps`);
        }
        lastBytes = s.bytesReceived;
        lastTime = s.timestamp;
      }

      if (s.type === 'candidate-pair' && s.state === 'succeeded' && s.nominated) {
        const local = report.get(s.localCandidateId);
        line.push(local?.candidateType === 'relay' ? '경로: Relay' : '경로: P2P');
      }
    });

    el.stats.textContent = line.join(' · ');
  }, 1000);
}

function teardown(message) {
  if (statsTimer) { clearInterval(statsTimer); statsTimer = null; }

  if (pc) { pc.close(); pc = null; }
  if (ws && ws.readyState === WebSocket.OPEN) ws.close();
  ws = null;

  el.video.srcObject = null;
  el.session.hidden = true;
  el.join.hidden = false;
  setStatus(message);
}
