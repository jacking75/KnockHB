// 입력-표시 지연 측정. Viewer 페이지 콘솔(또는 브라우저 자동화의 JS 실행)에서 실행한다.
// 대상 PC에 scripts/e2e-motion-target.ps1 -Mode latency 창을 띄우고, Viewer 영상에서 그 창을 한 번 클릭해 포커스를 준 뒤 쓴다.
// region: 영상 좌표계(videoWidth×videoHeight)에서 대상 창의 흰/검 영역. 창 테두리·제목줄은 빼고 안쪽만 잡는다.
// 키 이벤트를 Viewer 영상 요소에 보내고, 보낸 시각부터 영역 밝기가 반전된 프레임의 expectedDisplayTime까지를 잰다.
// 결과는 window.__lat에 남는다(측정에 약 30초).
async function measureInputToDisplay(region, samples = 40) {
  const v = document.getElementById('screen');
  const c = document.createElement('canvas'); c.width = region.w; c.height = region.h;
  const g = c.getContext('2d', { willReadFrequently: true });
  const luma = () => {
    g.drawImage(v, region.x, region.y, region.w, region.h, 0, 0, region.w, region.h);
    const d = g.getImageData(0, 0, region.w, region.h).data; let t = 0, n = 0;
    for (let i = 0; i < d.length; i += 32) { t += d[i] + d[i + 1] + d[i + 2]; n++; }
    return t / n / 3;
  };
  const frame = () => new Promise(res => v.requestVideoFrameCallback((now, meta) => res(meta)));
  const press = () => {
    for (const type of ['keydown', 'keyup']) v.dispatchEvent(new KeyboardEvent(type, { code: 'KeyA', key: 'a', bubbles: true, cancelable: true }));
  };
  const out = [];
  for (let i = 0; i < samples; i++) {
    await frame(); await frame();
    const dark = luma() < 128; const t0 = performance.now(); press(); let lat = null;
    while (performance.now() - t0 < 2000) {
      const m = await frame(); const l = luma();
      if (dark ? l > 192 : l < 64) { lat = m.expectedDisplayTime - t0; break; }
    }
    out.push(lat);
    await new Promise(res => setTimeout(res, 350 + Math.random() * 250));
  }
  const ok = out.filter(x => x !== null).sort((a, b) => a - b);
  const pct = p => ok[Math.min(ok.length - 1, Math.ceil(p * ok.length) - 1)];
  window.__lat = { region, samples, detected: ok.length, missed: samples - ok.length, minMs: ok[0], p50Ms: pct(.5), p95Ms: pct(.95),
    maxMs: ok.at(-1), stats: document.getElementById('stats').textContent };
  return window.__lat;
}
