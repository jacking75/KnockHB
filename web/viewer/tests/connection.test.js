import test from 'node:test';
import assert from 'node:assert/strict';
import { ViewerConnection, parseSignal, retryable } from '../connection.js';

class Socket {
  constructor() { this.readyState = 0; this.sent = []; this.closeCount = 0; }
  send(data) { assert.equal(this.readyState, 1); this.sent.push(JSON.parse(data)); }
  close() { this.closeCount++; this.readyState = 3; this.onclose?.(); }
  open() { this.readyState = 1; this.onopen?.(); }
  message(message) { this.onmessage?.({ data: JSON.stringify({ v: 1, ...message }) }); }
}
const joined = { type: 'joined', sessionId: 'sid', connectionId: 'cid', generation: 1 };
function fixture(overrides = {}) {
  const socket = new Socket(); const end = []; const timers = [];
  const peer = { connectionState: 'connected', candidates: [], async setRemoteDescription() {}, async createAnswer() { return { sdp: 'answer' }; },
    async setLocalDescription() {}, async addIceCandidate(value) { this.candidates.push(value); }, close() { this.closed = true; }, async getStats() { return new Map(); } };
  const connection = new ViewerConnection({ base: 'http://localhost:25001', code: '123456789', displayName: 'tester',
    createSocket: () => socket, createPeer: () => peer, onEnd: (...args) => end.push(args),
    setTimer: fn => { timers.push(fn); return fn; }, clearTimer: () => {}, ...overrides });
  socket.open(); socket.message(joined);
  return { connection, socket, peer, end, timers };
}
test('ICE preceding offer is retained and applied after SDP', async () => {
  const f = fixture(); f.socket.message({ type: 'ice', ice: { candidate: 'candidate:1' } });
  f.socket.message({ type: 'offer', sdp: 'offer' }); await f.connection.messageChain;
  assert.equal(f.peer.candidates.length, 1); assert.equal(f.socket.sent.at(-1).type, 'answer'); f.connection.end();
});

test('signaling rejects unknown fields and invalid nested ICE before dispatch',()=>{
  for(const message of [{v:1,type:'pong',unknown:true},{v:1,type:'ice',ice:{candidate:'candidate:1',extra:true}},{v:1,type:'ice',ice:{candidate:'candidate:1',sdpMLineIndex:-1}},{v:1,type:'joined',iceServers:[{urls:['turn:host'],extra:true}]}])assert.throws(()=>parseSignal(JSON.stringify(message)));
});

test('browser ICE extensions never reach the strict signaling wire contract', async () => {
  const f = fixture(); f.socket.message({ type: 'offer', sdp: 'offer' }); await f.connection.messageChain;
  const candidate = { candidate: 'candidate:1 1 udp 1 192.0.2.1 27002 typ host', sdpMid: '0', sdpMLineIndex: 0,
    usernameFragment: 'browser-extension', toJSON() { return { ...this, toJSON: undefined }; } };
  f.peer.onicecandidate({ candidate });
  assert.deepEqual(f.socket.sent.at(-1).ice, { candidate: candidate.candidate, sdpMid: '0', sdpMLineIndex: 0 });
  const count = f.socket.sent.length;
  f.peer.onicecandidate({ candidate: { candidate: '', sdpMid: '0', sdpMLineIndex: 0 } });
  f.peer.onicecandidate({ candidate: null });
  assert.equal(f.socket.sent.length, count); f.connection.end();
});
test('not-found failure survives socket close', async () => {
  const f = fixture(); f.socket.message({ type: 'error', code: 'not-found' }); f.socket.onclose();
  assert.deepEqual(f.end, [['대상 PC가 아직 지원 대기 중이 아니다.', true]]);
});
test('cancel during remote SDP prevents answer and late track', async () => {
  let release; let entered; const started = new Promise(resolve => { entered = resolve; }); let tracks = 0;
  const f = fixture({ onTrack: () => tracks++ });
  f.peer.setRemoteDescription = () => { entered(); return new Promise(resolve => { release = resolve; }); };
  f.socket.message({ type: 'offer', sdp: 'offer' }); await started;
  f.connection.end(); release(); await f.connection.messageChain;
  f.peer.ontrack({ streams: [] }); assert.equal(tracks, 0); assert.equal(f.socket.sent.some(m => m.type === 'answer'), false);
});
test('previous socket callbacks cannot alter new connection', () => {
  let current; let old; old = fixture({ isCurrent: () => !old || current === old.connection }); current = old.connection;
  old.connection.end(); const next = fixture(); current = next.connection;
  old.socket.onopen(); old.socket.message({ type: 'peer-left' }); old.socket.onclose();
  assert.equal(next.connection.closed, false); assert.equal(next.socket.closeCount, 0); next.connection.end();
});
test('CONNECTING socket is closed on cancellation', () => {
  const socket = new Socket(); const c = new ViewerConnection({ base: 'http://localhost', createSocket: () => socket,
    setTimer: () => 1, clearTimer: () => {} }); c.end(); assert.equal(socket.closeCount, 1); socket.onopen(); assert.equal(socket.sent.length, 0);
});
test('old stats promise cannot mutate UI after teardown', async () => {
  let release; let updates = 0; const f = fixture({ onStats: () => updates++ });
  f.socket.message({ type: 'offer', sdp: 'offer' }); await f.connection.messageChain;
  f.peer.getStats = () => new Promise(resolve => { release = resolve; }); const pending = f.timers.at(-1)();
  f.connection.end(); release(new Map()); await pending; assert.equal(updates, 0);
});

test('previous transport stats rejection cannot overwrite the new transport display',async()=>{
  let reject,updates=0;const f=fixture({onStats:()=>updates++});
  f.socket.message({type:'offer',sdp:'offer'});await f.connection.messageChain;
  f.peer.getStats=()=>new Promise((_,fail)=>{reject=fail;});const pending=f.timers.at(-1)();
  f.connection.clearTransport();reject(Error('old stats'));await pending;assert.equal(updates,0);f.connection.end();
});

test('stale input, chat and telemetry callbacks cannot terminate or update a replacement transport',()=>{
  let observed=0;const f=fixture({onData:()=>observed++});f.connection.permissions=['screen','control','clipboard','diagnostics','chat'];
  const channels=['input','chat','telemetry'].map(label=>({label,ordered:true,maxRetransmits:null,maxPacketLifeTime:null,readyState:'open',bufferedAmount:0,close(){}}));
  for(const channel of channels)f.connection.acceptChannel(channel);
  f.connection.clearTransport();observed=0;
  for(const channel of channels){channel.onmessage({data:'malformed stale message'});channel.onopen();}
  assert.equal(f.connection.closed,false);assert.equal(observed,0);f.connection.end();
});
test('heartbeat deadline closes blackholed signaling', () => {
  let now = 0; const f = fixture({ now: () => now }); now = 15000; f.timers[0](); assert.equal(f.connection.closed, true);
});

test('join without an offer stays bounded at 20 seconds', async () => {
  let now = 0; const join = fixture({ now: () => now }); await join.connection.messageChain;
  now = 19999; join.socket.message({ type: 'pong' }); join.timers[0]();
  assert.equal(join.connection.closed, false);
  now = 20000; join.socket.message({ type: 'pong' }); join.timers[0]();
  assert.equal(join.connection.closed, true);
});
test('replaced viewer stops retrying while every other refusal retries', () => {
  const f = fixture(); f.socket.message({ type: 'error', code: 'replaced' });
  assert.equal(f.connection.errorCode, 'replaced'); assert.equal(retryable('replaced'), false);
  for (const code of ['not-found', 'busy', 'consumed', null]) assert.equal(retryable(code), true);
});
test('duplicate keys, old versions and non-text are rejected', () => {
  for (const text of ['{"v":1,"v":1,"type":"ping"}', '{"v":1,"type":"ice","ice":{"a":1,"a":2}}', '{"type":"ping"}', '{"v":0,"type":"ping"}', null])
    assert.throws(() => parseSignal(text));
  assert.equal(parseSignal('{"v":1,"type":"error","reason":"한글 😀"}').reason, '한글 😀');
});
test('stalled SDP has an independent negotiation deadline', async () => {
  let now = 0; let entered; let release;
  const started = new Promise(resolve => { entered = resolve; });
  const f = fixture({ now: () => now }); f.peer.connectionState = 'connecting';
  f.peer.setRemoteDescription = () => { entered(); return new Promise(resolve => { release = resolve; }); };
  f.socket.message({ type: 'offer', sdp: 'offer' }); await started;
  now = 30000; f.timers[1]();
  assert.equal(f.connection.closed, true); assert.equal(f.end[0][0], 'WebRTC 협상 시간 초과다.');
  release(); await f.connection.messageChain;
  assert.equal(f.socket.sent.some(message => message.type === 'answer'), false);
});
test('pending protocol work has a bounded queue', async () => {
  const f = fixture();
  for (let i = 0; i < 129; i++) f.socket.message({ type: 'peer-joined' });
  assert.equal(f.connection.closed, true);
  await f.connection.messageChain;
  assert.equal(f.connection.queuedMessages, 0);
});
test('duplicate offer terminates rather than starting a second peer', async () => {
  let peers = 0; const f = fixture({ createPeer: () => { peers++; return f.peer; } });
  f.socket.message({ type: 'offer', sdp: 'offer' }); await f.connection.messageChain;
  f.socket.message({ type: 'offer', sdp: 'offer2' }); await f.connection.messageChain;
  assert.equal(peers, 1); assert.equal(f.connection.closed, true);
});
