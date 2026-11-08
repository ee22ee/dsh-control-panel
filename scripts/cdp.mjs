// 通过 Chrome DevTools Protocol 操作面板的 WebView2。
//
// 前置：面板以 DSH_PANEL_DEBUG=1 启动（会开 --remote-debugging-port=9223）。
//
// 用法：
//   node scripts/cdp.mjs eval "<js 表达式>"
//   node scripts/cdp.mjs click "<css 选择器>" [索引]
//   node scripts/cdp.mjs dom                 列出主要元素矩形
//
// 只依赖 node 内置的 fetch 与 WebSocket（node 22+）。

const PORT = process.env.CDP_PORT || '9223';

async function findPageTarget() {
  const res = await fetch(`http://127.0.0.1:${PORT}/json`);
  if (!res.ok) throw new Error('HTTP ' + res.status);
  const list = await res.json();
  const page = list.find((t) => t.type === 'page');
  if (!page) throw new Error('没有找到 page target：' + JSON.stringify(list.map(t => t.type)));
  return page;
}

async function withSession(fn) {
  const target = await findPageTarget();
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  let id = 0;
  const pending = new Map();

  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const msgId = ++id;
    pending.set(msgId, { resolve, reject });
    ws.send(JSON.stringify({ id: msgId, method, params }));
  });

  await new Promise((resolve, reject) => {
    ws.addEventListener('open', resolve, { once: true });
    ws.addEventListener('error', (e) => reject(new Error('WebSocket 错误：' + e.message)), { once: true });
  });

  ws.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
    }
  });

  try {
    return await fn(send);
  } finally {
    ws.close();
  }
}

const evalJs = async (expression) => {
  const result = await withSession((send) =>
    send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }));
  return result?.result?.value;
};

const [, , action, ...rest] = process.argv;

try {
  if (action === 'eval') {
    const out = await evalJs(rest.join(' '));
    console.log(typeof out === 'string' ? out : JSON.stringify(out, null, 2));
  } else if (action === 'click') {
    const selector = rest[0];
    const index = Number(rest[1] || 0);
    const ok = await evalJs(`(function(){
      const els = document.querySelectorAll(${JSON.stringify(selector)});
      const el = els[${index}];
      if (!el) return 'NOT_FOUND:' + els.length;
      el.click();
      return 'CLICKED:' + els.length;
    })()`);
    console.log(ok);
  } else if (action === 'dom') {
    const out = await evalJs(`(function(){
      const pick = (sel) => Array.from(document.querySelectorAll(sel)).slice(0,6).map(el => {
        const r = el.getBoundingClientRect();
        const cs = getComputedStyle(el);
        return { sel, tag: el.tagName, id: el.id || null, cls: el.className || null,
                 x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height),
                 display: cs.display, visibility: cs.visibility, opacity: cs.opacity };
      });
      return {
        viewport: { w: innerWidth, h: innerHeight, dpr: devicePixelRatio },
        items: [
          ...pick('.sidebar'), ...pick('.content'),
          ...pick('#page-settings .card'), ...pick('#page-settings .row'),
          ...pick('#page-settings .row-ctl'), ...pick('#page-settings .sw'),
          ...pick('#page-settings .num'), ...pick('#page-settings .sel'),
        ],
      };
    })()`);
    console.log(JSON.stringify(out, null, 2));
  } else if (action === 'shot') {
    // 直接从 WebView2 抓页面像素，绕开屏幕截图受窗口位置/其它窗口遮挡的干扰
    const fs = await import('node:fs');
    const outPath = rest[0] || 'shots/cdp.png';
    const res = await withSession((send) => send('Page.captureScreenshot', { format: 'png' }));
    fs.mkdirSync((await import('node:path')).dirname(outPath), { recursive: true });
    fs.writeFileSync(outPath, Buffer.from(res.data, 'base64'));
    console.log('saved', outPath);
  } else {
    console.log('用法: node cdp.mjs eval "<js>" | click "<selector>" [i] | dom | shot [路径]');
    process.exit(2);
  }
} catch (e) {
  console.error('ERR:', e.message);
  process.exit(1);
}
