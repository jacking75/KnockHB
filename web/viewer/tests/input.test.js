import test from 'node:test';
import assert from 'node:assert/strict';
import { ViewerInput, virtualKey } from '../input.js';
import { ViewerConnection } from '../connection.js';

class Target extends EventTarget {
  value='';videoWidth=1920;videoHeight=1080;
  getBoundingClientRect(){return {left:0,top:0,width:1000,height:1000};}focus(){}
  fire(type,props={}){const event=new Event(type,{cancelable:true});Object.assign(event,props);this.dispatchEvent(event);return event;}
}
test('IME sends one committed Unicode and blur resets remote held keys',async()=>{
  const sent=[],connection={canControl:()=>true,input:m=>{sent.push(m);return true;},resetInput:()=>sent.push({t:'reset'})};
  const video=new Target(),text=new Target(),button=new Target(),root=new Target(),doc=new Target();
  const input=new ViewerInput(connection,video,text,button,root,doc);
  text.fire('compositionstart');video.fire('keydown',{code:'KeyA',isComposing:true});text.fire('compositionend',{data:'한글'});await Promise.resolve();
  assert.deepEqual(sent,[{t:'key',a:'text',text:'한글'}]);
  video.fire('keydown',{code:'ControlLeft'});video.fire('keydown',{code:'ControlLeft',repeat:true});video.fire('keyup',{code:'ControlLeft'});
  assert.deepEqual(sent.slice(1),[{t:'key',a:'down',vk:162},{t:'key',a:'up',vk:162}]);
  video.fire('pointerdown',{button:0,clientX:500,clientY:10,pointerId:1});assert.equal(sent.length,3,'letterbox click injected');
  root.fire('blur');assert.equal(sent.at(-1).t,'reset');input.dispose();
  assert.equal(virtualKey('F24'),135);assert.equal(virtualKey('Unidentified'),undefined);
});
test('text button pressed while input reset is pending sends once on resume',()=>{
  let open=false;const sent=[],connection={canControl:()=>open,input:m=>{if(!open)return false;sent.push(m);return true;},resetInput(){}};
  const text=new Target(),button=new Target();const input=new ViewerInput(connection,new Target(),text,button,new Target(),new Target());
  text.value='원격 테스트';button.fire('click');assert.equal(sent.length,0);assert.equal(text.value,'원격 테스트');
  open=true;input.resumed();input.resumed();
  assert.deepEqual(sent,[{t:'key',a:'text',text:'원격 테스트'}]);assert.equal(text.value,'');input.dispose();
});
test('latest pointer coalescing, reliable final coordinate and epoch reset survive reorder',()=>{
  const viewer=new ViewerConnection({base:'http://localhost:25001',createSocket:()=>({readyState:0,close(){}}),setTimer:()=>1,clearTimer(){}});
  viewer.sessionId='sid';viewer.permissionGeneration=1;viewer.permissions=['screen','control'];viewer.dataReady=true;viewer.monitor={identity:'DISPLAY',topologyGeneration:1};
  const reliable=[],pointer=[];viewer.channels.set('input',{readyState:'open',bufferedAmount:0,send:m=>reliable.push(JSON.parse(m)),close(){}});
  const channel={readyState:'open',bufferedAmount:2048,send:m=>pointer.push(JSON.parse(m)),close(){}};viewer.channels.set('pointer',channel);
  viewer.input({t:'mouse',a:'move',x:.1,y:.1},true);viewer.input({t:'mouse',a:'move',x:.9,y:.9},true);viewer.flushPointer();assert.equal(pointer.length,0);
  viewer.input({t:'mouse',a:'down',btn:'left',x:.5,y:.6});assert.equal(reliable[0].x,.5);
  assert.equal(reliable[0].pointerBarrier,0); // Backpressured old pointer was discarded at button transition.
  channel.bufferedAmount=0;viewer.input({t:'mouse',a:'move',x:.9,y:.9},true);viewer.flushPointer();assert.equal(pointer[0].x,.9);assert.equal(pointer[0].seq,1);
  viewer.input({t:'mouse',a:'move',x:.2,y:.2},true);viewer.resetInput();viewer.flushPointer();assert.equal(pointer.length,1);
  assert.equal(reliable[1].inputEpoch,2);assert.equal(reliable[1].seq,2);viewer.end();
});

test('pointer capture failure sends no button down and resets held state',()=>{
  const sent=[],connection={canControl:()=>true,input:m=>{sent.push(m);return true;},resetInput:()=>sent.push({t:'reset'})};
  const video=new Target();video.setPointerCapture=()=>{throw Error('lost pointer');};
  const input=new ViewerInput(connection,video,new Target(),new Target(),new Target(),new Target());
  video.fire('pointerdown',{button:0,clientX:500,clientY:500,pointerId:1});
  assert.deepEqual(sent,[{t:'reset'}]);assert.equal(input.buttons.size,0);input.dispose();
});
