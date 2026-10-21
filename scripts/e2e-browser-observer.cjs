'use strict';

// Executed as an init script BEFORE application code. All wrappers call the
// original operation exactly once, preserve receiver/arguments/result, and only
// observe it. GUI actions remain exclusively DesktopTestDriver operations.
function installBrowserObserver() {
  const events = [], peers = [], channels = new WeakSet(),bookSources=new Map();
  let fileOfferId=null;
  let overflow = false, serial = 0;
  const record = (kind, fields = {}) => {
    if (events.length >= 16384) { overflow = true; return; }
    events.push({seq: ++serial, tick: performance.now(), kind, ...fields});
  };
  const safeMessage = value => {
    if (typeof value !== 'string' || value.length > 131072) return null;
    let m; try { m = JSON.parse(value); } catch { return null; }
    // Whitelist metadata. Never persist PIN/token/SDP/ICE credentials, original
    // clipboard text, arbitrary chat, runbook stdout, machine diagnostics lists.
    const out = {};
    for (const k of ['v','t','type','sessionId','connectionId','generation','permissionGeneration',
      'permissions','channels','monitor','id','seq','epoch','barrier','state','kind','code','hash','size','nextChunk',
      'inputEpoch','adminGeneration','monitorIdentity','topologyGeneration','width','height']) {
      if (m[k] !== undefined && JSON.stringify(m[k]).length < 2048) out[k] = m[k];
    }
    if (['ra-c07.bin','ra-c08.bin','ra-c09.bin'].includes(m.name)) out.name = m.name;
    if (typeof m.text === 'string') {
      out.textEqualsTest = ['RA-한글😀','ACK-RA','RA-CLIP😀'].includes(m.text);
      if (out.textEqualsTest) out.testText = m.text;
    }
    if (m.t === 'telemetry' && m.snapshot) {
      out.snapshot = {utc:m.snapshot.utc,cpuPercent:m.snapshot.cpuPercent,memory:m.snapshot.memory,
        unavailableProviders:Object.entries(m.snapshot).filter(([,v])=>v===null).map(([k])=>k)};
    }
    if (m.t === 'runbook-catalog') out.books = m.books?.map(b => ({bookId:b.bookId,bookVersion:b.bookVersion,
      hash:b.hash,requiresAdmin:b.requiresAdmin}));
    if(m.t==='runbook-catalog')for(const b of m.books??[])bookSources.set(b.bookId,b.source);
    if(m.t==='file-offer')fileOfferId=m.id;
    if(m.type && !['error','join-error'].includes(m.type))delete out.code;
    if (m.t === 'runbook-result' && m.result) out.result = {state:m.result.state,exitCode:m.result.exitCode};
    return out;
  };
  const message = (kind, label, value) => {
    if (typeof value === 'string') { const m = safeMessage(value); if (m) record(kind,{label,message:m}); return; }
    if (value instanceof ArrayBuffer || ArrayBuffer.isView(value)) {
      const b = value instanceof ArrayBuffer ? new Uint8Array(value) : new Uint8Array(value.buffer,value.byteOffset,value.byteLength);
      const fields = {label,bytes:b.byteLength};
      if (label === 'file' && b.byteLength >= 32) {
        const v = new DataView(b.buffer,b.byteOffset,b.byteLength);
        fields.validHeader = v.getUint32(0) === 0x52414654 && v.getUint16(4,true) === 1 && v.getUint16(6,true) === 0 && v.getUint32(28,true) === b.byteLength-32;
        fields.id = [...b.slice(8,24)].map(x=>x.toString(16).padStart(2,'0')).join('');
        fields.chunkSeq = v.getUint32(24,true); fields.length = v.getUint32(28,true);
      }
      record(kind+'-binary',fields);
    } else record('unsupported-message',{label});
  };
  function observeChannel(channel, peerId) {
    if (channels.has(channel)) return channel;
    channels.add(channel); record('channel-created',{label:channel.label,peerId});
    const send = channel.send;
    channel.send = function(value) { const result=Reflect.apply(send,this,[value]); message('dc-sent',this.label,value); return result; };
    channel.addEventListener('message',event=>message('dc-received',channel.label,event.data));
    channel.addEventListener('open',()=>record('channel-open',{label:channel.label,peerId}));
    channel.addEventListener('close',()=>record('channel-close',{label:channel.label,peerId}));
    return channel;
  }
  const OriginalPeer = globalThis.RTCPeerConnection;
  globalThis.RTCPeerConnection = new Proxy(OriginalPeer,{construct(target,args,newTarget) {
    const pc = Reflect.construct(target,args,newTarget), peerId = peers.length+1;
    peers.push({pc,peerId}); record('peer-created',{peerId,policy:pc.getConfiguration().iceTransportPolicy});
    const create = pc.createDataChannel;
    pc.createDataChannel = function(...values) { return observeChannel(Reflect.apply(create,this,values),peerId); };
    pc.addEventListener('datachannel',event=>observeChannel(event.channel,peerId));
    pc.addEventListener('track',event=>record('track',{peerId,mediaKind:event.track.kind}));
    pc.addEventListener('connectionstatechange',()=>record('peer-state',{peerId,state:pc.connectionState}));
    return pc;
  }});
  const OriginalSocket = globalThis.WebSocket;
  globalThis.WebSocket = new Proxy(OriginalSocket,{construct(target,args,newTarget) {
    const socket = Reflect.construct(target,args,newTarget), send = socket.send;
    socket.send = function(value) { const result=Reflect.apply(send,this,[value]);message('signal-sent','signaling',value);return result; };
    socket.addEventListener('message',event=>message('signal-received','signaling',event.data));
    return socket;
  }});
  // Observe actual native save picker and actual writer.close(). No fake handles.
  if (globalThis.showSaveFilePicker) {
    const picker = globalThis.showSaveFilePicker;
    globalThis.showSaveFilePicker = async function(...args) {
      record('save-picker-open'); const handle = await Reflect.apply(picker,this,args);
      record('save-picker-approved'); const open = handle.createWritable;
      handle.createWritable = async function(...values) {
        const writer = await Reflect.apply(open,this,values), close = writer.close;
        writer.close = async function(...parameters) {const result=await Reflect.apply(close,this,parameters);record('save-stream-closed');return result;};
        return writer;
      };
      return handle;
    };
  }
  addEventListener('keydown',event=>{if(event.key==='F12')queueMicrotask(()=>record('f12-keydown',{defaultPrevented:event.defaultPrevented}));},true);
  const element = id => document.getElementById(id);
  const textEquals = (id,value) => element(id)?.textContent?.includes(value) ?? false;
  const snapshot = async (marker = null) => {
    const selected=[];
    for (const {pc,peerId} of peers) {
      if (pc.connectionState === 'closed') continue;
      const stats = await pc.getStats();
      for (const transport of stats.values()) {
        if (transport.type !== 'transport' || !transport.selectedCandidatePairId) continue;
        const pair=stats.get(transport.selectedCandidatePairId), local=pair&&stats.get(pair.localCandidateId), remote=pair&&stats.get(pair.remoteCandidateId);
        if (pair && local && remote) selected.push({peerId,policy:pc.getConfiguration().iceTransportPolicy,
          selected:true,pairId:pair.id,localType:local.candidateType,remoteType:remote.candidateType,
          localAddress:local.address,localPort:local.port,remoteAddress:remote.address,remotePort:remote.port,
          protocol:local.protocol,bytesReceived:pair.bytesReceived,bytesSent:pair.bytesSent});
      }
    }
    const video=element('screen'), r=video?.getBoundingClientRect();
    const bookSource=bookSources.get(element('runbook-choice')?.value),preview=element('runbook-preview')?.textContent??'';
    const previewSource=typeof bookSource==='string'&&preview.endsWith(bookSource)?preview.slice(-bookSource.length).replaceAll('\r\n','\n'):null;
    const bookPreviewHash=previewSource===null?null:[...new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(previewSource)))].map(x=>x.toString(16).padStart(2,'0')).join('');
    let markerMatched=null;
    if(marker&&video?.videoWidth>0&&video?.videoHeight>0){
      const canvas=document.createElement('canvas');canvas.width=video.videoWidth;canvas.height=video.videoHeight;
      const context=canvas.getContext('2d',{willReadFrequently:true});context.drawImage(video,0,0);
      const bytes=marker.id.match(/../g).map(x=>parseInt(x,16));let matched=0;
      for(let i=0;i<128;i++){
        const px=Math.floor((marker.x+((i%16)+.5)*marker.width/16-marker.monitor.x)*video.videoWidth/marker.monitor.width);
        const py=Math.floor((marker.y+(Math.floor(i/16)+.5)*marker.height/8-marker.monitor.y)*video.videoHeight/marker.monitor.height);
        if(px<0||py<0||px>=canvas.width||py>=canvas.height)continue;
        const rgb=context.getImageData(px,py,1,1).data,level=(rgb[0]+rgb[1]+rgb[2])/3;
        const white=!!(bytes[Math.floor(i/8)]&(1<<(7-i%8)));
        if(white?level>192:level<63)matched++;
      }
      markerMatched=matched===128;
    }
    return {tick:performance.now(),frequency:1000,overflow,eventCount:serial,selected,
      effectiveRelayPolicy:new URLSearchParams(location.search).get('relay')==='1'?'relay':'all',
      pinEmpty:element('pin')?.value==='',
      markerMatched,
      frames:video?.getVideoPlaybackQuality().totalVideoFrames ?? 0,videoWidth:video?.videoWidth ?? 0,
      videoHeight:video?.videoHeight ?? 0,srcObjectPresent:!!video?.srcObject,
      viewport:r?{x:r.x,y:r.y,width:r.width,height:r.height}:null,
      joinVisible:element('join')?.hidden===false,inputEnabled:element('remote-text')?.disabled===false,
      chatEnabled:element('chat-send')?.disabled===false,clipboardEqualsTest:element('clipboard-text')?.value==='RA-CLIP😀',
      chatAEqualsTest:textEquals('chat-history','RA-한글😀'),chatBEqualsTest:textEquals('chat-history','ACK-RA'),
      chatBLineCount:(element('chat-history')?.textContent??'').split('\n').filter(x=>x==='대상자: ACK-RA').length,
      chatRenderMode:element('chat-history')?.children?.length===0?'textContent':'elements',fileOfferId,
      diagnosticsHasNA:textEquals('diagnostics-content','N/A'),diagnosticsDelayed:textEquals('diagnostics-status','지연'),
      fileSource:element('file-source')?.files?.length===1?{name:element('file-source').files[0].name,size:element('file-source').files[0].size}:null,
      bookId:element('runbook-choice')?.value,bookDetail:element('runbook-detail')?.value,
      bookPreviewHash,
      inputReady:element('session')?.dataset.inputReady,controlState:element('session')?.dataset.controlState};
  };
  Object.defineProperty(globalThis,'__raE2eObservation',{value:{snapshot,drain:()=>events.splice(0)},writable:false});
}
module.exports = {installBrowserObserver};
