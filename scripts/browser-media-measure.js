async page => {
  await page.waitForFunction(() => document.getElementById('screen').videoWidth > 0, undefined, { timeout: 20000 });
  return await page.locator('#screen').evaluate(async video => {
    const before = video.getVideoPlaybackQuality();
    const start = performance.now();
    let presented = 0, running = true;
    const callback = () => { if (running) { presented++; video.requestVideoFrameCallback(callback); } };
    video.requestVideoFrameCallback(callback);
    await new Promise(resolve => setTimeout(resolve, 60000));
    running = false;
    const after = video.getVideoPlaybackQuality();
    const seconds = (performance.now() - start) / 1000;
    return { utc: new Date().toISOString(), width: video.videoWidth, height: video.videoHeight, seconds,
      totalFrames: after.totalVideoFrames - before.totalVideoFrames, droppedFrames: after.droppedVideoFrames - before.droppedVideoFrames,
      presented, presentedFps: presented / seconds, stats: document.getElementById('stats').textContent,
      status: document.getElementById('status').textContent };
  });
}
