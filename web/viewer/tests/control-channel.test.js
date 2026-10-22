import test from 'node:test';
import assert from 'node:assert/strict';
import { ViewerConnection, parseData } from '../connection.js';

function session() {
  const messages = [], ends = [], socket = { readyState: 0, close() { this.readyState = 3; }, send() {} };
  const viewer = new ViewerConnection({ base: 'http://localhost:25001', code: '123', displayName: 'test',
    createSocket: () => socket, setTimer: () => 1, clearTimer() {}, onData: m => messages.push(m), onEnd: reason => ends.push(reason) });
  viewer.sessionId = 'sid'; viewer.permissionGeneration = 1; viewer.permissions = ['screen'];
  const sent = [], channel = { label: 'control', ordered: true, maxRetransmits: null, maxPacketLifeTime: null, readyState: 'open', bufferedAmount: 0,
    send: text => sent.push(JSON.parse(text)), close() { this.readyState = 'closed'; this.onclose?.(); } };
  const ready = { v: 1, t: 'data-ready', sessionId: 'sid', generation: 1, permissionGeneration: 1, id: 'id', seq: 1,
    channels: ['control'], permissions: ['screen'], monitor: { identity: 'DISPLAY', topologyGeneration: 1, x: -2160, y: 0, width: 2160, height: 3840 } };
  return { viewer, messages, ends, channel, sent, ready };
}
test('control ready binds negative origin monitor and encrypted pong correlation', () => {
  const s = session(); s.viewer.acceptChannel(s.channel); s.channel.onmessage({ data: JSON.stringify(s.ready) });
  assert.equal(s.viewer.closed, false); assert.equal(s.viewer.monitor.x, -2160); assert.equal(s.sent[0].t, 'data-ping');
  s.channel.onmessage({ data: JSON.stringify({ ...s.sent[0], t: 'data-pong', seq: 2 }) });
  assert.equal(s.messages[1].t, 'data-pong'); s.viewer.end();
});
test('old permission, duplicate ready, non-text and unknown data fail closed', () => {
  for (const action of [s => ({ ...s.ready, permissionGeneration: 2 }), s => ({ ...s.ready, t: 'execute-script' }), () => new Uint8Array(2)]) {
    const s = session(); s.viewer.acceptChannel(s.channel); const payload = action(s);
    s.channel.onmessage({ data: payload instanceof Uint8Array ? payload : JSON.stringify(payload) });
    assert.equal(s.viewer.closed, true);
  }
  const s = session(); s.viewer.acceptChannel(s.channel); s.channel.onmessage({ data: JSON.stringify(s.ready) });
  s.channel.onmessage({ data: JSON.stringify({ ...s.ready, seq: 2 }) }); assert.equal(s.viewer.closed, true);
  assert.throws(() => parseData('{"v":1,"t":"data-ready","seq":1,"seq":2}'));
});
test('duplicate or unreliable control and buffered overflow close the whole session', () => {
  for (const change of [{ ordered: false }, { maxRetransmits: 0 }, { label: 'input' }, { bufferedAmount: 262145 }]) {
    const s = session(); Object.assign(s.channel, change); s.viewer.acceptChannel(s.channel);
    s.channel.onmessage?.({ data: JSON.stringify(s.ready) }); assert.equal(s.viewer.closed, true);
  }
  const s = session(); s.viewer.acceptChannel(s.channel); s.viewer.acceptChannel({ ...s.channel }); assert.equal(s.viewer.closed, true);
});

test('admin second approval pauses input and changes epoch only on the correlated Host response',()=>{
  const s=session();s.viewer.permissions=['screen','control','admin'];
  s.ready.permissions=[...s.viewer.permissions];s.ready.channels=['control','input','pointer'];
  s.viewer.channels.set('input',{readyState:'open',bufferedAmount:0,send(){},close(){}});
  s.viewer.channels.set('pointer',{readyState:'open',bufferedAmount:0,send(){},close(){}});
  s.viewer.acceptChannel(s.channel);s.channel.onmessage({data:JSON.stringify(s.ready)});
  const id='0123456789abcdef0123456789abcdef';
  assert.equal(s.viewer.requestAdmin(id),true);assert.equal(s.viewer.canControl(),false);
  assert.equal(s.viewer.input({t:'key',a:'down',vk:65}),false);
  s.channel.onmessage({data:JSON.stringify({v:1,t:'admin-state',sessionId:'sid',generation:1,permissionGeneration:1,id,seq:2,state:'pending'})});
  assert.equal(s.viewer.adminGeneration,undefined);
  s.channel.onmessage({data:JSON.stringify({v:1,t:'admin-state',sessionId:'sid',generation:1,permissionGeneration:1,id,seq:3,state:'active',adminGeneration:1,inputEpoch:2})});
  assert.equal(s.viewer.adminGeneration,1);assert.equal(s.viewer.inputEpoch,2);assert.equal(s.viewer.canControl(),true);
  s.viewer.end();
});
test('unsolicited admin active state cannot grant privileges',()=>{
  const s=session();s.viewer.acceptChannel(s.channel);s.channel.onmessage({data:JSON.stringify(s.ready)});
  s.channel.onmessage({data:JSON.stringify({v:1,t:'admin-state',sessionId:'sid',generation:1,permissionGeneration:1,id:'spoofed',seq:2,state:'active',adminGeneration:1,inputEpoch:2})});
  assert.equal(s.viewer.closed,true);assert.equal(s.viewer.adminGeneration,undefined);
});

test('monitor selection is blocked during administrator startup and active mode while quality remains usable',()=>{
  const s=session();s.viewer.acceptChannel(s.channel);s.channel.onmessage({data:JSON.stringify(s.ready)});
  const id='0123456789abcdef0123456789abcdef';
  assert.equal(s.viewer.controlCommand('monitor-select',{monitorIdentity:'DISPLAY2'},id),true);
  s.viewer.adminRequestId=id;
  const before=s.sent.length;
  assert.equal(s.viewer.controlCommand('monitor-select',{monitorIdentity:'DISPLAY2'},id),false);
  s.viewer.adminRequestId=null;s.viewer.adminGeneration=1;
  assert.equal(s.viewer.controlCommand('monitor-select',{monitorIdentity:'DISPLAY2'},id),false);
  assert.equal(s.sent.length,before);
  assert.equal(s.viewer.controlCommand('quality-mode',{mode:'detail'},id),true);
  assert.equal(s.viewer.closed,false);s.viewer.end();
});

test('initial admin waits for negotiated capability and input readiness, then requests only once across denial and recovery',()=>{
  const s=session();
  assert.equal(s.viewer.requestInitialAdmin(),false);
  s.viewer.permissions=['screen','control','admin'];s.ready.permissions=[...s.viewer.permissions];s.ready.channels=['control','input','pointer'];
  s.viewer.acceptChannel(s.channel);s.channel.onmessage({data:JSON.stringify(s.ready)});
  assert.equal(s.viewer.requestInitialAdmin(),false);
  for(const label of ['input','pointer'])s.viewer.channels.set(label,{readyState:'open',bufferedAmount:0,send(){},close(){}});
  assert.equal(s.viewer.requestInitialAdmin(),true);
  const id=s.viewer.adminRequestId;
  assert.equal(s.viewer.requestInitialAdmin(),false);
  assert.equal(s.sent.filter(m=>m.t==='admin-request').length,1);
  s.channel.onmessage({data:JSON.stringify({v:1,t:'admin-state',sessionId:'sid',generation:1,permissionGeneration:1,id,seq:2,state:'denied',inputEpoch:2})});
  assert.equal(s.viewer.requestInitialAdmin(),false);
  assert.equal(s.viewer.requestAdmin(),true); // Explicit supporter retry remains available.
  s.viewer.clearTransport();assert.equal(s.viewer.initialAdminAttempted,true);
  s.viewer.end();
});

test('automatic admin does not run for screen/control-only permissions or during local approval',()=>{
  const s=session();s.viewer.dataReady=true;
  for(const label of ['input','pointer'])s.viewer.channels.set(label,{readyState:'open',close(){}});
  s.viewer.permissions=['screen','control'];assert.equal(s.viewer.requestInitialAdmin(),false);
  s.viewer.permissions.push('admin');s.viewer.localApprovalId='pending';assert.equal(s.viewer.requestInitialAdmin(),false);
  assert.equal(s.viewer.initialAdminAttempted,undefined);s.viewer.end();
});
test('file approval suspends input and resolves reset versus local epoch races without accepting unknown ACKs',()=>{
  for(const epoch of [2,3]) {
    const s=session();s.viewer.permissions=['screen','control','file'];s.ready.permissions=[...s.viewer.permissions];s.ready.channels=['control','input','pointer','file'];
    for(const label of ['input','pointer','file'])s.viewer.channels.set(label,{readyState:'open',bufferedAmount:0,send(){},close(){}});
    s.viewer.acceptChannel(s.channel);s.channel.onmessage({data:JSON.stringify(s.ready)});s.viewer.resetInput();const resetId=s.viewer.pendingReset.id;
    const id='0123456789abcdef0123456789abcdef',base={v:1,sessionId:'sid',generation:1,permissionGeneration:1,id,t:'local-approval',kind:'file'};
    s.channel.onmessage({data:JSON.stringify({...base,seq:2,state:'pending'})});assert.equal(s.viewer.canControl(),false);
    s.channel.onmessage({data:JSON.stringify({...base,seq:3,state:'resumed',inputEpoch:epoch})});assert.equal(s.viewer.canControl(),true);assert.equal(s.viewer.inputEpoch,epoch);
    s.channel.onmessage({data:JSON.stringify({...base,t:'input-reset-ack',kind:undefined,id:resetId,seq:4,state:undefined,inputEpoch:2})});assert.equal(s.viewer.closed,false);
    s.channel.onmessage({data:JSON.stringify({...base,t:'input-reset-ack',kind:undefined,id:'unknown',seq:5,state:undefined,inputEpoch:2})});assert.equal(s.viewer.closed,true);
  }
});
