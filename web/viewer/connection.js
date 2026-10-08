// Each attempt owns its socket, peer connection, timers and pending ICE.
export function parseSignal(text) {
  if (typeof text !== 'string' || new TextEncoder().encode(text).length > 128 * 1024) throw Error('시그널링 크기 또는 타입 오류다.');
  const message = JSON.parse(text);
  const stack = [];
  const tokens = text.match(/"(?:[^"\\]|\\.)*"|[{}\[\],:]|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null/g) || [];
  for (const token of tokens) {
    const top = stack.at(-1);
    if (token === '{' || token === '[') {
      stack.push({ object: token === '{', key: token === '{', names: new Set() });
      if (stack.length > 32) throw Error('JSON 중첩 상한 초과다.');
    } else if (token === '}' || token === ']') stack.pop();
    else if (token === ',' && top?.object) top.key = true;
    else if (token === ':' && top?.object) top.key = false;
    else if (top?.object && top.key && token.startsWith('"')) {
      const key = JSON.parse(token);
      if (top.names.has(key)) throw Error('중복 JSON 필드다.');
      top.names.add(key);
    }
  }
  if (!message || message.v !== 1 || typeof message.type !== 'string') throw Error('지원하지 않는 프로토콜이다.');
  return message;
}

export function describeError(message) {
  return ({ 'not-found': '없는 세션 코드다.', 'expired': '세션이 만료됐다. 새 코드를 요청해야 한다.',
    'bad-pin': 'PIN이 올바르지 않다.', 'slot-taken': '이미 다른 지원자가 접속했다.',
    'consumed': '이미 사용한 세션 코드다. 새 코드를 요청해야 한다.', 'locked': 'PIN 시도 횟수를 초과했다.',
    'capacity': '서버 접속 상한에 도달했다.' })[message.code] || '접속 요청이 거부됐다.';
}

export class ViewerConnection {
  constructor({ base, code, pin, displayName, createSocket = url => new WebSocket(url),
    createPeer = config => new RTCPeerConnection(config), isCurrent = () => true,
    onState = () => {}, onTrack = () => {}, onStats = () => {}, onEnd = () => {},
    setTimer = (callback, interval) => globalThis.setInterval(callback, interval),
    clearTimer = timer => globalThis.clearInterval(timer), now = Date.now, forceRelay = false }) {
    this.base = base; this.code = code; this.pin = pin; this.displayName = displayName;
    this.createPeer = createPeer; this.isCurrent = isCurrent; this.onState = onState;
    this.onTrack = onTrack; this.onStats = onStats; this.onEnd = onEnd;
    this.setTimer = setTimer; this.clearTimer = clearTimer; this.now = now;
    this.pc = null; this.pendingIce = []; this.closed = false; this.reason = null;
    this.forceRelay = forceRelay; this.iceServers = [];
    this.messageChain = Promise.resolve(); this.timers = []; this.granted = false; this.queuedMessages = 0;
    this.lastSeen = now(); this.startedAt = now(); this.remoteReady = false;
    this.ws = createSocket(base.replace(/^http/, 'ws') + '/ws');
    const socket = this.ws;
    socket.onopen = () => {
      if (!this.live()) return;
      this.send({ type: 'join', room: code, role: 'viewer', pin: this.pin, displayName });
      this.pin = ''; // never persist pairing secrets
    };
    socket.onerror = () => { if (this.live()) this.end('시그널링 서버에 연결할 수 없다.', true); };
    socket.onclose = () => { if (this.live()) this.end('시그널링 연결이 끊어졌다.', true); };
    socket.onmessage = event => {
      if (!this.live()) return;
      try {
        const message = parseSignal(event.data);
        this.lastSeen = now();
        // Termination and lease replies must not wait behind pending SDP work.
        if (message.type === 'ping') { this.send({ type: 'pong' }); return; }
        if (message.type === 'pong') return;
        if (message.type === 'error') { this.end(describeError(message), true); return; }
        if (message.type === 'peer-left' || message.type === 'bye') { this.end('대상 PC와의 연결이 종료됐다.'); return; }
        if (this.queuedMessages >= 128) throw Error('시그널링 처리 큐 상한이다.');
        this.queuedMessages++;
        this.messageChain = this.messageChain.then(() => this.live() && this.handle(message))
          .catch(() => { if (this.live()) this.end('연결 협상 또는 프로토콜 처리에 실패했다.', true); })
          .finally(() => { this.queuedMessages--; });
      } catch { this.end('잘못된 시그널링 응답이다.', true); }
    };
    this.timers.push(setTimer(() => {
      if (!this.live()) return;
      if (now() - this.lastSeen >= 15000) this.end('시그널링 생존 확인 시간 초과다.', true);
      else if (!this.pc && now() - this.startedAt >= 60000) this.end('참가 또는 동의 대기 시간 초과다.', true);
      else if (socket.readyState === 1) this.send({ type: 'ping' });
    }, 5000));
    onState('대상 PC에 접속하는 중이다.');
  }
  live() { return !this.closed && this.isCurrent(); }
  send(message) {
    if (!this.live() || this.ws.readyState !== 1) return;
    this.ws.send(JSON.stringify({ v: 1, ...message }));
  }
  async handle(message) {
    if (message.type !== 'joined' && message.sessionId && message.sessionId !== this.sessionId) throw Error('다른 세션이다.');
    if (message.generation !== undefined && message.generation !== 1) throw Error('다른 협상 세대다.');
    switch (message.type) {
      case 'joined':
        if (this.sessionId || !message.sessionId || !message.connectionId) throw Error('잘못된 ACK다.');
        this.sessionId = message.sessionId; this.connectionId = message.connectionId;
        if (message.iceServers !== undefined) {
          if (!Array.isArray(message.iceServers) || message.iceServers.length > 8) throw Error('ICE server 상한이다.');
          for (const server of message.iceServers) {
            if (!Array.isArray(server.urls) || server.urls.length > 8 || server.urls.some(url => typeof url !== 'string' || !/^(stun|stuns|turn|turns):/.test(url))) throw Error('잘못된 ICE server다.');
          }
          this.iceServers = message.iceServers;
        }
        this.onState('대상자의 명시적 동의를 기다리는 중이다.'); break;
      case 'peer-joined': case 'consent-pending': this.onState('대상자의 명시적 동의를 기다리는 중이다.'); break;
      case 'consent':
        if (!Array.isArray(message.permissions) || !Number.isSafeInteger(message.permissionGeneration) || message.permissionGeneration < 1) throw Error('잘못된 승인 통지다.');
        this.granted = message.permissions.includes('screen'); this.permissionGeneration = message.permissionGeneration;
        this.onState('대상자가 승인한 기능을 연결하는 중이다.'); break;
      case 'consent-denied': this.end('대상자가 동의를 거절했다.'); break;
      case 'ice':
        if (!message.ice || typeof message.ice.candidate !== 'string' || message.ice.candidate.length > 8192) throw Error('잘못된 ICE다.');
        if (!this.remoteReady) {
          if (this.pendingIce.length >= 256) throw Error('ICE 상한이다.');
          this.pendingIce.push(message.ice);
        } else await this.pc.addIceCandidate(message.ice);
        break;
      case 'offer': await this.offer(message.sdp); break;
      default: throw Error('알 수 없는 메시지다.');
    }
  }
  async offer(sdp) {
    if (this.pc || !this.granted || typeof sdp !== 'string' || sdp.length > 96 * 1024) throw Error('승인 없는/중복 offer다.');
    if (this.forceRelay && !this.iceServers.some(server => server.urls.some(url => /^turns?:/.test(url)))) throw Error('TURN 설정이 없다.');
    const pc = this.createPeer({ iceServers: this.iceServers, iceTransportPolicy: this.forceRelay ? 'relay' : 'all' }); this.pc = pc; this.negotiatedAt = this.now();
    // SDP promises can themselves stall. This deadline must exist before awaiting them.
    this.timers.push(this.setTimer(() => {
      if (this.live() && this.now() - this.negotiatedAt >= 30000 && pc.connectionState !== 'connected')
        this.end('WebRTC 협상 시간 초과다.', true);
    }, 1000));
    pc.ontrack = event => { if (this.live() && this.pc === pc) this.onTrack(event); };
    pc.onicecandidate = event => {
      if (this.live() && this.pc === pc && event.candidate)
        this.send({ type: 'ice', room: this.code, ice: event.candidate.toJSON ? event.candidate.toJSON() : event.candidate });
    };
    pc.onconnectionstatechange = () => {
      if (!this.live() || this.pc !== pc) return;
      if (pc.connectionState === 'connected') this.onState('대상자가 승인한 기능으로 연결됐다.');
      if (['failed', 'closed', 'disconnected'].includes(pc.connectionState)) this.end('WebRTC 연결이 종료됐다.', pc.connectionState !== 'closed');
    };
    await pc.setRemoteDescription({ type: 'offer', sdp }); if (!this.live()) return;
    this.remoteReady = true;
    for (const ice of this.pendingIce.splice(0)) { await pc.addIceCandidate(ice); if (!this.live()) return; }
    const answer = await pc.createAnswer(); if (!this.live()) return;
    await pc.setLocalDescription(answer); if (!this.live()) return;
    this.send({ type: 'answer', room: this.code, sdp: answer.sdp });
    let busy = false; let lastBytes = 0; let lastTime = 0;
    this.timers.push(this.setTimer(async () => {
      if (!this.live() || busy) return;
      if (this.now() - this.negotiatedAt >= 30000 && pc.connectionState !== 'connected') { this.end('WebRTC 협상 시간 초과다.', true); return; }
      busy = true;
      try {
        const report = await pc.getStats(); if (!this.live()) return;
        const line = [];
        for (const entry of report.values()) {
          if (entry.type === 'inbound-rtp' && entry.kind === 'video') {
            if (entry.frameWidth) line.push(`${entry.frameWidth}×${entry.frameHeight}`);
            if (entry.framesPerSecond !== undefined) line.push(`표시 ${entry.framesPerSecond} fps`);
            if (lastTime && entry.timestamp > lastTime) line.push(`${Math.max(0, (entry.bytesReceived - lastBytes) * 8 / (entry.timestamp - lastTime)).toFixed(0)} kbps`);
            lastBytes = entry.bytesReceived; lastTime = entry.timestamp;
          }
          if (entry.type === 'transport' && entry.selectedCandidatePairId) {
            const pair = report.get(entry.selectedCandidatePairId);
            const local = report.get(pair?.localCandidateId); const remote = report.get(pair?.remoteCandidateId);
            if (local && remote) line.push(local.candidateType === 'relay' || remote.candidateType === 'relay' ? '경로: Relay' : '경로: P2P');
          }
        }
        this.onStats(line.join(' · '));
      } catch { if (this.live()) this.onStats('통계 조회 실패다.'); }
      finally { busy = false; }
    }, 1000));
  }
  end(reason = '세션을 종료했다.', error = false) {
    if (this.closed) return;
    this.reason ??= reason; this.closed = true; this.pin = '';
    for (const timer of this.timers.splice(0)) this.clearTimer(timer);
    this.pendingIce.length = 0;
    this.pc?.close();
    if (this.ws.readyState < 2) this.ws.close(); // CONNECTING is also owned
    this.onEnd(this.reason, error);
  }
}
