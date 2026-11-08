using System.Text;
using System.Text.Json;

namespace DshPanel2;

/// <summary>
/// C# ↔ 网页的唯一桥：推送界面状态、分发页面命令。
///
/// 协议与旧版完全一致（15 种推送 / 25 个 cmd），但有一处刻意的行为变化：
/// 旧版把「读配置→算状态→拼 JSON→推网页」散落在 PanelUi.cs 的十几个方法里，
/// 且与 WinForms 控件纠缠（例如状态文本先写 Label 再被托盘读去做字符串比较）。
/// 本版所有推送都从这里出，状态一律来自 <see cref="PanelConfig"/> 与
/// <see cref="ServiceController"/>，不经过任何控件。
/// </summary>
internal sealed class UiBridge
{
    private readonly WebHost _host;
    private readonly PanelConfig _cfg;
    private readonly ServiceController _svc;

    internal UiBridge(WebHost host, PanelConfig cfg, ServiceController svc)
    {
        _host = host;
        _cfg = cfg;
        _svc = svc;
    }

    /// <summary>网页标题栏的拖动请求，由 MainForm 接管（发 WM_NCLBUTTONDOWN+HTCAPTION）。</summary>
    internal Action? DragRequested { get; set; }
    internal Action? MinimizeRequested { get; set; }
    internal Action? CloseRequested { get; set; }

    // ==================== 推送 ====================

    /// <summary>页面完成握手后的全量推送。顺序与旧版一致，前端有依赖。</summary>
    internal void PushAll()
    {
        PushState();
        PushPaths();
        PushSettings();
        PushPlugins();
        PushBrowsers();
        PushTheme();
        _svc.PushServiceLogTail();   // 把已有日志补过去，否则日志页每次进都是空的
    }

    /// <summary>可选浏览器列表。修复旧版缺陷 d：额外给出 currentPath，让页面用路径比较选中态。</summary>
    internal void PushBrowsers()
    {
        var list = BrowserLauncher.FindBrowsers();

        string currentName;
        if (!string.IsNullOrEmpty(_cfg.BrowserPath))
        {
            currentName = Path.GetFileNameWithoutExtension(_cfg.BrowserPath);
        }
        else
        {
            var auto = BrowserLauncher.FindBrowser();
            currentName = string.IsNullOrEmpty(auto) ? "未探测到" : Path.GetFileNameWithoutExtension(auto);
        }

        var sb = new StringBuilder(512);
        sb.Append("{\"t\":\"browsers\"");
        sb.Append(",\"current\":\"").Append(Esc(currentName)).Append('"');
        sb.Append(",\"currentPath\":\"").Append(Esc(_cfg.BrowserPath)).Append('"');
        sb.Append(",\"list\":[");

        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append("{\"name\":\"").Append(Esc(list[i].Name))
              .Append("\",\"path\":\"").Append(Esc(list[i].Path)).Append("\"}");
        }

        sb.Append("]}");
        _host.Post(sb.ToString());
    }

    /// <summary>插件列表。载荷字段与旧版一致：{list:[{n,d,v,on,lock}]}。</summary>
    internal void PushPlugins()
    {
        var items = new List<PluginItem>();
        var path = _cfg.PluginJson;
        string err;
        var ok = false;

        if (string.IsNullOrEmpty(path))
        {
            err = "没有配置 DSH 目录，无法定位插件清单";
        }
        else if (!File.Exists(path))
        {
            err = "插件清单不存在：" + path;
        }
        else
        {
            ok = PluginStore.Load(path, items, out err);
        }

        var sb = new StringBuilder(2048);
        sb.Append("{\"t\":\"plugins\",\"ok\":").Append(JsBool(ok));
        if (!ok)
        {
            sb.Append(",\"err\":\"").Append(Esc(err)).Append('"');
        }
        sb.Append(",\"on\":").Append(items.Count(i => i.Enabled));
        sb.Append(",\"total\":").Append(items.Count);
        sb.Append(",\"list\":[");

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (i > 0) { sb.Append(','); }
            sb.Append("{\"n\":\"").Append(Esc(it.Name))
              .Append("\",\"d\":\"").Append(Esc(it.Display))
              .Append("\",\"v\":\"").Append(Esc(it.Version))
              .Append("\",\"on\":").Append(JsBool(it.Enabled))
              .Append(",\"lock\":").Append(JsBool(it.Protected))
              .Append('}');
        }

        sb.Append("]}");
        _host.Post(sb.ToString());
    }

    internal void PushState()
    {
        var s = _svc.Snapshot();
        var sb = new StringBuilder(256);
        sb.Append("{\"t\":\"state\"");
        sb.Append(",\"running\":").Append(JsBool(s.Running));
        sb.Append(",\"starting\":").Append(JsBool(s.Starting));
        sb.Append(",\"restarting\":").Append(JsBool(s.Restarting));
        sb.Append(",\"clean\":").Append(JsBool(s.Clean));
        sb.Append(",\"port\":").Append(_cfg.Port);
        sb.Append(",\"pid\":").Append(s.Pid);
        sb.Append(",\"mode\":\"").Append(Esc(_cfg.BrowserMode)).Append('"');
        sb.Append(",\"browser\":\"").Append(Esc(Path.GetFileNameWithoutExtension(_cfg.BrowserPath))).Append('"');
        sb.Append(",\"url\":\"").Append(Esc(s.Url)).Append('"');
        sb.Append(",\"dark\":").Append(JsBool(_cfg.DarkTheme));
        // 网页窗口是否开着：前端据此在「打开网页 DSH」和「关闭网页 DSH」之间切换。
        sb.Append(",\"webOpen\":").Append(JsBool(s.WebOpen));
        // 启动失败的短提示。ServiceController 只在失败窗口期内给出值，过期自动清空。
        // 文档里声明过这个字段、代码却一直漏推，网页的失败态因此永远不出现。
        if (!string.IsNullOrEmpty(s.FailMsg))
        {
            sb.Append(",\"failMsg\":\"").Append(Esc(s.FailMsg)).Append('"');
        }
        sb.Append('}');
        _host.Post(sb.ToString());
    }

    internal void PushSettings()
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"t\":\"settings\"");
        sb.Append(",\"autoStart\":").Append(JsBool(_cfg.AutoStart));
        sb.Append(",\"openOnStart\":").Append(JsBool(_cfg.AutoOpen));
        sb.Append(",\"autoTray\":").Append(JsBool(_cfg.AutoTray));
        sb.Append(",\"stopOnClose\":").Append(JsBool(_cfg.StopWithPanel));
        sb.Append(",\"closeToTray\":").Append(JsBool(_cfg.CloseToTray));
        sb.Append(",\"notifyCrash\":").Append(JsBool(_cfg.BalloonOnStop));
        sb.Append(",\"dev\":").Append(JsBool(_cfg.DevMode));
        sb.Append(",\"dark\":").Append(JsBool(_cfg.DarkTheme));
        sb.Append(",\"mode\":\"").Append(Esc(_cfg.BrowserMode)).Append('"');
        sb.Append(",\"browser\":\"").Append(Esc(_cfg.BrowserPath)).Append('"');
        sb.Append(",\"port\":").Append(_cfg.Port);
        sb.Append('}');
        _host.Post(sb.ToString());
    }

    internal void PushPaths()
    {
        var sb = new StringBuilder(320);
        sb.Append("{\"t\":\"paths\"");
        sb.Append(",\"dshhome\":\"").Append(Esc(_cfg.DshHome)).Append('"');
        sb.Append(",\"cfgok\":").Append(JsBool(_cfg.DshConfigured));
        sb.Append(",\"svccmd\":\"").Append(Esc(_cfg.SvcCmd)).Append('"');
        sb.Append(",\"workdir\":\"").Append(Esc(_cfg.WorkDir)).Append('"');
        sb.Append(",\"svclog\":\"").Append(Esc(_cfg.LogFile)).Append('"');
        sb.Append(",\"statelog\":\"").Append(Esc(_cfg.StateLog)).Append('"');
        sb.Append(",\"plugin\":\"").Append(Esc(_cfg.PluginJson)).Append('"');
        sb.Append(",\"cfgfile\":\"").Append(Esc(_cfg.FilePath)).Append('"');
        sb.Append(",\"datadir\":\"").Append(Esc(Paths.DataDir)).Append('"');
        sb.Append('}');
        _host.Post(sb.ToString());
    }

    internal void PushTheme()
    {
        _host.Post("{\"t\":\"theme\",\"dark\":" + JsBool(_cfg.DarkTheme) + "}");
    }

    /// <summary>让页面切到某页（托盘「运行日志」等入口用）。</summary>
    internal void PushNav(string page)
    {
        _host.Post("{\"t\":\"nav\",\"page\":\"" + Esc(page) + "\"}");
    }

    internal void PushToast(string msg)
    {
        _host.Post("{\"t\":\"toast\",\"msg\":\"" + Esc(msg) + "\"}");
    }

    /// <summary>日志增量。调用方（ServiceController.ScanLog）保证 text 已是新增片段。</summary>
    internal void PushLog(string text)
    {
        if (string.IsNullOrEmpty(text)) { return; }
        _host.Post("{\"t\":\"log\",\"text\":\"" + Esc(text) + "\"}");
    }

    // ==================== 命令分发 ====================

    /// <summary>处理页面发来的一条消息。所有 key 都与旧版一致。</summary>
    internal void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("t", out var tEl) ? tEl.GetString() : null;

            if (type == "ready")
            {
                _host.MarkReady();

                // 页面握手时顺带报一份布局尺寸，便于排查「控件被裁」这类渲染问题
                if (root.TryGetProperty("diag", out var diag))
                {
                    Program.LogEvent(
                        "页面布局：" +
                        $"视口={root.GetProperty("w")}x{root.GetProperty("h")} dpr={root.GetProperty("dpr")} " +
                        $"侧边栏={GetInt(diag, "sidebar")} 内容区={GetInt(diag, "content")} " +
                        $"卡片={GetInt(diag, "card")} --nav-w={GetStr(diag, "navMin")}");
                }

                Program.LogEvent("页面已完成握手（ready），开始全量推送");
                PushAll();
                return;
            }

            // 视口尺寸变化：WebView2 自己会重排布局，这里不需要额外处理
            if (type == "resize" || type == "nav")
            {
                return;
            }

            if (type != "cmd")
            {
                Program.LogEvent("未知消息类型：" + Truncate(json, 160));
                return;
            }

            var name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
            switch (name)
            {
                case "setSetting":
                    ApplyWebSetting(
                        root.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "",
                        root.TryGetProperty("value", out var vEl) ? vEl : default);
                    break;

                case "setPort":
                    ApplyPort(root.TryGetProperty("value", out var pEl) ? pEl.ToString() : "");
                    break;

                case "pickDshHome":
                    PickDshHome();
                    break;

                case "probeDshHome":
                    ProbeDshHome();
                    break;

                case "togglePlugin":
                    ApplyPluginToggle(
                        root.TryGetProperty("plugin", out var plEl) ? plEl.GetString() ?? "" : "",
                        root.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True);
                    break;

                case "toggleAllPlugins":
                    ToggleAllPlugins(
                        root.TryGetProperty("on", out var allEl) && allEl.ValueKind == JsonValueKind.True);
                    break;

                case "toggleTheme":
                    _cfg.DarkTheme = !_cfg.DarkTheme;
                    _cfg.Save();
                    PushTheme();
                    PushSettings();
                    break;

                case "pickBrowserFile":
                    PickBrowserFile();
                    break;

                case "setBrowser":
                    SetBrowser(root.TryGetProperty("value", out var bEl) ? bEl.GetString() ?? "" : "");
                    break;

                // ---- 以下为 M2 起的服务控制，此处先接上分发骨架 ----
                case "start":
                case "stop":
                case "restart":
                case "openWeb":
                case "clean":
                case "clearLog":
                    _svc.HandleCommand(name);
                    break;

                // ---- 窗口控制（网页标题栏）----
                case "drag":
                    DragRequested?.Invoke();
                    break;
                case "minimize":
                    MinimizeRequested?.Invoke();
                    break;
                case "close":
                    CloseRequested?.Invoke();
                    break;

                // ---- 尚未实现的：明确记日志，避免静默丢弃 ----
                case "openRepo":
                    OpenUrl("https://github.com/ee22ee/dsh-control-panel");
                    break;
                case "devtools":
                    Program.LogEvent("devtools 请求已忽略（本版关闭了 DevTools）");
                    break;
                default:
                    Program.LogEvent("未实现命令：" + name);
                    break;
            }
        }
        catch (JsonException ex)
        {
            Program.LogEvent("消息解析失败：" + ex.Message + " 原文：" + Truncate(json, 200));
        }
    }

    private void ApplyWebSetting(string key, JsonElement value)
    {
        // value 可能是 bool / string / number，逐个类型取
        bool boolVal = value.ValueKind == JsonValueKind.True ||
                       (value.ValueKind == JsonValueKind.String && IniText.Truthy(value.GetString() ?? ""));
        var strVal = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();

        switch (key)
        {
            case "mode":
                // 只接受 app / tab，其余忽略（旧版也是这个行为）
                if (strVal is "app" or "tab") { _cfg.BrowserMode = strVal; }
                else { PushToast("未知的打开方式：" + strVal); return; }
                break;
            case "autoStart":   _cfg.AutoStart = boolVal; break;
            case "openOnStart": _cfg.AutoOpen = boolVal; break;
            case "autoTray":    _cfg.AutoTray = boolVal; break;
            case "stopOnClose": _cfg.StopWithPanel = boolVal; break;
            case "closeToTray": _cfg.CloseToTray = boolVal; break;
            case "notifyCrash": _cfg.BalloonOnStop = boolVal; break;
            case "dev":         _cfg.DevMode = boolVal; break;
            case "dark":        _cfg.DarkTheme = boolVal; PushTheme(); break;
            default:
                Program.LogEvent("未知设置项：" + key);
                return;
        }

        _cfg.Save();

        // 开机自启是唯一有系统副作用的开关：写注册表失败就回滚配置，
        // 避免出现「配置说开、实际没开」的不一致（旧版缺陷 i 的另一面）
        if (key == "autoStart")
        {
            if (!AutoStart.Set(_cfg.AutoStart, out var err))
            {
                _cfg.AutoStart = !_cfg.AutoStart;
                _cfg.Save();
                Program.LogEvent("开机自启设置失败，已回滚配置：" + err);
                PushToast("开机自启设置失败：" + err);
                PushSettings();
                return;
            }
            Program.LogEvent(_cfg.AutoStart ? "开机自启已开启" : "开机自启已关闭");
            PushToast(_cfg.AutoStart ? "已开启开机自启" : "已关闭开机自启");
        }

        PushSettings();
        PushState();
    }

    private void ApplyPort(string raw)
    {
        if (!int.TryParse(raw.Trim(), out var p) || p <= 0 || p >= 65536)
        {
            PushToast("端口必须是 1~65535 之间的整数");
            return;
        }

        if (_svc.IsRunning())
        {
            PushToast("服务正在运行，先把服务停掉再改端口");
            return;
        }

        _cfg.Port = p;
        _cfg.Save();
        PushToast("端口已改为 " + p);
        PushState();
        PushPaths();
    }

    private void PickDshHome()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择 DSH 数据目录（里面应当有 profiles 子目录）",
            UseDescriptionForTitle = true,
            SelectedPath = _cfg.DshHome,
        };
        if (dlg.ShowDialog() != DialogResult.OK) { return; }

        var picked = dlg.SelectedPath;
        if (!Paths.LooksLikeDshHome(picked))
        {
            var go = MessageBox.Show(
                "这个目录里没有 profiles 子目录，看起来不像是 DSH 的数据目录。\n\n仍然使用它吗？",
                Program.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (go != DialogResult.Yes) { return; }
            // 手动指定了非标准目录：插件清单路径不再自动推导
            _cfg.PluginJsonRaw = "";
        }

        _cfg.DshHome = picked;
        _cfg.Save();
        Program.LogEvent("用户手动指定 DSH 目录：" + picked);
        PushPaths();
        PushToast("DSH 目录已设置为：" + picked);
    }

    private void ProbeDshHome()
    {
        var found = Paths.ProbeDefaultDshHome();
        if (string.IsNullOrEmpty(found))
        {
            PushToast("没有在默认位置找到 DSH 目录（DSH_HOME 或 %USERPROFILE%\\.dsh）");
            return;
        }

        _cfg.DshHome = found;
        _cfg.Save();
        Program.LogEvent("重新探测到 DSH 目录：" + found);
        PushPaths();
        PushPlugins();
        PushToast("已找到 DSH 目录：" + found);
    }

    // ==================== 浏览器 ====================

    /// <summary>手动指定浏览器可执行文件。</summary>
    private void SetBrowser(string path)
    {
        if (!string.IsNullOrEmpty(path) && !File.Exists(path))
        {
            PushToast("这个文件不存在：" + path);
            return;
        }

        _cfg.BrowserPath = path;
        _cfg.Save();
        Program.LogEvent("默认浏览器设为：" + (path.Length == 0 ? "自动探测" : path));
        PushBrowsers();
        PushToast(path.Length == 0
            ? "已改为自动探测浏览器"
            : "已设为 " + Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>弹文件选择框挑浏览器 exe。</summary>
    private void PickBrowserFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择浏览器可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog() == DialogResult.OK)
        {
            SetBrowser(dlg.FileName);
        }
    }

    // ==================== 插件 ====================

    /// <summary>
    /// 单个插件开关。服务运行中拒绝修改 —— package.json 改了也不会热生效，
    /// 反而容易让用户以为「点了没反应」。
    /// </summary>
    private void ApplyPluginToggle(string name, bool on)
    {
        if (_svc.IsRunning())
        {
            PushToast("服务正在运行，先停止服务再改插件");
            return;
        }

        var path = _cfg.PluginJson;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            PushToast("找不到插件清单（先设置 DSH 目录）");
            return;
        }

        var items = new List<PluginItem>();
        if (!PluginStore.Load(path, items, out var err))
        {
            PushToast(err);
            return;
        }

        var item = items.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            PushToast("找不到插件：" + name);
            return;
        }

        if (item.Protected && !on)
        {
            PushToast($"{item.Display} 是 DSH 命脉插件，不能禁用");
            return;
        }

        item.Enabled = on;

        if (!PluginStore.Save(path, items, out var saveErr))
        {
            Program.LogEvent("插件开关写入失败：" + saveErr);
            PushToast(saveErr);
            return;
        }

        Program.LogEvent($"插件 {(on ? "启用" : "停用")}：{name}");
        PushPlugins();
        PushState();
        PushToast(on
            ? $"已启用 {item.Display}（下次启动 DSH 生效）"
            : $"已停用 {item.Display}（下次启动 DSH 生效）");
    }

    /// <summary>全开 / 全关。受保护的插件始终保留启用。</summary>
    private void ToggleAllPlugins(bool on)
    {
        if (_svc.IsRunning())
        {
            PushToast("服务正在运行，先停止服务再改插件");
            return;
        }

        var path = _cfg.PluginJson;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            PushToast("找不到插件清单（先设置 DSH 目录）");
            return;
        }

        var items = new List<PluginItem>();
        if (!PluginStore.Load(path, items, out var err))
        {
            PushToast(err);
            return;
        }

        foreach (var item in items)
        {
            item.Enabled = on || item.Protected;
        }

        if (!PluginStore.Save(path, items, out var saveErr))
        {
            PushToast(saveErr);
            return;
        }

        Program.LogEvent(on ? "全部插件已启用" : "全部插件已停用（保留命脉插件）");
        PushPlugins();
        PushState();
        PushToast(on ? "已启用全部插件" : "已停用全部插件（命脉插件保留）");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Program.LogEvent("打开链接失败：" + ex.Message);
        }
    }

    // ==================== JSON 辅助 ====================

    /// <summary>
    /// JSON 字符串转义。只处理必须的几类字符，与旧版一致：
    /// 不处理非 BMP / 代理对（面板里的文本都是本地化短字符串，用不到）。
    /// </summary>
    internal static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) { return ""; }

        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) { sb.Append("\\u").Append(((int)ch).ToString("x4")); }
                    else { sb.Append(ch); }
                    break;
            }
        }
        return sb.ToString();
    }

    internal static string JsBool(bool b) => b ? "true" : "false";

    private static string GetInt(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.ToString() : "?";

    private static string GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.ToString() : "?";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
