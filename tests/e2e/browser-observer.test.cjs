'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm');
const {installBrowserObserver}=require('../../scripts/e2e-browser-observer.cjs');
function fixture(options={}){
 class Target { constructor(){this.listeners={};}addEventListener(k,f){(this.listeners[k]??=[]).push(f);}emit(k,value){for(const f of this.listeners[k]??[])f(value);} }
 class Channel extends Target {constructor(label){super();this.label=label;this.sent=[];}send(v){this.sent.push(v);return 17;}}
 class Peer extends Target {constructor(config){super();this.config=config;this.connectionState='connected';this.stats=new Map();}getConfiguration(){return this.config;}createDataChannel(label){return new Channel(label);}async getStats(){return this.stats;}}
 class Socket extends Target {constructor(url){super();this.url=url;this.sent=[];}send(v){this.sent.push(v);return 23;}}
 let closeCalls=0,pickerCalls=0;
 const writer={async write(){if(options.writeReject)throw Error('private-write');return 7;},async close(){closeCalls++;if(options.closeReject)throw Error('private-close');return 31;}},handle={async createWritable(){if(options.openReject)throw Error('private-open');return writer;}};
 const context=vm.createContext({RTCPeerConnection:Peer,WebSocket:Socket,performance:{now:()=>1234},
  document:{getElementById:()=>null},location:{search:''},URLSearchParams,addEventListener(){},queueMicrotask,ArrayBuffer,Uint8Array,DataView,
  async showSaveFilePicker(){pickerCalls++;if(options.pickerReject)throw Error('private-picker');return handle;}});
 vm.runInContext(`(${installBrowserObserver.toString()})()`,context);
 return {context,Peer,Socket,observer:context.__raE2eObservation,counts:()=>({closeCalls,pickerCalls})};
}
test('network wrappers forward exactly once and retain native types',()=>{
 const f=fixture(),pc=new f.context.RTCPeerConnection({iceTransportPolicy:'relay'}),ch=pc.createDataChannel('chat');
 assert(pc instanceof f.Peer);assert.equal(ch.send('{"t":"chat","text":"RA-한글😀"}'),17);assert.equal(ch.sent.length,1);
 const ws=new f.context.WebSocket('wss://example/ws');assert(ws instanceof f.Socket);assert.equal(ws.send('{"type":"join","pin":"1234"}'),23);assert.equal(ws.sent.length,1);
});
test('secrets and arbitrary clipboard data never enter metadata',()=>{
 const f=fixture(),ws=new f.context.WebSocket('wss://example/ws');
 ws.send(JSON.stringify({type:'join',pin:'secret-pin',token:'secret-token',sdp:'private-sdp',iceServers:[{credential:'turn-secret'}]}));
 const pc=new f.context.RTCPeerConnection({iceTransportPolicy:'all'}),ch=pc.createDataChannel('chat');
 ch.emit('message',{data:JSON.stringify({t:'clipboard-text',text:'ORIGINAL PRIVATE DATA',hash:'h'})});
 const log=JSON.stringify(f.observer.drain());for(const secret of ['secret-pin','secret-token','private-sdp','turn-secret','ORIGINAL PRIVATE DATA'])assert(!log.includes(secret));
 assert(log.includes('clipboard-text'));
});
test('RAFT metadata reflects header, sequence and bytes without payload',()=>{
 const f=fixture(),ch=new f.context.RTCPeerConnection({}).createDataChannel('file');
 const bytes=new Uint8Array(48),view=new DataView(bytes.buffer);bytes.set([82,65,70,84]);view.setUint16(4,1,true);view.setUint32(24,7,true);view.setUint32(28,16,true);
 ch.send(bytes.buffer);const row=f.observer.drain().find(e=>e.kind==='dc-sent-binary');
 assert.equal(row.validHeader,true);assert.equal(row.chunkSeq,7);assert.equal(row.length,16);assert(!('payload'in row));
});
test('only transport-selected pair counts as selected relay',async()=>{
 const f=fixture(),pc=new f.context.RTCPeerConnection({iceTransportPolicy:'relay'});
 pc.stats.set('unused',{id:'unused',type:'local-candidate',candidateType:'relay'});
 assert.equal((await f.observer.snapshot()).selected.length,0);
 pc.stats.set('transport',{type:'transport',selectedCandidatePairId:'pair'});pc.stats.set('pair',{id:'pair',localCandidateId:'local',remoteCandidateId:'remote',bytesReceived:123});
 pc.stats.set('local',{candidateType:'relay',address:'192.0.2.1',port:27100});pc.stats.set('remote',{candidateType:'host',address:'192.0.2.2',port:27002});
 const pair=(await f.observer.snapshot()).selected[0];assert.equal(pair.pairId,'pair');assert.equal(pair.localType,'relay');assert.equal(pair.bytesReceived,123);
});
test('save observer waits for real native picker and real close',async()=>{
 const f=fixture(),handle=await f.context.showSaveFilePicker({suggestedName:'ra-c08.bin'}),writer=await handle.createWritable();
 assert.equal(await writer.close(),31);assert.deepEqual(f.counts(),{pickerCalls:1,closeCalls:1});
 const kinds=f.observer.drain().map(e=>e.kind);assert.deepEqual(Array.from(kinds),['save-picker-open','save-picker-approved','save-stream-closed']);
});

test('native picker and writable rejection never record successful completion',async()=>{
 for(const option of ['pickerReject','openReject','closeReject']){
  const f=fixture({[option]:true});
  await assert.rejects(async()=>{const h=await f.context.showSaveFilePicker();const w=await h.createWritable();await w.close();});
  const events=f.observer.drain(),kinds=events.map(e=>e.kind);
  assert(!kinds.includes('save-stream-closed'));if(option==='pickerReject')assert(!kinds.includes('save-picker-approved'));
  assert(!JSON.stringify(events).includes('private-'));assert.equal(f.counts().pickerCalls,1);
 }
});
test('reused handles and writers retain one hook per real close',async()=>{
 const f=fixture();
 await f.context.showSaveFilePicker();const handle=await f.context.showSaveFilePicker();
 await handle.createWritable();const writer=await handle.createWritable();
 await writer.close();
 assert.equal(f.counts().closeCalls,1);assert.equal(f.observer.drain().filter(e=>e.kind==='save-stream-closed').length,1);
});
test('write rejection keeps native promise behavior and does not fabricate close',async()=>{
 const f=fixture({writeReject:true}),handle=await f.context.showSaveFilePicker(),writer=await handle.createWritable();
 await assert.rejects(writer.write('private-payload'));
 const events=f.observer.drain();assert(!events.some(e=>e.kind==='save-stream-closed'));assert(!JSON.stringify(events).includes('private-payload'));
});
