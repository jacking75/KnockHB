async page => {
  await page.waitForFunction(() => document.getElementById('screen').videoWidth > 0, undefined, { timeout: 20000 });
  const video = await page.locator('#screen').evaluate(element => ({
    width: element.videoWidth, height: element.videoHeight, readyState: element.readyState,
    decoded: element.getVideoPlaybackQuality().totalVideoFrames
  }));
  return { video, status: await page.locator('#status').textContent(), stats: await page.locator('#stats').textContent() };
}
