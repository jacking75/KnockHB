const fs=require('node:fs'),path=require('node:path');
const {chromium}=require('../.artifacts/browser/node_modules/playwright-core');
const run=path.resolve(process.argv[2]);
const until=performance.now()+450000;
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const save=(name,value)=>fs.writeFileSync(path.join(run,name),JSON.stringify(value,null,2));
async function wait(fn,ms,label){const end=Math.min(until,performance.now()+ms);while(performance.now()<end){if(await fn())return;await sleep(100)}throw new Error(label+' timeout')}
(async()=>{
 let browser;
 try{
  browser=await chromium.launch({executablePath:'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',headless:true});
  const context=await browser.newContext({viewport:{width:1280,height:900}});
  const page=await context.newPage();
  await page.goto('https://192.168.0.61:25001/',{waitUntil:'domcontentloaded',timeout:15000});
  save('viewer-ready.json',{ready:true});
  await wait(()=>fs.existsSync(path.join(run,'pairing.json')),150000,'pairing');
  const pairing=JSON.parse(fs.readFileSync(path.join(run,'pairing.json'),'utf8').replace(/^\uFEFF/,''));
  await page.locator('#name').fill('LAN E2E tester');
  await page.locator('#code').fill(pairing.code);await page.locator('#pin').fill(pairing.pin);
  await page.locator('#join-form button').click();
  const before=await page.locator('#screen').evaluate(v=>({frames:v.getVideoPlaybackQuality().totalVideoFrames,width:v.videoWidth,height:v.videoHeight}));
  if(before.frames!==0)throw new Error('Frames received before consent observation');
  save('consent-requested.json',{beforeConsent:before});
  await wait(async()=>await page.locator('#screen').evaluate(v=>v.videoWidth>0&&v.getVideoPlaybackQuality().totalVideoFrames>=3),135000,'human consent and video');
  const first=await page.locator('#screen').evaluate(v=>({frames:v.getVideoPlaybackQuality().totalVideoFrames,width:v.videoWidth,height:v.videoHeight}));
  await wait(async()=>await page.locator('#screen').evaluate(v=>v.getVideoPlaybackQuality().totalVideoFrames)>first.frames,10000,'video frame progression');
  if(!await page.locator('#remote-text').isDisabled())throw new Error('Screen-only session unexpectedly permits text input');
  await page.screenshot({path:path.join(run,'viewer.png')});
  save('video-ready.json',{first,screenOnly:true,status:await page.locator('#session-status').textContent()});
  await wait(()=>page.locator('#join').isVisible(),60000,'target local stop');
  save('viewer-result.json',{state:'PASS',scope:'screen-only consent, decoded frames, disabled input, target stop'});
 }catch(error){save('viewer-result.json',{state:'FAIL',error:error.message});process.exitCode=1}
 finally{if(browser)await browser.close()}
})();
