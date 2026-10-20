const fs=require('node:fs'),path=require('node:path');
const {chromium}=require('../.artifacts/browser/node_modules/playwright-core');
const run=path.resolve(process.argv[2]);
const save=(n,v)=>fs.writeFileSync(path.join(run,n),JSON.stringify(v,null,2));
const events=[];let previous='';let joined=false;let browser;
const start=performance.now();
(async()=>{
 try{
  browser=await chromium.launch({executablePath:'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',headless:false,args:['--force-renderer-accessibility','--window-size=1280,900','--window-position=100,100']});
  const context=await browser.newContext({viewport:{width:1240,height:760}});
  const page=await context.newPage();
  page.on('pageerror',e=>events.push({ms:performance.now()-start,kind:'pageerror',error:e.message}));
  page.on('websocket',ws=>{
   ws.on('framereceived',({payload})=>{try{const m=JSON.parse(payload);events.push({ms:performance.now()-start,kind:'received',type:m.type,code:m.code??null});}catch{}});
   ws.on('framesent',({payload})=>{try{const m=JSON.parse(payload);events.push({ms:performance.now()-start,kind:'sent',type:m.type});}catch{}});
  });
  await page.goto('https://192.168.0.61:25001/',{waitUntil:'domcontentloaded',timeout:15000});
  save('viewer-ready.json',{ready:true,mode:'DesktopTestDriver GUI; Playwright observation only'});
  let firstFrames=null;
  while(performance.now()-start<300000&&!fs.existsSync(path.join(run,'finish.json'))){
   if(events.length>4096)throw Error('Observation event limit');
   const s=await page.evaluate(()=>{const v=document.querySelector('#screen');return {joinVisible:!document.querySelector('#join').hidden,status:document.querySelector('#status').textContent,statusError:document.querySelector('#status').classList.contains('error'),sessionStatus:document.querySelector('#session-status').textContent,stats:document.querySelector('#stats').textContent,frames:v.getVideoPlaybackQuality().totalVideoFrames,width:v.videoWidth,height:v.videoHeight,textDisabled:document.querySelector('#remote-text').disabled};});
   s.elapsedMs=performance.now()-start;save('viewer-observation.json',s);
   const state=JSON.stringify({...s,elapsedMs:0,frames:s.frames?1:0});
   if(state!==previous){events.push({ms:s.elapsedMs,kind:'ui',...s});previous=state;save('viewer-events.json',events);}
   if(!s.joinVisible)joined=true;
   if(s.frames>=3&&s.width>0&&s.height>0){
    if(firstFrames===null)firstFrames=s.frames;
    else if(s.frames>firstFrames)save('video-ready.json',s);
   }
   if(joined&&s.joinVisible){save('viewer-ended.json',s);break;}
   if(s.statusError){save('viewer-join-error.json',s);break;}
   await new Promise(r=>setTimeout(r,250));
  }
 }catch(e){save('viewer-error.json',{error:e.message});process.exitCode=1;}
 finally{save('viewer-events.json',events);if(browser)await Promise.race([browser.close(),new Promise(resolve=>setTimeout(resolve,2000))]);save('viewer-closed.json',{closed:true});}
})();
