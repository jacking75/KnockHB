import { permissionMask, featureChannels, FeatureSession } from './features.js';
import { FileSession } from './files.js';
import { RunbookSession } from './runbooks.js';
// Each attempt owns its socket, peer connection, timers and pending ICE.
function parseEnvelope(text, maxBytes, typeField) {
  if (typeof text !== 'string' || new TextEncoder().encode(text).length > maxBytes) throw Error('프로토콜 크기 또는 타입 오류다.');
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
  if (!message || message.v !== 1 || typeof message[typeField] !== 'string') throw Error('지원하지 않는 프로토콜이다.');
  return message;
}
export function parseSignal(text){
  const message=parseEnvelope(text,128*1024,'type');
  const fields=['v','type','sessionId','connectionId','generation','permissionGeneration','permissions','displayName','iceServers','room','role','pin','token','sdp','ice','peerPresent','code','reason'];
  if(Object.keys(message).some(k=>!fields.includes(k)))throw Error('미등록 시그널링 필드다.');
  if(message.ice!==undefined){const ice=message.ice;if(!ice||Object.keys(ice).some(k=>!['candidate','sdpMid','sdpMLineIndex'].includes(k))||typeof ice.candidate!=='string'||ice.candidate.length>8192||ice.sdpMid!=null&&(typeof ice.sdpMid!=='string'||ice.sdpMid.length>256)||ice.sdpMLineIndex!=null&&(!Number.isInteger(ice.sdpMLineIndex)||ice.sdpMLineIndex<0||ice.sdpMLineIndex>65535))throw Error('ICE 필드 오류다.');}
  if(message.iceServers!==undefined)validateIceServers(message.iceServers);
  return message;
}
export const parseData = text => parseEnvelope(text, 32 * 1024, 't');
function validateIceServers(servers){
  if(!Array.isArray(servers)||servers.length>8)throw Error('ICE server 상한이다.');
  for(const s of servers)if(!s||Object.keys(s).some(k=>!['urls','username','credential'].includes(k))||!Array.isArray(s.urls)||s.urls.length<1||s.urls.length>8||s.urls.some(u=>typeof u!=='string'||u.length>1024||!/^(stun|stuns|turn|turns):[^\s]+$/.test(u))||s.username!==undefined&&(typeof s.username!=='string'||s.username.length>256)||s.credential!==undefined&&(typeof s.credential!=='string'||s.credential.length>256))throw Error('ICE server 오류다.');return servers;
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
    onState = () => {}, onTrack = () => {}, onStats = () => {}, onEnd = () => {}, onData = () => {},
    setTimer = (callback, interval) => globalThis.setInterval(callback, interval),
    clearTimer = timer => globalThis.clearInterval(timer), now = () => performance.now(), forceRelay = false }) {
    this.base = base; this.code = code; this.pin = pin; this.displayName = displayName;
    this.createPeer = createPeer; this.isCurrent = isCurrent; this.onState = onState;
    this.onTrack = onTrack; this.onStats = onStats; this.onEnd = onEnd;
    this.onData = onData; this.channels = new Map(); this.dataReceived = 0; this.dataSent = 0;
    this.generation=1;this.features=new FeatureSession(this);
    this.files=new FileSession(this);this.runbooks=new RunbookSession(this);
    this.inputSequence = 0; this.pointerSequence = 0; this.inputEpoch = 1; this.pendingPointer = null;
    this.supersededResets=new Map();
    this.setTimer = setTimer; this.clearTimer = clearTimer; this.now = now;
    this.pc = null; this.pendingIce = []; this.closed = false; this.reason = null;
    this.forceRelay = forceRelay; this.iceServers = [];
    this.messageChain = Promise.resolve(); this.timers = []; this.granted = false; this.queuedMessages = 0;
    this.lastSeen = now(); this.startedAt = now(); this.prePeerDeadline = this.startedAt + 60000; this.consentPendingAt = null; this.remoteReady = false;
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
      else if (!this.pc && now() >= this.prePeerDeadline) this.end('참가 또는 동의 대기 시간 초과다.', true);
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
    if (message.generation !== undefined && message.generation !== this.generation && !(message.type==='offer'&&message.generation===this.generation+1)) throw Error('다른 협상 세대다.');
    switch (message.type) {
      case 'joined':
        if (this.sessionId || !message.sessionId || !message.connectionId || message.role!==undefined&&message.role!=='viewer') throw Error('잘못된 ACK다.');
        this.sessionId = message.sessionId; this.connectionId = message.connectionId;
        if (message.iceServers !== undefined) {
          if (!Array.isArray(message.iceServers) || message.iceServers.length > 8) throw Error('ICE server 상한이다.');
          for (const server of message.iceServers) {
            if (!Array.isArray(server.urls) || server.urls.length > 8 || server.urls.some(url => typeof url !== 'string' || !/^(stun|stuns|turn|turns):/.test(url))) throw Error('잘못된 ICE server다.');
          }
          this.iceServers = message.iceServers;
        }
        this.onState('대상자의 명시적 동의를 기다리는 중이다.'); break;
      case 'peer-joined': this.onState('대상자의 명시적 동의를 기다리는 중이다.'); break;
      case 'consent-pending':
        if (this.consentPendingAt === null && !this.granted) {
          this.consentPendingAt = this.now(); this.prePeerDeadline = this.consentPendingAt + 125000;
        }
        this.onState('대상자의 명시적 동의를 기다리는 중이다.'); break;
      case 'consent':
        if (!Array.isArray(message.permissions) || !Number.isSafeInteger(message.permissionGeneration) || message.permissionGeneration < 1) throw Error('잘못된 승인 통지다.');
        if(this.granted||(permissionMask(message.permissions)&~255)!==0)throw Error('중복 또는 미지원 승인 통지다.');
        this.granted = true; this.prePeerDeadline = this.now() + 10000; this.permissionGeneration = message.permissionGeneration; this.permissions = [...message.permissions];
        this.onState('대상자가 승인한 기능을 연결하는 중이다.'); break;
      case 'consent-denied': this.end('대상자가 동의를 거절했다.'); break;
      case 'ice':
        if (!message.ice || typeof message.ice.candidate !== 'string' || message.ice.candidate.length > 8192) throw Error('잘못된 ICE다.');
        if (!this.remoteReady) {
          if (this.pendingIce.length >= 256) throw Error('ICE 상한이다.');
          this.pendingIce.push(message.ice);
        } else await this.pc.addIceCandidate(message.ice);
        break;
      case 'turn-credentials': this.iceServers=validateIceServers(message.iceServers??[]); break;
      case 'offer': await this.offer(message.sdp,message.generation??1); break;
      default: throw Error('알 수 없는 메시지다.');
    }
  }
  async offer(sdp,generation=1) {
    if(this.pc&&generation===this.generation+1){this.generation=generation;this.clearTransport();}
    else if(generation!==this.generation)throw Error('협상 세대 오류다.');
    if (this.pc || !this.granted || typeof sdp !== 'string' || sdp.length > 96 * 1024) throw Error('승인 없는/중복 offer다.');
    if (this.forceRelay && !this.iceServers.some(server => server.urls.some(url => /^turns?:/.test(url)))) throw Error('TURN 설정이 없다.');
    const pc = this.createPeer({ iceServers: this.iceServers, iceTransportPolicy: this.forceRelay ? 'relay' : 'all' }); this.pc = pc; this.negotiatedAt = this.now();
    // SDP promises can themselves stall. This deadline must exist before awaiting them.
    this.timers.push(this.setTimer(() => {
      if (this.live() && this.pc===pc && this.now() - this.negotiatedAt >= 30000 && pc.connectionState !== 'connected')
        this.end('WebRTC 협상 시간 초과다.', true);
    }, 1000));
    pc.ontrack = event => { if (this.live() && this.pc === pc) {if(!this.permissions.includes('screen'))this.end('승인하지 않은 화면 트랙이다.',true);else this.onTrack(event);} };
    pc.ondatachannel = event => { if (this.live() && this.pc === pc) this.acceptChannel(event.channel); else event.channel.close(); };
    pc.onicecandidate = event => {
      if (this.live() && this.pc === pc && event.candidate?.candidate)
        this.send({ type: 'ice',generation:this.generation, room: this.code, ice: {
          candidate: event.candidate.candidate, sdpMid: event.candidate.sdpMid ?? null,
          sdpMLineIndex: event.candidate.sdpMLineIndex ?? null
        } });
    };
    pc.onconnectionstatechange = () => {
      if (!this.live() || this.pc !== pc) return;
      if (pc.connectionState === 'connected') this.onState('대상자가 승인한 기능으로 연결됐다.');
      if(pc.connectionState==='closed')this.end('WebRTC 연결이 종료됐다.');
      else if(['failed','disconnected'].includes(pc.connectionState)){this.pendingPointer=null;this.recovering=true;this.recoverAt=this.now();this.features.clear();this.files.clear();this.runbooks.clear();this.onData({t:'input-paused'});this.onState('연결 복구 중이다. 원격 입력을 중단한다.');}
    };
    await pc.setRemoteDescription({ type: 'offer', sdp }); if (!this.live()) return;
    this.remoteReady = true;
    for (const ice of this.pendingIce.splice(0)) { await pc.addIceCandidate(ice); if (!this.live()) return; }
    const answer = await pc.createAnswer(); if (!this.live()) return;
    await pc.setLocalDescription(answer); if (!this.live()) return;
    this.send({ type: 'answer',generation:this.generation, room: this.code, sdp: answer.sdp });
    let busy = false; let lastBytes = 0; let lastTime = 0;let lastLost=0,lastPackets=0,lastDecoded=0,lastDecodeTime=0,lastVideoAt=this.now(),watchVideo=false;
    this.timers.push(this.setTimer(()=>{
      if(!this.live()||this.pc!==pc)return;
      const enabled=this.dataReady&&this.permissions.includes('screen')&&pc.connectionState==='connected'&&!this.recovering&&this.localApprovalKind!=='monitor'&&!this.adminRequestId;
      if(!enabled||!watchVideo)lastVideoAt=this.now();watchVideo=enabled;
      if(enabled&&this.now()-lastVideoAt>=15000)this.end('영상 디코딩 진행이 멈춰 지원 세션을 종료한다.',true);
    },500));
    this.timers.push(this.setTimer(async () => {
      if (!this.live() || this.pc!==pc || busy) return;
      if (this.now() - this.negotiatedAt >= 30000 && pc.connectionState !== 'connected') { this.end('WebRTC 협상 시간 초과다.', true); return; }
      busy = true;
      try {
        const report = await pc.getStats(); if (!this.live()||this.pc!==pc) return;
        const line = [];let sample=null,rtt=null;
        for (const entry of report.values()) {
          if (entry.type === 'inbound-rtp' && entry.kind === 'video') {
            if (entry.frameWidth) line.push(`${entry.frameWidth}×${entry.frameHeight}`);
            if (entry.framesPerSecond !== undefined) line.push(`디코딩 ${entry.framesPerSecond} fps`);
            const kbps=lastTime&&entry.timestamp>lastTime?Math.max(0,(entry.bytesReceived-lastBytes)*8/(entry.timestamp-lastTime)):null;
            if(kbps!==null)line.push(`수신 ${kbps.toFixed(0)} kbps`);
            lastBytes = entry.bytesReceived; lastTime = entry.timestamp;
            if(Number.isFinite(entry.packetsLost)&&Number.isFinite(entry.packetsReceived)&&Number.isFinite(entry.totalDecodeTime)&&Number.isFinite(entry.framesDecoded)){
              const lost=Math.max(0,entry.packetsLost-lastLost),packets=Math.max(0,entry.packetsReceived-lastPackets),frames=Math.max(0,entry.framesDecoded-lastDecoded),seconds=Math.max(0,entry.totalDecodeTime-lastDecodeTime);
              if(frames>0)lastVideoAt=this.now();
              sample=kbps!==null&&frames>0?{rttMs:0,lossPercent:packets+lost?100*lost/(packets+lost):0,decodeMs:seconds*1000/frames,receivedKbps:Math.min(100000,kbps)}:null;lastLost=entry.packetsLost;lastPackets=entry.packetsReceived;lastDecoded=entry.framesDecoded;lastDecodeTime=entry.totalDecodeTime;
            }
          }
          if (entry.type === 'transport' && entry.selectedCandidatePairId) {
            const pair = report.get(entry.selectedCandidatePairId);
            const local = report.get(pair?.localCandidateId); const remote = report.get(pair?.remoteCandidateId);
            if (local && remote) line.push(local.candidateType === 'relay' || remote.candidateType === 'relay' ? '경로: Relay' : '경로: P2P');
            if(Number.isFinite(pair?.currentRoundTripTime))rtt=pair.currentRoundTripTime*1000;
          }
        }
        this.onStats(line.join(' · '));
        if(sample&&rtt!==null&&this.dataReady&&this.permissions.includes('screen')&&pc.connectionState==='connected'){sample.rttMs=Math.min(10000,rtt);sample.decodeMs=Math.min(10000,sample.decodeMs);this.controlCommand('quality-report',{sample},crypto.randomUUID().replaceAll('-',''));}
      } catch { if (this.live()&&this.pc===pc) this.onStats('통계 조회 실패다.'); }
      finally { busy = false; }
    }, 1000));
  }
  acceptChannel(channel) {
    if (!(featureChannels(this.permissions).includes(channel.label)) || this.channels.has(channel.label) || channel.ordered !== true
        || channel.maxRetransmits !== null || channel.maxPacketLifeTime !== null) { channel.close(); this.end('미지원 또는 중복 기능 채널이다.', true); return; }
    this.channels.set(channel.label, channel);
    channel.onclose = () => { if (this.live()&&this.channels.get(channel.label)===channel&&!this.recovering) this.end('기능 채널이 종료됐다.', true); };
    channel.onerror = () => { if (this.live()&&this.channels.get(channel.label)===channel&&!this.recovering) this.end('기능 채널 전송 실패다.', true); };
    channel.onopen=()=>{if(this.live()&&this.channels.get(channel.label)===channel)this.onData({t:'feature-ready'});};
    if(channel.label==='runbook'){
      let chain=Promise.resolve(),queued=0;
      channel.onmessage=event=>{if(!this.live()||this.channels.get('runbook')!==channel)return;if(++queued>8){this.end('Runbook 수신 큐 상한이다.',true);return;}chain=chain.then(()=>this.live()&&this.channels.get('runbook')===channel&&this.runbooks.receive(parseData(event.data))).catch(()=>{if(this.channels.get('runbook')===channel)this.end('Runbook 채널 계약 오류다.',true);}).finally(()=>queued--);};return;
    }
    if(channel.label==='file'){
      channel.binaryType='arraybuffer';let chain=Promise.resolve(),queued=0;
      channel.onmessage=event=>{if(!this.live()||this.channels.get(channel.label)!==channel)return;if(++queued>32){this.end('파일 수신 큐 상한이다.',true);return;}chain=chain.then(()=>this.live()&&this.channels.get(channel.label)===channel&&this.files.receive(typeof event.data==='string'?parseData(event.data):event.data)).catch(()=>{if(this.channels.get(channel.label)===channel)this.end('파일 채널 계약 오류다.',true);}).finally(()=>queued--);};return;
    }
    if(['chat','telemetry'].includes(channel.label)) {
      let chain=Promise.resolve(),queued=0;
      channel.onmessage=event=>{
        if(!this.live()||this.channels.get(channel.label)!==channel)return;
        if(++queued>64){this.end('보조 채널 수신 큐 상한이다.',true);return;}
        let message;try{message=parseData(event.data);}catch{this.end('보조 채널 JSON 오류다.',true);return;}
        chain=chain.then(()=>this.live()&&this.channels.get(channel.label)===channel&&this.features.receive(channel.label,message)).catch(()=>{if(this.live()&&this.channels.get(channel.label)===channel)this.end('보조 채널 계약 오류다.',true);}).finally(()=>queued--);
      };
      return;
    }
    if (channel.label !== 'control') {
      channel.onmessage = () => {if(this.live()&&this.channels.get(channel.label)===channel)this.end('입력 채널에서 예상하지 않은 응답이다.', true);};
      return;
    }
    channel.onmessage = event => {
      if (!this.live()||this.channels.get(channel.label)!==channel) return;
      try {
        const message = parseData(event.data);
        if (message.sessionId !== this.sessionId || message.generation !== this.generation || message.permissionGeneration !== this.permissionGeneration
            || !Number.isSafeInteger(message.seq) || message.seq !== this.dataReceived + 1 || typeof message.id !== 'string' || message.id.length < 1 || message.id.length > 64)
          throw Error('다른 기능 채널 바인딩이다.');
        this.dataReceived = message.seq;
        const allowed = ['v','t','sessionId','generation','permissionGeneration','id','seq', ...(message.t === 'monitor-list'?['monitors']:message.t==='monitor-state'?['monitor']:message.t==='quality-state'?['quality']:message.t === 'data-ready' ? ['channels','permissions','monitor'] : message.t === 'input-reset-ack' ? ['inputEpoch'] : message.t === 'admin-state' ? ['state','adminGeneration','inputEpoch'] : message.t==='local-approval'?['kind','state','inputEpoch']:[])];
        if (Object.keys(message).some(key => !allowed.includes(key))) throw Error('알 수 없는 제어 필드다.');
        if (message.t === 'data-ready') {
          if (this.dataReady || JSON.stringify(message.channels) !== JSON.stringify(featureChannels(this.permissions))
              || JSON.stringify(message.permissions) !== JSON.stringify(this.permissions)) throw Error('기능 채널 준비 오류다.');
          const m = message.monitor;
          if (this.permissions.includes('screen')?(!m || typeof m.identity !== 'string' || m.identity.length < 1 || m.identity.length > 128 || (!Number.isSafeInteger(m.topologyGeneration)||m.topologyGeneration<1)
              || Object.keys(m).some(key=>!['identity','topologyGeneration','x','y','width','height'].includes(key))
              || ![m.x, m.y, m.width, m.height].every(Number.isSafeInteger) || m.x < -2147483648 || m.x > 2147483647 || m.y < -2147483648 || m.y > 2147483647 || m.width < 2 || m.height < 2 || m.width > 16384 || m.height > 16384):m!==undefined) throw Error('모니터 계약 오류다.');
          this.monitor = m?Object.freeze({ ...m }):null; this.dataReady = true;this.recovering=false; this.pingId = message.id;
          this.lastPong = this.now(); this.ping();
          this.timers.push(this.setTimer(() => {
            if (!this.live()||this.channels.get('control')!==channel) return;
            if(this.recovering){if(this.now()-this.recoverAt>=30000)this.end('연결 복구 시간 초과다.',true);return;}
            this.features.expire();this.files.expire();this.runbooks.expire();if(!this.live())return;
            if (this.pendingReset && !this.localApprovalId && !this.adminRequestId && this.now() - this.pendingReset.at >= 1000) this.end('입력 해제 응답 시간 초과다.', true);
            else if (this.now() - this.lastPong >= 2000) this.end('기능 채널 생존 확인 시간 초과다.', true); else this.ping();
          }, 500));
          this.timers.push(this.setTimer(() => {if(this.channels.get('control')===channel)this.flushPointer();}, 16));
        } else if(message.t==='monitor-list'){
          if(!this.permissions.includes('screen')||!Array.isArray(message.monitors)||message.monitors.length<1||message.monitors.length>16)throw Error('모니터 목록 오류다.');for(const m of message.monitors)this.validateMonitor(m);this.monitors=message.monitors;
        } else if(message.t==='monitor-state'){
          this.validateMonitor(message.monitor);if(!this.permissions.includes('screen')||message.monitor.topologyGeneration<this.monitor.topologyGeneration||message.monitor.topologyGeneration>this.monitor.topologyGeneration+1)throw Error('모니터 세대 오류다.');this.monitor=Object.freeze({...message.monitor});this.pendingPointer=null;
        } else if(message.t==='quality-state'){
          const q=message.quality;if(!this.permissions.includes('screen')||!q||Object.keys(q).some(k=>!['fps','bitrateKbps','longEdge','shortEdge','mode'].includes(k))||!Number.isInteger(q.fps)||q.fps<1||q.fps>60||!Number.isInteger(q.bitrateKbps)||q.bitrateKbps<100||q.bitrateKbps>50000||!Number.isInteger(q.longEdge)||q.longEdge<2||q.longEdge>3840||!Number.isInteger(q.shortEdge)||q.shortEdge<2||q.shortEdge>2160||q.shortEdge>q.longEdge||!['auto','detail','motion'].includes(q.mode))throw Error('품질 설정 오류다.');
        } else if(message.t==='local-approval') {
          if(!['file','runbook','monitor'].includes(message.kind)||!this.permissions.includes(message.kind==='monitor'?'screen':message.kind)||!['pending','resumed'].includes(message.state)||!/^[a-f0-9]{32}$/.test(message.id))throw Error('로컬 승인 응답 오류다.');
          if(message.state==='pending'){if(this.localApprovalId||this.adminRequestId||message.inputEpoch!==undefined)throw Error('승인 UI 중복이다.');this.localApprovalId=message.id;this.localApprovalKind=message.kind;this.pendingPointer=null;}
          else {
            if(this.localApprovalId!==message.id||this.localApprovalKind!==message.kind||!this.permissions.includes('control')&&message.inputEpoch!==undefined)throw Error('로컬 승인 epoch·바인딩 오류다.');
            if(this.permissions.includes('control'))this.resumeEpoch(message.inputEpoch);this.pointerSequence=0;this.pendingPointer=null;this.localApprovalId=this.localApprovalKind=null;
          }
        } else if(message.t === 'admin-state') {
          if(!this.permissions.includes('admin')||message.id!==this.adminRequestId||!['pending','denied','active'].includes(message.state))throw Error('예상하지 않은 관리자 응답이다.');
          if(message.state === 'pending') {if(message.adminGeneration!==undefined||message.inputEpoch!==undefined)throw Error('잘못된 관리자 대기 응답이다.');}
          else {
            this.resumeEpoch(message.inputEpoch);
            if(message.state === 'active' ? !Number.isSafeInteger(message.adminGeneration)||message.adminGeneration<1 : message.adminGeneration!==undefined)throw Error('관리자 세대 오류다.');
            this.inputEpoch=message.inputEpoch;this.pointerSequence=0;this.pendingPointer=null;this.adminRequestId=null;
            this.adminGeneration=message.state === 'active'?message.adminGeneration:null;
          }
        } else if (message.t === 'input-reset-ack') {
          if(this.supersededResets.get(message.id)===message.inputEpoch&&message.inputEpoch<=this.inputEpoch){this.supersededResets.delete(message.id);return;}
          if(!this.pendingReset || message.id !== this.pendingReset.id || message.inputEpoch !== this.inputEpoch) throw Error('오래되거나 다른 입력 해제 응답이다.');
          this.pendingReset = null;
        } else if (message.t !== 'data-pong' || !this.dataReady || message.id !== this.pingId) throw Error('예상하지 않은 제어 응답이다.');
        else this.lastPong = this.now();
        this.onData(message);
      } catch { this.end('잘못된 기능 채널 메시지다.', true); }
    };
  }
  resumeEpoch(epoch) {
    if(!Number.isSafeInteger(epoch)||(epoch!==this.inputEpoch+1&&!(this.pendingReset&&epoch===this.inputEpoch)))throw Error('로컬 승인 입력 epoch 오류다.');
    if(this.pendingReset){this.supersededResets.set(this.pendingReset.id,this.inputEpoch);if(this.supersededResets.size>16)this.supersededResets.delete(this.supersededResets.keys().next().value);this.pendingReset=null;}
    this.inputEpoch=epoch;
  }
  validateMonitor(m){if(!m||Object.keys(m).some(k=>!['identity','topologyGeneration','x','y','width','height'].includes(k))||typeof m.identity!=='string'||m.identity.length<1||m.identity.length>128||!Number.isSafeInteger(m.topologyGeneration)||m.topologyGeneration<1||![m.x,m.y,m.width,m.height].every(Number.isSafeInteger)||m.width<2||m.width>16384||m.height<2||m.height>16384)throw Error('모니터 geometry 오류다.');}
  ping() {
    const channel = this.channels.get('control');
    if (!this.live() || channel?.readyState !== 'open' || channel.bufferedAmount > 256 * 1024) { this.end('기능 채널 송신 상한이다.', true); return; }
    try { channel.send(JSON.stringify({ v: 1, t: 'data-ping', sessionId: this.sessionId, generation: this.generation, permissionGeneration: this.permissionGeneration, id: this.pingId, seq: ++this.dataSent })); }
    catch { this.end('기능 채널 송신 실패다.', true); }
  }
  canControl() { return this.live() && !this.recovering && !this.pendingReset && !this.adminRequestId && !this.localApprovalId && this.dataReady && this.permissions?.includes('control') && ['input','pointer'].every(label => this.channels.get(label)?.readyState === 'open'); }
  controlCommand(t,fields={},id) {
    const channel=this.channels.get('control');
    if(!this.live()||channel?.readyState!=='open'||channel.bufferedAmount>256*1024){this.end('기능 제어 송신 상태 오류다.',true);return false;}
    try{channel.send(JSON.stringify({...fields,v:1,t,sessionId:this.sessionId,generation:this.generation,permissionGeneration:this.permissionGeneration,id,seq:++this.dataSent}));return true;}
    catch{this.end('기능 제어 송신 실패다.',true);return false;}
  }
  requestAdmin(id=globalThis.crypto.randomUUID().replaceAll('-','')) {
    if(!this.canControl()||!this.permissions.includes('admin')||this.adminGeneration)return false;
    this.adminRequestId=id;this.onData({t:'input-paused'});return this.controlCommand('admin-request',{},id);
  }
  adminAction(t) {
    if(!this.canControl()||!this.adminGeneration||!['admin-sas','admin-revoke'].includes(t))return false;
    return this.controlCommand(t,{adminGeneration:this.adminGeneration},globalThis.crypto.randomUUID().replaceAll('-',''));
  }
  input(payload, pointer = false) {
    if (!this.canControl()) return false;
    if (pointer) { this.pendingPointer = payload; return true; }
    if(payload.t === 'mouse') { this.flushPointer(); this.pendingPointer = null; payload = {...payload, pointerBarrier:this.pointerSequence}; }
    const channel = this.channels.get('input');
    if (channel.bufferedAmount > 64 * 1024) { this.end('입력 채널 송신 상한이다.', true); return false; }
    try { channel.send(JSON.stringify(this.inputEnvelope(payload, ++this.inputSequence))); return true; }
    catch { this.end('입력 전송 실패다.', true); return false; }
  }
  inputEnvelope(payload, seq) {
    return { ...payload, v: 1, sessionId: this.sessionId, generation: this.generation, permissionGeneration: this.permissionGeneration,
      id: `input-${this.inputEpoch}-${seq}`, seq, inputEpoch: this.inputEpoch, monitorIdentity: this.monitor.identity, topologyGeneration: this.monitor.topologyGeneration };
  }
  flushPointer() {
    if (!this.canControl() || !this.pendingPointer) return;
    const channel = this.channels.get('pointer'); if (channel.bufferedAmount >= 2048) return;
    const latest = this.pendingPointer; this.pendingPointer = null;
    try { channel.send(JSON.stringify(this.inputEnvelope(latest, ++this.pointerSequence))); }
    catch { this.end('포인터 전송 실패다.', true); }
  }
  resetInput() {
    if (!this.canControl()) return;
    this.pendingPointer = null; this.inputEpoch++; this.pointerSequence = 0;
    if(this.input({ t: 'reset', a: 'all' })) {this.pendingReset={id:`input-${this.inputEpoch}-${this.inputSequence}`,at:this.now()};this.onData({t:'input-paused'});}
  }
  clearTransport(){
    const old=this.pc;this.pc=null;this.recovering=true;this.dataReady=false;
    const channels=[...this.channels.values()];this.channels.clear();for(const channel of channels)channel.close();old?.close();
    for(const timer of this.timers.splice(1))this.clearTimer(timer);
    this.files.clear();this.features.clear();this.runbooks.clear();this.files=new FileSession(this);this.features=new FeatureSession(this);this.runbooks=new RunbookSession(this);
    this.dataReceived=this.dataSent=this.inputSequence=this.pointerSequence=0;this.inputEpoch=1;this.remoteReady=false;this.pendingIce.length=0;
    this.pendingPointer=this.pendingReset=this.adminRequestId=this.localApprovalId=this.localApprovalKind=null;this.supersededResets.clear();this.onData({t:'input-paused'});
  }
  end(reason = '세션을 종료했다.', error = false) {
    if (this.closed || this.ending) return; this.ending = true;
    this.resetInput();
    this.reason ??= reason; this.closed = true; this.pin = '';
    for (const timer of this.timers.splice(0)) this.clearTimer(timer);
    this.pendingIce.length = 0;
    this.features.clear();
    this.files.clear();
    this.runbooks.clear();
    this.supersededResets.clear();
    for (const channel of this.channels.values()) channel.close(); this.channels.clear();
    this.pc?.close();
    if (this.ws.readyState < 2) this.ws.close(); // CONNECTING is also owned
    this.onEnd(this.reason, error);
  }
}
