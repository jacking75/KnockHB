async page => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('http://127.0.0.1:25001/');
  await page.locator('#name').fill('브라우저 회귀 검사');
  await page.locator('#code').fill('000000000');
  await page.locator('#pin').fill('0000');
  await page.locator('#join-form button').click();
  await page.waitForFunction(() => document.getElementById('status').textContent === '없는 세션 코드다.', undefined, { timeout: 10000 });
  await page.waitForFunction(() => document.getElementById('pin').value === '');
  const response = await page.request.post('http://127.0.0.1:25001/session');
  if (!response.ok()) throw new Error('로컬 테스트 세션 발급 실패다.');
  const credentials = await response.json();
  const wrongPin = credentials.pin === '0000' ? '0001' : '0000';
  await page.locator('#code').fill(credentials.code);
  await page.locator('#pin').fill(wrongPin);
  await page.locator('#join-form button').click();
  await page.waitForFunction(() => document.getElementById('status').textContent === 'PIN이 올바르지 않다.', undefined, { timeout: 10000 });
  // Close the synthetic room through its authenticated Host slot; no consent or media is involved.
  await page.evaluate(async credentials => {
    await new Promise((resolve, reject) => {
      const socket = new WebSocket(location.origin.replace(/^http/, 'ws') + '/ws');
      const deadline = setTimeout(() => { socket.close(); reject(new Error('cleanup timeout')); }, 5000);
      socket.onopen = () => socket.send(JSON.stringify({ v: 1, type: 'join', role: 'host', room: credentials.code, token: credentials.hostToken }));
      socket.onmessage = event => {
        const message = JSON.parse(event.data);
        if (message.type === 'joined') { socket.send(JSON.stringify({ v: 1, type: 'bye' })); socket.close(); }
      };
      socket.onclose = () => { clearTimeout(deadline); resolve(); };
      socket.onerror = () => { clearTimeout(deadline); reject(new Error('cleanup connection')); };
    });
  }, credentials);
  const status = await page.locator('#status').textContent();
  const hidden = await page.locator('#session').evaluate(element => element.hidden);
  if (!hidden || errors.length) throw new Error(JSON.stringify({ hidden, errors }));
  return { status, hidden, errors };
}
