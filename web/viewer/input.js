import { videoPoint } from './input-geometry.js';

export function virtualKey(code) {
  if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3);
  if (/^Digit[0-9]$/.test(code)) return code.charCodeAt(5);
  if (/^F(?:[1-9]|1[0-9]|2[0-4])$/.test(code)) return 111 + Number(code.slice(1));
  if (/^Numpad[0-9]$/.test(code)) return 96 + Number(code.slice(6));
  return ({ Backspace:8,Tab:9,Enter:13,NumpadEnter:13,Escape:27,Space:32,PageUp:33,PageDown:34,End:35,Home:36,ArrowLeft:37,ArrowUp:38,ArrowRight:39,ArrowDown:40,
    Insert:45,Delete:46,ShiftLeft:160,ShiftRight:161,ControlLeft:162,ControlRight:163,AltLeft:164,AltRight:165,MetaLeft:91,MetaRight:92,
    CapsLock:20,NumLock:144,ScrollLock:145,ContextMenu:93,NumpadMultiply:106,NumpadAdd:107,NumpadSubtract:109,NumpadDecimal:110,NumpadDivide:111,
    Semicolon:186,Equal:187,Comma:188,Minus:189,Period:190,Slash:191,Backquote:192,BracketLeft:219,Backslash:220,BracketRight:221,Quote:222 })[code];
}

export class ViewerInput {
  constructor(connection, video, text, sendText, root = globalThis, doc = document) {
    this.connection=connection;this.video=video;this.text=text;this.keys=new Set();this.buttons=new Set();this.composing=false;
    this.abort=new AbortController();const options={signal:this.abort.signal};
    const listen=(element,event,handler,extra={})=>element.addEventListener(event,handler,{...options,...extra});
    const point=event=>videoPoint(video.getBoundingClientRect(),video.videoWidth,video.videoHeight,event.clientX,event.clientY);
    listen(video,'pointermove',event=>{const p=point(event);if(p)this.connection.input({t:'mouse',a:'move',...p},true);});
    listen(video,'pointerdown',event=>{
      const p=point(event);const btn=['left','middle','right'][event.button];if(!p||!btn||!connection.canControl())return;
      event.preventDefault();video.focus();
      try{video.setPointerCapture?.(event.pointerId);}catch{this.reset();return;}
      this.buttons.add(btn);connection.input({t:'mouse',a:'down',btn,...p});
    });
    listen(video,'pointerup',event=>{
      const btn=['left','middle','right'][event.button];if(!this.buttons.delete(btn))return;
      const p=point(event);if(p)connection.input({t:'mouse',a:'up',btn,...p});else this.reset();
      try{video.releasePointerCapture?.(event.pointerId);}catch{this.reset();}
    });
    listen(video,'pointercancel',()=>this.reset());listen(video,'lostpointercapture',()=>{if(this.buttons.size)this.reset();});
    listen(video,'contextmenu',event=>{if(connection.canControl())event.preventDefault();});
    listen(video,'wheel',event=>{const p=point(event);if(!p||!connection.canControl())return;event.preventDefault();const d=Math.max(-12000,Math.min(12000,-Math.round(event.deltaY*(event.deltaMode===1?40:event.deltaMode===2?800:1))));if(d)connection.input({t:'mouse',a:'wheel',d,...p});},{passive:false});
    listen(video,'keydown',event=>{
      if(!connection.canControl()||event.isComposing||this.composing)return;const vk=virtualKey(event.code);if(!vk)return;
      event.preventDefault();if(event.repeat||this.keys.has(vk))return;this.keys.add(vk);connection.input({t:'key',a:'down',vk});
    });
    listen(video,'keyup',event=>{const vk=virtualKey(event.code);if(!this.keys.delete(vk))return;event.preventDefault();connection.input({t:'key',a:'up',vk});});
    listen(video,'blur',()=>this.reset());listen(root,'blur',()=>this.reset());
    listen(doc,'visibilitychange',()=>{if(doc.hidden)this.reset();});
    listen(text,'compositionstart',()=>{this.composing=true;});
    listen(text,'compositionend',event=>{
      this.composing=false;
      if(!doc.hidden&&event.data?.length>0&&event.data.length<=4096) {
        if(connection.input({t:'key',a:'text',text:event.data})) {text.value='';queueMicrotask(()=>{text.value='';});}
        else text.value=event.data; // Preserve an uncommitted remote send for the explicit button after reset ACK.
      }
    });
    // Physical characters never accompany IME composition. The committed Unicode is sent once.
    listen(sendText,'click',()=>{if(!this.composing&&text.value.length>0&&text.value.length<=4096&&connection.input({t:'key',a:'text',text:text.value}))text.value='';});
  }
  reset() {this.keys.clear();this.buttons.clear();this.connection.resetInput();}
  clearLocalState() {this.keys.clear();this.buttons.clear();}
  dispose() {this.reset();this.abort.abort();this.text.value='';}
}
