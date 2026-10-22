const {chromium}=require('../.artifacts/browser/node_modules/playwright-core');
const fs=require('fs'),path=require('path');
const run=process.env.REMOTEASSIST_E2E_RUN_ID;
if(!/^\d{8}-\d{6}$/.test(run||''))throw Error('Fresh REMOTEASSIST_E2E_RUN_ID required');
const out=path.resolve('.artifacts/remote-pc-e2e',run);
if(!fs.existsSync(path.join(out,'prepared.json')))throw Error('Run not prepared');
(async()=>{
 const browser=await chromium.connectOverCDP('http://127.0.0.1:25191',{timeout:5000});
 try{
  const page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().startsWith('https://192.168.0.61:25001/'));
  if(!page)throw Error('Owned Viewer missing');
  const state=await page.evaluate(()=>{
   const ids=['status','session-status','input-status','stats','diagnostics-status','diagnostics-content'];
   const data=Object.fromEntries(ids.map(id=>[id,document.getElementById(id)?.textContent]));
   const v=document.getElementById('screen');
   return {...data,utc:new Date().toISOString(),sessionVisible:!document.getElementById('session').hidden,video:{width:v.videoWidth,height:v.videoHeight,time:v.currentTime,frames:v.getVideoPlaybackQuality().totalVideoFrames},adminEnabled:!document.getElementById('admin-request').disabled};
  });
  const id=Date.now();fs.writeFileSync(path.join(out,`viewer-${id}.json`),JSON.stringify(state,null,2));
  if(process.argv.includes('--screenshot'))await page.screenshot({path:path.join(out,`viewer-${id}.png`),mask:[page.locator('#code'),page.locator('#pin')],timeout:5000});
  console.log(JSON.stringify({...state,evidenceId:id}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
