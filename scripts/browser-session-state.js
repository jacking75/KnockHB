async page => ({
  width: await page.locator('#screen').evaluate(v => v.videoWidth),
  height: await page.locator('#screen').evaluate(v => v.videoHeight),
  status: await page.locator('#status').textContent()
})
