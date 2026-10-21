const fs=require('node:fs'),path=require('node:path');
const {chromium}=require('../.artifacts/browser/node_modules/playwright-core');
const {installBrowserObserver}=require('./e2e-browser-observer.cjs');
const run=path.resolve(process.argv[2]);
const save=(n,v)=>{const file=path.join(run,n),tmp=file+'.tmp';fs.writeFileSync(tmp,JSON.stringify(v,null,2));fs.renameSync(tmp,file);};
const manifest=JSON.parse(fs.readFileSync(path.join(run,'campaign.json'),'utf8').replace(/^\uFEFF/,''));
if(manifest.version===3&&manifest.profile==='lan-view-consent-stop'&&!manifest.caseId)manifest.caseId='C01';
if(!/^C(?:0[1-9]|1[0-6])$/.test(manifest.caseId))throw Error('Explicit C01-C16 campaign required');
const viewerUrl='https://192.168.0.61:25001/'+(manifest.caseId==='C15'?'?relay=1':'');
const events=[];let previous='';let joined=false;let browser;
const start=performance.now();
(async()=>{
 try{
  browser=await chromium.launch({executablePath:manifest.local.chrome??'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',headless:false,args:['--force-renderer-accessibility','--window-size=1280,900','--window-position=100,100']});
  const system=await browser.newBrowserCDPSession();
  const processes=await system.send('SystemInfo.getProcessInfo');
  const browserProcesses=processes.processInfo.filter(p=>p.type==='browser');
  if(browserProcesses.length!==1)throw Error('Browser identity unavailable');
  save('viewer-owner.json',{runId:manifest.id,pid:browserProcesses[0].id,imagePath:manifest.local.chrome});
  const context=await browser.newContext({viewport:{width:1240,height:760}});
  await context.addInitScript(installBrowserObserver);
  const page=await context.newPage();
  page.on('pageerror',()=>events.push({ms:performance.now()-start,kind:'pageerror'}));
  page.on('websocket',ws=>{
   ws.on('framereceived',({payload})=>{try{const m=JSON.parse(payload);events.push({ms:performance.now()-start,kind:'received',type:m.type});}catch{}});
   ws.on('framesent',({payload})=>{try{const m=JSON.parse(payload);events.push({ms:performance.now()-start,kind:'sent',type:m.type});}catch{}});
  });
  await page.goto(viewerUrl,{waitUntil:'domcontentloaded',timeout:15000});
  save('viewer-ready.json',{ready:true,mode:'DesktopTestDriver GUI; Playwright observation only'});
  let firstFrames=null;
  while(performance.now()-start<300000&&!fs.existsSync(path.join(run,'finish.json'))){
   if(events.length>4096)throw Error('Observation event limit');
   const s=await page.evaluate(()=>{const v=document.querySelector('#screen');return {joinVisible:!document.querySelector('#join').hidden,status:document.querySelector('#status').textContent,statusError:document.querySelector('#status').classList.contains('error'),sessionStatus:document.querySelector('#session-status').textContent,stats:document.querySelector('#stats').textContent,frames:v.getVideoPlaybackQuality().totalVideoFrames,width:v.videoWidth,height:v.videoHeight,textDisabled:document.querySelector('#remote-text').disabled};});
   s.elapsedMs=performance.now()-start;save('viewer-observation.json',s);
   const markerPath=path.join(run,'video-marker-probe.json');
   const marker=fs.existsSync(markerPath)?JSON.parse(fs.readFileSync(markerPath,'utf8')):null;
   if(marker&&marker.id!==manifest.id)throw Error('Marker owner changed');
   const observation=await page.evaluate(marker=>globalThis.__raE2eObservation.snapshot(marker),marker);
   const metadata=await page.evaluate(()=>globalThis.__raE2eObservation.drain());
   if(observation.overflow)throw Error('Metadata observation overflow');
   const metadataFile=path.join(run,'browser-metadata.ndjson');
   if(fs.existsSync(metadataFile)&&fs.statSync(metadataFile).size>1048576)throw Error('Metadata file bound');
   for(const row of metadata)fs.appendFileSync(metadataFile,JSON.stringify({runId:manifest.id,...row})+'\n');
   save('browser-observation.json',{runId:manifest.id,...observation});
   const state=JSON.stringify({...s,elapsedMs:0,frames:s.frames?1:0});
   if(state!==previous){events.push({ms:s.elapsedMs,kind:'ui',...s});previous=state;save('viewer-events.json',events);}
   if(!s.joinVisible)joined=true;
   if(s.frames>=3&&s.width>0&&s.height>0){
    if(firstFrames===null)firstFrames=s.frames;
    else if(s.frames>firstFrames)save('video-ready.json',s);
   }
   if(joined&&s.joinVisible&&!fs.existsSync(path.join(run,'viewer-ended.json')))save('viewer-ended.json',s);
   if(s.statusError){save('viewer-join-error.json',s);break;}
   await new Promise(r=>setTimeout(r,250));
  }
 }catch(e){save('viewer-error.json',{error:e.name});process.exitCode=1;}
 finally{save('viewer-events.json',events);if(browser)await Promise.race([browser.close(),new Promise(resolve=>setTimeout(resolve,2000))]);save('viewer-closed.json',{closed:true});}
})();
