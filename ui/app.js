/* ==========================================================================
   DSH 控制面板 v1.0 前端

   协议与旧版一致（见 docs/进度.md 第八节）。本节负责：
     - 渲染 C# 推来的 state / settings / paths / plugins / browsers / theme / toast / log
     - 把用户操作转成 {t:'cmd', name, ...} 发回去

   三条刻意保留的旧版经验：
     1. 点击启动后立刻「乐观置忙」，不等外壳回推 —— 否则一两秒内像没点到。
     2. 回填设置时若输入框正在编辑，不覆盖它（否则 1.5 秒的 state 推送会把输入冲掉）。
     3. 默认浏览器用自绘下拉，不用原生 <select> —— 原生菜单是顶层窗口，
        既不受卡片裁剪也没法套主题，还会跑到面板外面去。
   ========================================================================== */

const $ = (id) => document.getElementById(id);
const bridge = window.chrome?.webview ?? null;

let lastState = null;
let editingPort = false;

// 「重启」的二次确认做在网页里：点 ⟳ 先展开成「确认重启」，再点才真正重启，不弹 MessageBox。
let armedRestart = false;
let armedAt = 0;

// 信息卡横跨四份推送（state / paths / plugins / browsers），各自到达时就地重绘
const cache = { state: null, paths: null, plugins: null, browsers: null, settings: null };

function post(obj) {
  if (bridge) bridge.postMessage(obj);
  else console.log('[no-bridge]', obj);
}

function cmd(name, extra) {
  post(Object.assign({ t: 'cmd', name }, extra || {}));
}

function toast(msg) {
  const el = $('toast');
  if (!el) return;
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(el._t);
  el._t = setTimeout(() => el.classList.remove('show'), 2600);
}

const esc = (s) => String(s == null ? '' : s)
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

const baseName = (p) => {
  const raw = String(p || '').replace(/[\\/]+$/, '');
  const cut = Math.max(raw.lastIndexOf('\\'), raw.lastIndexOf('/'));
  const name = cut >= 0 ? raw.slice(cut + 1) : raw;
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(0, dot) : name;
};

// 运行时的 url 带令牌（?token=...），信息卡里只留 origin
const originOf = (url) => {
  const s = String(url || '');
  if (!s) return '';
  const q = s.indexOf('?');
  return q > 0 ? s.slice(0, q) : s;
};

/* ---------------- 标题栏 ---------------- */

$('titlebar').addEventListener('mousedown', (e) => {
  if (e.button !== 0) return;
  if (e.target.closest('button')) return;
  cmd('drag');
});

$('btnMin').addEventListener('click', () => cmd('minimize'));
$('btnClose').addEventListener('click', () => cmd('close'));
$('btnTheme').addEventListener('click', () => cmd('toggleTheme'));

/* ---------------- 导航 ---------------- */

function switchPage(page) {
  document.querySelectorAll('.nav').forEach((b) => {
    b.classList.toggle('active', b.dataset.page === page);
  });
  document.querySelectorAll('.page').forEach((p) => {
    p.classList.toggle('active', p.id === 'page-' + page);
  });
  // 每次切页从顶开始。设置页自己滚动，留着上次的位置会让人以为页面上半截没了。
  const active = document.getElementById('page-' + page);
  if (active) active.scrollTop = 0;
  post({ t: 'nav', page });
}

document.querySelectorAll('.nav').forEach((b) => {
  b.addEventListener('click', () => switchPage(b.dataset.page));
});

// 支持 URL hash 指定初始页（调试/验证用）：index.html#settings
// 外壳可通过环境变量 DSH_PANEL_PAGE 拼出带 hash 的地址，方便自动截图验证各页面。
(function applyHashPage() {
  const raw = (location.hash || '').replace('#', '').trim();
  if (raw && document.getElementById('page-' + raw)) { switchPage(raw); }
})();

$('btnCollapse').addEventListener('click', () => {
  document.body.classList.toggle('collapsed');
});

/* ---------------- 服务页：状态 ---------------- */

// 文案切换时轻轻缩一下，让「按钮真的换了个意思」看得出来。offsetWidth 强制重排以重启动画。
function replaySwap(el) {
  el.classList.remove('swap');
  void el.offsetWidth;
  el.classList.add('swap');
}

// 主按钮换文案时带上过渡；文字没变就只更新样式，避免每 1.5 秒重放一次动画
function setMainButton(btn, text, cls) {
  const t = btn.querySelector('.btxt');
  const changed = !!t && t.textContent !== text;
  if (t) t.textContent = text;
  btn.className = cls;
  if (changed) replaySwap(btn);
}

// 切主题：先给 body 挂 .theming（CSS 里统一所有元素的过渡时长），220ms 后摘掉。
// 不做去重的话，每 1.5 秒一次的 state 推送会反复重开这个窗口，
// 把用户正在做的 hover / 开关滑动动画也一起压成 .16s。
let curDark = null;
let themeTimer = 0;

function applyTheme(dark) {
  const d = !!dark;
  if (curDark === d) return;

  const first = curDark === null;   // 首帧：直接落位，别让启动瞬间从默认深色渐变到浅色
  curDark = d;

  if (first) {
    document.body.classList.toggle('light', !d);
    return;
  }

  document.body.classList.add('theming');
  document.body.classList.toggle('light', !d);
  clearTimeout(themeTimer);
  themeTimer = setTimeout(() => document.body.classList.remove('theming'), 240);
}

function renderState(s) {
  lastState = s;
  cache.state = s;

  const running = !!s.running;
  const starting = !!s.starting;
  const restarting = !!s.restarting;

  $('dot').className = 'dot' + (running ? ' on' : starting ? ' busy' : '');

  const stateText = $('stateText');
  const badge = $('stateBadge');
  const urlText = $('urlText');
  const hint = $('heroHint');
  const port = s.port || 3080;

  if (starting) {
    stateText.textContent = restarting ? '正在重启…' : '启动中…';
    badge.textContent = '启动中';
    badge.className = 'badge busy';
    urlText.textContent = '正在拉起服务，请稍候（首次可能要几十秒）';
    hint.textContent = '启动中会一直写服务日志，日志页可实时查看';
  } else if (running) {
    stateText.textContent = s.clean ? '运行中（纯净）' : '运行中';
    badge.textContent = s.clean ? '纯净运行' : '运行中';
    badge.className = 'badge ok';
    urlText.textContent = s.url || ('http://127.0.0.1:' + port);
    hint.textContent = '主按钮现在是「停止 DSH」；停止服务不会关闭面板，网页用旁边的地球按钮打开';
  } else if (s.failMsg) {
    stateText.textContent = '启动失败';
    badge.textContent = '启动失败';
    badge.className = 'badge busy';
    urlText.textContent = s.failMsg;
    hint.textContent = '失败原因已写进服务日志，日志页可查；详情看面板日志';
  } else {
    stateText.textContent = '已停止';
    badge.textContent = '已停止';
    badge.className = 'badge';
    urlText.textContent = '未运行 · 端口 ' + port;
    hint.textContent = '服务跑起来后主按钮会变成「停止 DSH」；停止服务不会关闭面板';
  }

  $('portLabel').textContent = '端口 ' + port;

  const btnMain = $('btnMain');
  const btnRestart = $('btnRestart');
  const btnConfirm = $('btnRestartConfirm');
  btnMain.disabled = false;
  btnConfirm.textContent = '确认重启';
  btnConfirm.disabled = false;

  // 「确认重启」是网页内的二次确认（不弹 MessageBox）。离开运行态或超时 6 秒自动收起。
  if (armedRestart && (!running || Date.now() - armedAt > 6000)) { armedRestart = false; }

  btnRestart.hidden = true;
  btnRestart.classList.remove('collapse');
  btnConfirm.classList.remove('on');

  if (starting) {
    setMainButton(btnMain, restarting ? '正在重启…' : '启动中…', 'btn primary big busy');
    btnMain.disabled = true;
  } else if (running) {
    // 重启图标不用 display:none 收起 —— 那样主按钮会先瞬间撑宽 56px 再开始动画。
    // 用 .collapse 把它的宽度收到 0，三个按钮同步做过渡。
    btnRestart.hidden = false;
    btnRestart.classList.toggle('collapse', armedRestart);
    btnConfirm.classList.toggle('on', armedRestart);
    // 确认态：主按钮收成 46×46 的红色方块（只留停止图标），把中间的位置让给「确认重启」。
    // 宽度的变化靠 .btn.big / .btn.compact / .confirm-restart 的 flex 过渡完成。
    setMainButton(btnMain,
      '停止 DSH',
      armedRestart ? 'btn danger big compact' : 'btn danger big');
  } else if (s.failMsg) {
    setMainButton(btnMain, '✗ 启动失败，重试', 'btn primary big');
  } else {
    setMainButton(btnMain, '启动并打开 DSH', 'btn primary big');
  }

  $('btnOpenWeb').hidden = !running;

  applyTheme(s.dark);
  renderInfo();
}

$('btnMain').addEventListener('click', () => {
  if (!lastState) return;

  const btn = $('btnMain');
  if (armedRestart) { armedRestart = false; }   // 点主按钮就放弃这次重启确认

  // 运行中：主按钮是「停止 DSH」。具体确认由外壳的 MessageBox 负责，
  // 所以这里只给一个乐观的忙碌态；2.5 秒还没变化（用户在确认框点了「否」）就回滚。
  if (lastState.running) {
    setMainButton(btn, '正在停止…', 'btn danger big busy');
    btn.disabled = true;
    cmd('stop');
    setTimeout(() => {
      if (lastState && lastState.running && !lastState.starting) { renderState(lastState); }
    }, 2500);
    return;
  }

  // 乐观置忙：不等回推就换文案，否则点击瞬间像没反应
  setMainButton(btn, '启动中…', 'btn primary big busy');
  btn.disabled = true;
  $('dot').className = 'dot busy';
  $('stateText').textContent = '启动中…';
  $('urlText').textContent = '正在拉起服务，请稍候…';

  cmd('start');
});

// 点一下 ⟳ 不直接重启：先把它自己展开成「确认重启」，再点才真重启。
// 这样确认框长在按钮位置上（不弹 MessageBox，也不会被弹窗挡住页面）。
$('btnRestart').addEventListener('click', () => {
  armedRestart = true;
  armedAt = Date.now();
  if (lastState) renderState(lastState);
});

$('btnRestartConfirm').addEventListener('click', () => {
  armedRestart = false;
  const b = $('btnRestartConfirm');
  b.disabled = true;
  b.textContent = '正在重启…';
  toast('正在重启服务…');
  cmd('restartNow');
});

$('btnOpenWeb').addEventListener('click', () => cmd('openWeb'));

/* ---------------- 服务页：插件列表 ---------------- */

// 本地 link: 包的 version 是一整条 file: 路径（例：link:C:/…/dsh-approval-timeout），
// 直接塞进版本位会把整行撑成三行、把开关顶歪。缩成「本地链接」，完整值放 title。
const verLabel = (v) => {
  const s = String(v || '');
  return (/^link:/i.test(s) || s.length > 24) ? '本地链接' : s;
};

function renderPlugins(m) {
  cache.plugins = m;
  const list = $('pluginList');
  const hint = $('pluginHint');
  if (!list) return;

  if (!m.ok) {
    hint.textContent = m.err || '无法读取插件清单';
    hint.className = 'sum warn';
    list.innerHTML = '';
    renderInfo();
    return;
  }

  hint.textContent = `共 ${m.total} 个，已启用 ${m.on} 个。改动在下次启动 DSH 时生效。`;
  hint.className = 'sum';

  list.innerHTML = (m.list || []).map((p) => `
    <div class="prow${p.lock ? ' locked' : ''}">
      <div class="pnm">
        <div class="pn">${esc(p.d)}${p.lock ? '<span class="tag">命脉</span>' : ''}</div>
        <div class="pmeta"><span class="pk">${esc(p.n)}</span></div>
      </div>
      <span class="pv" title="${esc(p.v)}">${esc(verLabel(p.v))}</span>
      <label class="sw"><input type="checkbox"${p.on ? ' checked' : ''}${p.lock ? ' disabled' : ''}><i></i></label>
    </div>`).join('');

  // 事件绑定走委托，37 个插件的 37 次 addEventListener 没必要
  Array.from(list.querySelectorAll('.prow')).forEach((row, i) => {
    const item = (m.list || [])[i];
    const input = row.querySelector('input');
    if (!input || !item || item.lock) return;
    input.addEventListener('change', (e) => {
      cmd('togglePlugin', { plugin: item.n, on: e.target.checked });
      toast((e.target.checked ? '已启用 ' : '已关闭 ') + item.d);
    });
  });

  renderInfo();
}

$('btnPluginAll').addEventListener('click', () => {
  cmd('toggleAllPlugins', { on: true });
  toast('正在启用全部插件…');
});
$('btnPluginNone').addEventListener('click', () => {
  cmd('toggleAllPlugins', { on: false });
  toast('正在关闭全部插件…');
});

/* ---------------- 服务页：信息卡（多来源汇总） ---------------- */

function renderInfo() {
  const st = cache.state || {};
  const p = cache.paths || {};
  const pl = cache.plugins || {};
  const br = cache.browsers || {};
  const cfg = cache.settings || {};
  const port = st.port || cfg.port || 3080;

  const addr = st.running ? (originOf(st.url) || ('http://127.0.0.1:' + port)) : '—';
  $('kAddr').textContent = addr;

  $('kPort').textContent = String(port);
  $('kMode').textContent = (st.mode || cfg.mode) === 'tab' ? '普通窗口' : '无地址栏独立窗口';

  // 浏览器：优先用动作里配置的路径；没配就显示自动探测到的那只
  const browserText = cfg.browser
    ? baseName(cfg.browser)
    : (br.current ? '自动 · ' + br.current : '自动');
  $('kBrowser').textContent = browserText;

  $('kPlugins').textContent = pl.ok ? `${pl.on} / ${pl.total}` : '—';
  $('kPid').textContent = st.pid ? String(st.pid) : '—';

  const setPath = (id, val) => {
    const el = $(id);
    const text = val || '—';
    el.textContent = text;
    el.title = text;          // 路径一律省略号截断，完整值放 title
  };
  setPath('kSvcCmd', p.svccmd);
  setPath('kWorkDir', p.workdir);
  setPath('kSvcLog', p.svclog);
  setPath('kStateLog', p.statelog);
  setPath('kCfg', p.cfgfile);
}

/* ---------------- 设置页 ---------------- */

const bindSwitch = (id, key) => {
  $(id).addEventListener('change', (e) => {
    cmd('setSetting', { key, value: e.target.checked });
  });
};

bindSwitch('swAutoStart', 'autoStart');
bindSwitch('swAutoOpen', 'openOnStart');
bindSwitch('swAutoTray', 'autoTray');
bindSwitch('swCloseToTray', 'closeToTray');
bindSwitch('swStopWithPanel', 'stopOnClose');
bindSwitch('swBalloon', 'notifyCrash');
bindSwitch('swDark', 'dark');
bindSwitch('swDev', 'dev');

function setSegMode(mode) {
  const want = mode === 'tab' ? 'tab' : 'app';
  document.querySelectorAll('#segMode button').forEach((b) => {
    b.classList.toggle('active', b.dataset.mode === want);
  });
}

document.querySelectorAll('#segMode button').forEach((b) => {
  b.addEventListener('click', () => {
    if (b.classList.contains('active')) return;
    setSegMode(b.dataset.mode);
    cmd('setSetting', { key: 'mode', value: b.dataset.mode });
  });
});

function renderSettings(s) {
  cache.settings = s;

  $('swAutoStart').checked = !!s.autoStart;
  $('swAutoOpen').checked = !!s.openOnStart;
  $('swAutoTray').checked = !!s.autoTray;
  $('swCloseToTray').checked = !!s.closeToTray;
  $('swStopWithPanel').checked = !!s.stopOnClose;
  $('swBalloon').checked = !!s.notifyCrash;
  $('swDev').checked = !!s.dev;
  $('swDark').checked = !!s.dark;
  setSegMode(s.mode);

  // 正在编辑端口时不覆盖，否则 1.5 秒一次的推送会把输入冲掉
  if (!editingPort && document.activeElement !== $('inpPort')) {
    $('inpPort').value = s.port;
  }

  renderInfo();
}

/* ---------------- 设置页：默认浏览器自绘下拉 ---------------- */

function closeBrowserMenu() {
  const menu = $('browserMenu');
  if (menu) menu.hidden = true;
}

function renderBrowsers(m) {
  cache.browsers = m;
  const currentPath = m.currentPath || '';
  const currentName = m.current || '';

  $('browserLabel').textContent = currentPath
    ? baseName(currentPath)
    : (currentName ? '自动 · ' + currentName : '自动（未探测到）');

  const menu = $('browserMenu');
  const rows = [];

  rows.push(`<button class="mi${currentPath ? '' : ' on'} dim" data-path="">
    ${currentName ? '自动 · ' + esc(currentName) : '自动（未探测到浏览器）'}</button>`);

  (m.list || []).forEach((b) => {
    const on = currentPath && b.path === currentPath;
    rows.push(`<button class="mi${on ? ' on' : ''}" data-path="${esc(b.path)}">${esc(b.name)}</button>`);
  });

  if ((m.list || []).length) rows.push('<div class="sep"></div>');
  rows.push('<button class="mi dim" data-pick="file">选择浏览器文件…</button>');

  menu.innerHTML = rows.join('');

  menu.querySelectorAll('.mi').forEach((mi) => {
    mi.addEventListener('click', () => {
      closeBrowserMenu();
      if (mi.dataset.pick === 'file') { cmd('pickBrowserFile'); return; }
      cmd('setBrowser', { value: mi.dataset.path });
    });
  });

  renderInfo();
}

$('btnPickBrowser').addEventListener('click', (e) => {
  e.stopPropagation();
  const menu = $('browserMenu');
  if (!menu.hidden) { menu.hidden = true; return; }
  // 菜单贴着卡片下沿时会被视口裁掉，空间不够就改成向上弹
  const rect = e.currentTarget.getBoundingClientRect();
  const est = menu.querySelectorAll('.mi').length * 36 + 14;
  menu.classList.toggle('up', window.innerHeight - rect.bottom < est + 16);
  menu.hidden = false;
});

document.addEventListener('click', (e) => {
  if (!e.target.closest('#pickBrowser')) closeBrowserMenu();
  // 点按钮区以外的地方 = 放弃这次「确认重启」
  if (armedRestart && !e.target.closest('.btns')) {
    armedRestart = false;
    if (lastState) renderState(lastState);
  }
});
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  closeBrowserMenu();
  if (armedRestart) {
    armedRestart = false;
    if (lastState) renderState(lastState);
  }
});

$('btnClean').addEventListener('click', () => {
  const ok = confirm('启动纯净 DSH？\n\n会用官方默认插件集另起一个实例，用于排查插件问题。\n如果服务正在运行，需要先停止它。');
  if (ok) cmd('clean');
});

$('inpPort').addEventListener('focus', () => { editingPort = true; });
$('inpPort').addEventListener('blur', () => { editingPort = false; });
$('btnPortSave').addEventListener('click', () => {
  cmd('setPort', { value: $('inpPort').value });
  editingPort = false;
  toast('端口已保存，下次启动 DSH 时生效');
});

$('btnPickDsh').addEventListener('click', () => cmd('pickDshHome'));
$('btnProbeDsh').addEventListener('click', () => cmd('probeDshHome'));

/* ---------------- 设置页：DSH 目录 ---------------- */

function renderPaths(p) {
  cache.paths = p;

  const pathEl = $('dshPath');
  pathEl.textContent = p.dshhome || '（未找到）';
  pathEl.title = p.dshhome || '';
  pathEl.className = p.cfgok ? 'path ok' : 'path';

  const st = $('dshState');
  if (p.cfgok) {
    st.textContent = '已找到 DSH 数据目录，插件清单可读写。';
    st.className = 'st ok';
  } else {
    st.textContent = '没有找到 DSH 数据目录（需要包含 profiles 子目录）。点「选择…」手动指定。';
    st.className = 'st';
  }

  renderInfo();
}

/* ---------------- 日志 ---------------- */

function appendLog(text) {
  const box = $('logBox');
  if (!box) return;

  const atBottom = box.scrollTop + box.clientHeight >= box.scrollHeight - 24;
  box.textContent += text;
  if (box.textContent.length > 200000) {
    box.textContent = box.textContent.slice(-120000);
  }
  if (atBottom) box.scrollTop = box.scrollHeight;
}

$('btnClearLog').addEventListener('click', () => {
  $('logBox').textContent = '';
  cmd('clearLog');
});

/* ---------------- 关于 ---------------- */

$('btnRepo').addEventListener('click', () => cmd('openRepo'));
$('btnDevTools').addEventListener('click', () => cmd('devtools'));

/* ---------------- C# → 页面 ---------------- */

function handleMessage(m) {
  if (!m || !m.t) return;

  switch (m.t) {
    case 'state':    renderState(m); break;
    case 'settings': renderSettings(m); break;
    case 'paths':    renderPaths(m); break;
    case 'plugins':  renderPlugins(m); break;
    case 'browsers': renderBrowsers(m); break;
    case 'theme':
      applyTheme(m.dark);
      if (cache.settings) cache.settings.dark = !!m.dark;
      $('swDark').checked = !!m.dark;
      break;
    case 'toast':    toast(m.msg || ''); break;
    case 'log':      appendLog(m.text || ''); break;
    case 'nav':      switchPage(m.page || 'service'); break;
    default:
      console.log('[外壳]', m);
      break;
  }
}

if (bridge) {
  bridge.addEventListener('message', (e) => handleMessage(e.data));
}

/* ---------------- 握手 ---------------- */

window.addEventListener('resize', () => {
  post({ t: 'resize', w: window.innerWidth, h: window.innerHeight, dpr: window.devicePixelRatio });
});

// ready 必须在挂完所有监听之后发，否则外壳会抢在页面准备好之前推全量。
// 附带一份布局诊断：侧边栏/内容区/卡片的实际 CSS 宽度，用于排查「控件被裁」这类问题。
post({
  t: 'ready',
  w: window.innerWidth,
  h: window.innerHeight,
  dpr: window.devicePixelRatio,
  zoom: 1,
  diag: (() => {
    const rect = (sel) => {
      const el = document.querySelector(sel);
      return el ? Math.round(el.getBoundingClientRect().width) : -1;
    };
    return {
      sidebar: rect('.sidebar'),
      content: rect('.content'),
      card: rect('.card'),
      navMin: getComputedStyle(document.documentElement).getPropertyValue('--nav-w').trim(),
    };
  })(),
});
