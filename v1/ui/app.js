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
      btnMain.className = 'btn primary big busy';
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
      case 'pet':        onPet(data); break;
      case 'petSchema':  renderPetSchema(data.raw); break;
      case 'petConfig':
        if (data.raw && data.raw.ok) { pet.cfg = data.raw.data || {}; petApplyConfig(); }
        break;
      case 'petState':   renderPetState(data.raw); break;
      case 'petPatched': onPetPatched(data.raw); break;
      case 'petAction':
        if (data.raw && data.raw.ok === false) { toast('桌宠：' + (data.raw.error || '操作失败')); }
        break;
    }
  }

  if (wv) {
    wv.addEventListener('message', function (e) { onMessage(e.data); });
  }

  // ── 按钮 ─────────────────────────────────────────────────
  btnMain.addEventListener('click', function () {
    if (last.running) {
      // 停止也要即时反馈，不然点完那一两秒像是没点到
      btnMain.textContent = '正在停止…';
      btnMain.className = 'btn primary big busy';
      btnMain.disabled = true;
      post({ t: 'cmd', name: 'stop' });
      return;
    }
    // 乐观置忙：不等外壳回推 state，点击瞬间就换成「启动中…」+ 扫光动画。
    // 外壳推回真实状态（starting / running / 失败）时会自动覆盖这个临时状态。
    btnMain.textContent = '启动中…';
    btnMain.className = 'btn primary big busy';
    btnMain.disabled = true;
    btnRestart.hidden = true;
    btnWeb.hidden = true;
    dot.className = 'dot busy';
    stateText.textContent = '启动中…';
    mainHint.textContent = '正在拉起服务，请稍候…';
    post({ t: 'cmd', name: 'start' });
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
    // 连外壳里的日志缓冲一起清，否则下次进日志页历史又被补回来
    post({ t: 'cmd', name: 'clearLog' });
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

  // ── 桌宠（dsh-pet）────────────────────────────────────────
  // 外壳把桌宠的 schema / config / state 原样转发过来，这里按 schema 现场生成控件，
  // 不硬编码任何字段名 —— 桌宠那边加一项设置，面板里就自动多一行。
  var pet = { on: false, groups: [], cfg: {}, ver: '', dir: '' };
  var cardPet  = document.getElementById('cardPet');
  var petHead  = document.getElementById('petHead');
  var petBody  = document.getElementById('petGroups');
  var petVerEl = document.getElementById('petVer');
  var petNowEl = document.getElementById('petNow');
  var petDirEl = document.getElementById('petDir');
  var petCfgEl = document.getElementById('petCfgDir');

  var PET_STATE = {
    IDLE: '空闲', THINKING: '思考中', WORKING: '干活中',
    WAITING: '等你确认', SUCCESS: '刚完成', ERROR: '出错了', OFFLINE: '未连接'
  };

  // 折叠状态记在本地；默认收起，用户展开后下次还是展开
  if (localStorage.getItem('petOpen') === '1') { cardPet.classList.remove('collapsed'); }
  else { cardPet.classList.add('collapsed'); }
  petHead.addEventListener('click', function () {
    cardPet.classList.toggle('collapsed');
    localStorage.setItem('petOpen', cardPet.classList.contains('collapsed') ? '0' : '1');
  });

  function petShow(on) { cardPet.hidden = !on; }
  function petSetVer(v) { petVerEl.textContent = v ? ('v' + v) : ''; }
  function petSetNow(txt) { petNowEl.textContent = txt || ''; }
  function petSetDir(d) { petDirEl.textContent = d || ''; }

  function petFindField(k) {
    for (var i = 0; i < pet.groups.length; i++) {
      var fs = pet.groups[i].fields || [];
      for (var j = 0; j < fs.length; j++) { if (fs[j].key === k) { return fs[j]; } }
    }
    return null;
  }

  function petFmt(v, f) {
    var n = Number(v);
    if (isNaN(n)) { return String(v); }
    var s = (Math.round(n * 100) / 100).toString();
    return f && f.unit ? (s + ' ' + f.unit) : s;
  }

  function petPatch(key, value) {
    var patch = {};
    patch[key] = value;
    post({ t: 'cmd', name: 'petPatch', patch: patch });
  }

  // 按 schema 造一个字段行
  function petField(f) {
    var row = document.createElement('div');
    row.className = 'pet-field';
    row.dataset.key = f.key;

    var lab = document.createElement('div');
    lab.className = 'fl';
    lab.textContent = f.label || f.key;
    row.appendChild(lab);

    var box = document.createElement('div');
    box.className = 'fc';

    if (f.kind === 'number') {
      var r = document.createElement('input');
      r.type = 'range';
      r.className = 'rng';
      r.min = (f.min != null ? f.min : 0);
      r.max = (f.max != null ? f.max : 100);
      r.step = (f.step != null ? f.step : 1);
      r.addEventListener('input', function () {
        var v = row.querySelector('.fv');
        if (v) { v.textContent = petFmt(r.value, f); }
      });
      r.addEventListener('change', function () { petPatch(f.key, Number(r.value)); });
      box.appendChild(r);
    } else if (f.kind === 'bool') {
      var sw = document.createElement('label');
      sw.className = 'sw';
      var cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.addEventListener('change', function () { petPatch(f.key, cb.checked); });
      sw.appendChild(cb);
      sw.appendChild(document.createElement('i'));
      box.appendChild(sw);
    } else if (f.kind === 'enum') {
      var seg = document.createElement('div');
      seg.className = 'seg';
      (f.values || []).forEach(function (v) {
        var b = document.createElement('button');
        b.type = 'button';
        b.dataset.v = v;
        b.textContent = (f.labels && f.labels[v]) || v;
        b.addEventListener('click', function () { petPatch(f.key, v); });
        seg.appendChild(b);
      });
      box.appendChild(seg);
    } else {
      var inp = document.createElement('input');
      inp.className = 'inp';
      // 密钥类字段默认遮起来，避免面板上明文摊着 API Key
      var secret = /(key|token|secret|password)/i.test(f.key);
      inp.type = secret ? 'password' : 'text';
      if (f.default) { inp.placeholder = f.default; }
      inp.addEventListener('change', function () { petPatch(f.key, inp.value); });
      box.appendChild(inp);
      if (secret) {
        var eye = document.createElement('button');
        eye.type = 'button';
        eye.className = 'btn ghost';
        eye.textContent = '显示';
        eye.addEventListener('click', function () {
          if (inp.type === 'password') { inp.type = 'text'; eye.textContent = '隐藏'; }
          else { inp.type = 'password'; eye.textContent = '显示'; }
        });
        box.appendChild(eye);
      }
    }

    row.appendChild(box);

    var val = document.createElement('div');
    val.className = 'fv';
    row.appendChild(val);

    if (f.hint) {
      var h = document.createElement('div');
      h.className = 'fh';
      h.textContent = f.hint;
      row.appendChild(h);
    }
    return row;
  }

  function renderPetSchema(raw) {
    var groups = (raw && raw.ok && raw.data && raw.data.groups) || [];
    pet.groups = groups;
    petBody.innerHTML = '';
    if (!groups.length) {
      var empty = document.createElement('div');
      empty.className = 'hint2';
      empty.textContent = '桌宠没有提供设置项。';
      petBody.appendChild(empty);
      return;
    }
    groups.forEach(function (g) {
      var box = document.createElement('div');
      box.className = 'pet-group';
      var gname = document.createElement('div');
      gname.className = 'gname';
      gname.textContent = g.name || '';
      box.appendChild(gname);
      (g.fields || []).forEach(function (f) { box.appendChild(petField(f)); });
      petBody.appendChild(box);
    });
    petApplyConfig();
  }

  // 把当前配置回填到控件上（不打断正在编辑的输入框）
  function petApplyConfig() {
    var c = pet.cfg || {};
    var rows = petBody.querySelectorAll('.pet-field');
    for (var i = 0; i < rows.length; i++) {
      var row = rows[i], k = row.dataset.key;
      if (!(k in c)) { continue; }
      var f = petFindField(k);
      if (!f) { continue; }
      var v = c[k];
      var shown = row.querySelector('.fv');
      if (f.kind === 'number') {
        var r = row.querySelector('.rng');
        if (r) { r.value = v; }
        if (shown) { shown.textContent = petFmt(v, f); }
      } else if (f.kind === 'bool') {
        var cb = row.querySelector('input[type=checkbox]');
        if (cb) { cb.checked = !!v; }
      } else if (f.kind === 'enum') {
        var bs = row.querySelectorAll('.seg button');
        for (var j = 0; j < bs.length; j++) {
          bs[j].classList.toggle('active', bs[j].dataset.v === v);
        }
      } else {
        var inp = row.querySelector('.inp');
        if (inp && document.activeElement !== inp) { inp.value = (v == null ? '' : v); }
      }
    }
  }

  function renderPetState(raw) {
    if (!raw || !raw.ok || !raw.data) { return; }
    var st = raw.data.status || {};
    var name = PET_STATE[st.state] || st.state || '';
    petSetNow(name ? (name + (st.message ? (' · ' + st.message) : '')) : '');
    if (raw.data.config) { pet.cfg = raw.data.config; petApplyConfig(); }
  }

  function onPet(d) {
    pet.on = !!d.running;
    petShow(pet.on);
    // 高级卡里的「桌宠目录」：配置值还是自动探测，都显示出来，方便排查为什么没认到
    if (petCfgEl) {
      if (d.cfgdir) { petCfgEl.textContent = d.cfgdir; petCfgEl.title = d.cfgdir; }
      else { petCfgEl.textContent = '自动探测'; petCfgEl.title = ''; }
    }
    if (pet.on) {
      petSetVer(d.version);
      petSetDir(d.dir ? ('目录：' + d.dir) : '');
    } else {
      petSetNow('');
    }
  }

  function onPetPatched(raw) {
    if (!raw) { toast('桌宠没有响应'); return; }
    if (raw.ok === false) { toast('桌宠：' + (raw.error || '保存失败')); return; }
    var d = raw.data || {};
    if (d.config) { pet.cfg = d.config; petApplyConfig(); }
    if (d.ignored && d.ignored.length) {
      toast('桌宠不认识这些设置：' + d.ignored.join('、'));
    }
  }

  document.getElementById('petActs').addEventListener('click', function (e) {
    var b = e.target.closest ? e.target.closest('button[data-act]') : null;
    if (!b) { return; }
    post({ t: 'cmd', name: 'petAction', action: b.dataset.act });
  });
  document.getElementById('btnPetReset').addEventListener('click', function () {
    post({ t: 'cmd', name: 'petReset' });
  });
  document.getElementById('btnPetDir').addEventListener('click', function () {
    post({ t: 'cmd', name: 'petPickDir' });
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
