// Browser UI events only. Never call product internals or send data channels directly.
const {chromium}=require('../.artifacts/browser/node_modules/playwright-core');
(async()=>{
 const browser=await chromium.connectOverCDP('http://127.0.0.1:25191',{timeout:5000});
 try{
  const page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().startsWith('https://192.168.0.61:25001/'));
  page.setDefaultTimeout(4000);
  const [op,value,extra]=process.argv.slice(2);
  if(op==='click')await page.locator('#'+value).click();
  else if(op==='key')await page.locator('#screen').press(value);
  else if(op==='text'){await page.locator('#remote-text').fill(value);await page.locator('#send-text').click();}
  else if(op==='point'){
   const point=JSON.parse(value),box=await page.locator('#screen').boundingBox();
   const size=await page.locator('#screen').evaluate(v=>({w:v.videoWidth,h:v.videoHeight}));
   const scale=Math.min(box.width/size.w,box.height/size.h);
   const x=box.x+(box.width-size.w*scale)/2+point.x*scale,y=box.y+(box.height-size.h*scale)/2+point.y*scale;
   if(point.x<0||point.y<0||point.x>=size.w||point.y>=size.h)throw Error('Point outside observed frame');
   if(extra){const end=JSON.parse(extra);await page.mouse.move(x,y);await page.mouse.down();try{await page.mouse.move(x+(end.x-point.x)*scale,y+(end.y-point.y)*scale,{steps:10});}finally{await page.mouse.up();}}
   else await page.mouse.click(x,y);
  }else throw Error('Unknown UI action');
  console.log(JSON.stringify({operation:op,completed:true,utc:new Date().toISOString()}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
