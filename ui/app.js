/* ==========================================================================
   DeepSeek Harness 控制台 —— 页面脚本
   C# 侧通过 PostWebMessageAsJson 推 JSON；这里用 chrome.webview.postMessage 回发。
   约定：每条消息都有 t 字段。
   ========================================================================== */

(function () {
  'use strict';

  var wv = window.chrome && window.chrome.webview ? window.chrome.webview : null;

  function post(obj) {
    if (wv) { wv.postMessage(obj); }
    else { console.log('no webview bridge', obj); }
  }

  // ── 主题 ────────────────────────────────────────────────
  function applyTheme(dark) {
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  }

  // ── toast ───────────────────────────────────────────────
  var toastEl = document.getElementById('toast');
  var toastTimer = 0;
  function toast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.add('on');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove('on'); }, 2600);
  }

  // ── 页面切换 ─────────────────────────────────────────────
  var navItems = Array.prototype.slice.call(document.querySelectorAll('.nav-item[data-page]'));
  var pages = {
    service:  document.getElementById('page-service'),
    settings: document.getElementById('page-settings'),
    log:      document.getElementById('page-log'),
    about:    document.getElementById('page-about')
  };

  function showPage(name) {
    if (!pages[name]) { return; }
    Object.keys(pages).forEach(function (k) {
      pages[k].classList.toggle('active', k === name);
    });
    navItems.forEach(function (b) {
      b.classList.toggle('active', b.dataset.page === name);
    });
  }

  navItems.forEach(function (btn) {
    btn.addEventListener('click', function () {
      showPage(btn.dataset.page);
      post({ t: 'nav', page: btn.dataset.page });
    });
  });

  // ── 服务状态 ─────────────────────────────────────────────
  var dot       = document.getElementById('dot');
  var stateText = document.getElementById('stateText');
  var portLabel = document.getElementById('portLabel');
  var urlText   = document.getElementById('urlText');
  var btnMain   = document.getElementById('btnMain');
  var btnRestart= document.getElementById('btnRestart');
  var btnWeb    = document.getElementById('btnWeb');
  var mainHint  = document.getElementById('mainHint');
  var vAddr = document.getElementById('vAddr');
  var vPort = document.getElementById('vPort');
  var vMode = document.getElementById('vMode');
  var vBrowser = document.getElementById('vBrowser');
  var vPlugins = document.getElementById('vPlugins');
  var vPid = document.getElementById('vPid');
  var vSvcCmd = document.getElementById('vSvcCmd');
  var vWorkDir = document.getElementById('vWorkDir');
  var vSvcLog = document.getElementById('vSvcLog');
  var vStateLog = document.getElementById('vStateLog');
  var vCfg = document.getElementById('vCfg');
  var pluginSum = document.getElementById('pluginSum');
  var pluginList = document.getElementById('pluginList');
  var btnAllNone = document.getElementById('btnAllNone');
  var btnReset = document.getElementById('btnReset');

  var last = { running: false, starting: false, port: 3080, plugins: [], dshok: false };

  function setState(s) {
    last.running  = !!s.running;
    last.starting = !!s.starting;
    last.port     = s.port || last.port;
    last.clean    = !!s.clean;

    var busy = last.starting || !!s.restarting;
    var running = last.running;

    dot.className = 'dot' + (busy ? ' busy' : (running ? ' on' : ''));
    stateText.textContent = busy ? (s.restarting ? '重启中…' : '启动中…')
                          : (running ? (s.clean ? '运行中（纯净）' : '运行中') : '已停止');
    portLabel.textContent = '端口 ' + last.port;

    urlText.textContent = running ? (s.url || '') : ('未运行 · 端口 ' + last.port);

    if (busy) {
      btnMain.textContent = s.restarting ? '重启中…' : '启动中…';
      btnMain.className = 'btn primary big';
      btnMain.disabled = true;
    } else if (running) {
      btnMain.textContent = '停止 DSH';
      btnMain.className = 'btn danger big';
      btnMain.disabled = false;
    } else {
      btnMain.textContent = '启动并打开 DSH';
      btnMain.className = 'btn primary big';
      btnMain.disabled = false;
    }
    btnRestart.hidden = !running;
    btnWeb.hidden = !running;
    mainHint.textContent = running ? '服务运行中 · 插件开关已锁定，改动需重启服务'
                                   : '启动后插件开关会自动锁定';

    vAddr.textContent = running ? (s.url || '') : '(未监听)';
    vPort.textContent = last.port;
    vMode.textContent = s.mode === 'tab' ? '普通窗口' : '无地址栏独立窗口';
    vBrowser.textContent = s.browser || '自动';
    vPlugins.textContent = (s.on || 0) + ' / ' + (s.total || 0);
    vPid.textContent = running && s.pid ? String(s.pid) : '—';

    // 服务运行中，插件开关锁定
    var locked = running || busy;
    Array.prototype.forEach.call(pluginList.querySelectorAll('input'), function (inp) {
      inp.disabled = locked || inp.dataset.protected === '1';
    });
    btnAllNone.disabled = locked;
    btnReset.disabled = locked;
  }

  function renderPlugins(list) {
    last.plugins = list || [];
    last.gotPlugins = true;
    pluginList.innerHTML = '';

    if (!last.dshok) {
      pluginSum.className = 'sum warn';
      pluginSum.textContent = '还没有指定 DSH 目录，读不到插件列表 —— 请到「设置 → DSH 目录」里选一下。';
      return;
    }

    var on = 0;
    var frag = document.createDocumentFragment();
    last.plugins.forEach(function (p) {
      if (p.on) { on++; }
      var row = document.createElement('div');
      row.className = 'prow' + (p.lock ? ' locked' : '');

      var n = document.createElement('span');
      n.className = 'pn';
      n.textContent = p.d || p.n;
      n.title = p.n;
      row.appendChild(n);

      if (p.v) {
        var v = document.createElement('span');
        v.className = 'pv';
        v.textContent = p.v;
        row.appendChild(v);
      }

      if (p.lock) {
        var tag = document.createElement('span');
        tag.className = 'tag';
        tag.textContent = '核心';
        row.appendChild(tag);
      }

      var sp = document.createElement('label');
      sp.className = 'sp sw';
      var inp = document.createElement('input');
      inp.type = 'checkbox';
      inp.checked = !!p.on;
      inp.dataset.name = p.n;
      inp.dataset.protected = p.lock ? '1' : '0';
      inp.disabled = (last.running || last.starting) || !!p.lock;
      inp.addEventListener('change', function () {
        post({ t: 'cmd', name: 'togglePlugin', plugin: p.n, on: inp.checked });
      });
      var i = document.createElement('i');
      sp.appendChild(inp);
      sp.appendChild(i);
      row.appendChild(sp);

      frag.appendChild(row);
    });

    pluginList.appendChild(frag);

    pluginSum.className = 'sum';
    pluginSum.textContent = '共 ' + last.plugins.length + ' 个已注册插件，启用 ' + on +
                            ' 个 · 勾选后在下次启动 DSH 时生效';
    btnAllNone.textContent = (on === last.plugins.length) ? '全不选' : '全选';
  }

  // ── 设置项 ───────────────────────────────────────────────
  var swMap = {
    autoStart:   document.getElementById('swAutoStart'),
    openOnStart: document.getElementById('swOpenOnStart'),
    autoTray:    document.getElementById('swAutoTray'),
    stopOnClose: document.getElementById('swStopOnClose'),
    notifyCrash: document.getElementById('swNotifyCrash'),
    dev:         document.getElementById('swDev')
  };

  function setSettings(o) {
    Object.keys(swMap).forEach(function (k) {
      if (typeof o[k] === 'boolean') {
        swMap[k].checked = o[k];
        swMap[k].dataset.ready = '1';
      }
    });
    if (typeof o.dark === 'boolean') { applyTheme(o.dark); }
    if (o.mode) { markSeg(o.mode); }
    if (o.browser) {
      var bl = document.getElementById('browserLabel');
      if (bl) { bl.textContent = o.browser; }
    }
    if (typeof o.port === 'number') { document.getElementById('inpPort').value = o.port; }
  }

  Object.keys(swMap).forEach(function (k) {
    swMap[k].addEventListener('change', function () {
      if (swMap[k].dataset.ready !== '1') { return; }
      post({ t: 'cmd', name: 'setSetting', key: k, value: swMap[k].checked });
    });
  });

  // ── 默认浏览器下拉（自绘，菜单不跑出面板） ──────────────
  var browserState = { current: '', list: [] };

  function renderBrowsers(o) {
    browserState.current = o.current || '';
    browserState.list = o.list || [];
    var label = document.getElementById('browserLabel');
    if (label) { label.textContent = o.current ? o.current : '系统默认浏览器'; }

    var menu = document.getElementById('browserMenu');
    if (!menu) { return; }
    menu.innerHTML = '';

    var items = [{ name: '系统默认浏览器', path: '' }].concat(browserState.list);
    items.forEach(function (b) {
      var el = document.createElement('button');
      el.className = 'mi' + (b.path === browserState.current ? ' on' : '');
      el.textContent = b.name;
      el.addEventListener('click', function () {
        post({ t: 'cmd', name: 'setBrowser', value: b.path });
        menu.hidden = true;
      });
      menu.appendChild(el);
    });

    var sep = document.createElement('div');
    sep.className = 'sep';
    menu.appendChild(sep);

    var pick = document.createElement('button');
    pick.className = 'mi dim';
    pick.textContent = '选择其他程序…';
    pick.addEventListener('click', function () {
      post({ t: 'cmd', name: 'pickBrowserFile' });
      menu.hidden = true;
    });
    menu.appendChild(pick);
  }

  var seg = document.getElementById('segMode');
  function markSeg(mode) {
    Array.prototype.forEach.call(seg.querySelectorAll('button'), function (b) {
      b.classList.toggle('active', b.dataset.mode === mode);
    });
  }
  seg.addEventListener('click', function (e) {
    var b = e.target.closest('button');
    if (!b) { return; }
    markSeg(b.dataset.mode);
    post({ t: 'cmd', name: 'setSetting', key: 'mode', value: b.dataset.mode });
  });

  // ── 路径信息 ─────────────────────────────────────────────
  function setPaths(p) {
    last.dshok = !!p.cfgok;
    var pathEl = document.getElementById('dshPath');
    var stEl   = document.getElementById('dshState');

    if (p.cfgok) {
      pathEl.className = 'path ok';
      pathEl.textContent = p.dshhome || '—';
      stEl.className = 'st ok';
      stEl.textContent = '已找到 DeepSeek Harness 的数据目录。';
    } else {
      pathEl.className = 'path';
      pathEl.textContent = p.dshhome || '（未指定）';
      stEl.className = 'st';
      stEl.textContent = p.dshhome
        ? '这个目录不存在，或者不像 DSH 的数据目录（应当包含 profiles 子目录）。'
        : '还没有指定 DSH 的数据目录。';
    }

    if (p.svccmd)   { vSvcCmd.textContent   = p.svccmd; }
    if (p.workdir)  { vWorkDir.textContent  = p.workdir; }
    if (p.svclog)   { vSvcLog.textContent   = p.svclog; }
    if (p.statelog) { vStateLog.textContent = p.statelog; }

    // paths 可能比 plugins 晚到：补一次渲染，否则会一直显示「还没有指定 DSH 目录」
    if (last.gotPlugins) { renderPlugins(last.plugins); }
  }

  // ── 收到 C# 消息 ─────────────────────────────────────────
  function onMessage(data) {
    switch (data.t) {
      case 'state':    setState(data); break;
      case 'plugins':  renderPlugins(data.list); break;
      case 'paths':    setPaths(data); break;
      case 'settings': setSettings(data); break;
      case 'theme':    applyTheme(!!data.dark); break;
      case 'toast':    toast(data.msg || ''); break;
      case 'log':      appendLog(data.text || ''); break;
      case 'nav':      showPage(data.page); break;
      case 'browsers': renderBrowsers(data); break;
    }
  }

  if (wv) {
    wv.addEventListener('message', function (e) { onMessage(e.data); });
  }

  // ── 按钮 ─────────────────────────────────────────────────
  btnMain.addEventListener('click', function () {
    post({ t: 'cmd', name: last.running ? 'stop' : 'start' });
  });
  btnRestart.addEventListener('click', function () { post({ t: 'cmd', name: 'restart' }); });
  btnWeb.addEventListener('click', function () { post({ t: 'cmd', name: 'openWeb' }); });

  btnAllNone.addEventListener('click', function () {
    var allOn = last.plugins.length > 0 && last.plugins.every(function (p) { return p.on; });
    var target = !allOn;
    last.plugins.forEach(function (p) {
      if (p.lock) { return; }
      post({ t: 'cmd', name: 'togglePlugin', plugin: p.n, on: target });
    });
  });

  btnReset.addEventListener('click', function () {
    post({ t: 'cmd', name: 'resetPlugins' });
  });

  document.getElementById('btnPickDsh').addEventListener('click', function () {
    post({ t: 'cmd', name: 'pickDshHome' });
  });
  document.getElementById('btnProbeDsh').addEventListener('click', function () {
    post({ t: 'cmd', name: 'probeDshHome' });
  });
  document.getElementById('btnPickBrowser').addEventListener('click', function (e) {
    e.stopPropagation();
    var menu = document.getElementById('browserMenu');
    menu.hidden = !menu.hidden;
  });
  // 点面板其它地方收起来；菜单里的点击自己处理
  document.addEventListener('click', function () {
    var menu = document.getElementById('browserMenu');
    if (menu) { menu.hidden = true; }
  });
  document.getElementById('browserMenu').addEventListener('click', function (e) {
    e.stopPropagation();
  });
  document.getElementById('btnSavePort').addEventListener('click', function () {
    post({ t: 'cmd', name: 'setPort', value: document.getElementById('inpPort').value.trim() });
  });
  document.getElementById('btnClean').addEventListener('click', function () {
    post({ t: 'cmd', name: 'clean' });
  });
  document.getElementById('btnLogClear').addEventListener('click', function () {
    document.getElementById('logBox').textContent = '';
  });
  // 退出按钮在外壳（WinForms 侧），页面里没有；保留判空防止脚本中断
  var btnExit = document.getElementById('btnExit');
  if (btnExit) {
    btnExit.addEventListener('click', function () { post({ t: 'cmd', name: 'exit' }); });
  }
  document.getElementById('btnDevTools').addEventListener('click', function () {
    post({ t: 'cmd', name: 'devtools' });
  });
  document.getElementById('btnOpenRepo').addEventListener('click', function () {
    post({ t: 'cmd', name: 'openRepo' });
  });

  // ── 日志 ────────────────────────────────────────────────
  var logBox = document.getElementById('logBox');
  function appendLog(text) {
    if (!text) { return; }
    var atBottom = logBox.scrollTop + logBox.clientHeight >= logBox.scrollHeight - 30;
    logBox.textContent += text;
    if (atBottom) { logBox.scrollTop = logBox.scrollHeight; }
  }

  // 键盘：F5 刷新禁用、Ctrl+Shift+I 开发者工具
  document.addEventListener('keydown', function (e) {
    if (e.key === 'F5') { e.preventDefault(); }
    if (e.ctrlKey && e.shiftKey && (e.key === 'I' || e.key === 'i')) {
      e.preventDefault();
      post({ t: 'cmd', name: 'devtools' });
    }
  });

  // 【DPI】用 CSS zoom 把页面缩放到面板的「设备像素」坐标系。
  // 面板自绘控件在 200% 缩放下客户区是 2004×1564 设备像素，而 WebView2 给的 CSS 视口
  // 是 1810×1514、devicePixelRatio 2 —— 不处理就会显示成两倍大、右侧被裁掉。
  // Chrome 的 zoom 会把布局视口除以 zoom、渲染尺寸乘以 zoom，devicePixelRatio 不变，
  // 所以 zoom = dpr 之后：视口 ≈ 905 CSS，每 CSS px 占 2 设备像素，字号视觉尺寸正好。
  // 【DPI】缩放交给 C# 侧的 WebView2 ZoomFactor（RasterizationScale 钉 1、ZoomFactor =
  // 面板缩放系数），页面这里不再自己 zoom —— 两者叠加会变成 4 倍。
  var dpr = window.devicePixelRatio || 1;

  // 就绪：告诉 C# 可以推数据了（顺带把视口尺寸报回去，方便排查 DPI 缩放导致的布局问题）
  post({
    t: 'ready',
    w: window.innerWidth,
    h: window.innerHeight,
    dpr: window.devicePixelRatio || 1,
    zoom: (document.body.style.zoom || 'none'),
    bodyW: document.body.clientWidth
  });
  showPage('service');
})();
