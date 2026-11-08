using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshPanel2;

/// <summary>
/// WebView2 宿主：唯一的内容渲染面。
///
/// 设计取舍（相对旧版）：
///   旧版是「WinForms 自绘外壳 + WebView2 内容区」两套渲染，
///   导致主题切换要重涂两遍（P12）、滚动要用 IMessageFilter 截（P24）、
///   句柄越用越多（P10/P25）、切页要抓图做动画（P8）。
///   本版只有这一个控件，标题栏与导航都画在网页里，上述问题从根上消失。
///
/// 对应旧项目坑位：
///   P13 COM 线程亲和 — Post() 内建 InvokeRequired→BeginInvoke 自递归，
///       后台线程（日志扫描、状态轮询）直接调也不会抛 E_NOINTERFACE 崩进程。
///   P7  首帧黑屏 — 窗口在导航完成前被挪到屏幕外，由 MainForm 负责归位。
///   J2  数据目录 — exe 目录 → %LOCALAPPDATA%\DshPanel2\WebView2 → %TEMP%，每步真写探针。
/// </summary>
internal sealed class WebHost
{
    private const string VirtualOrigin = "https://panel.local/";

    private readonly WebView2 _view;
    private CoreWebView2? _core;

    /// <summary>数据目录探测成功与否的说明，写进面板日志便于排障。</summary>
    internal string DataFolderNote { get; private set; } = "";

    /// <summary>导航完成（页面可交互）时触发。可能触发多次。</summary>
    internal event Action? Loaded;

    /// <summary>页面发来的 JSON 消息（原样字符串，由上层解析）。</summary>
    internal event Action<string>? MessageReceived;

    internal WebHost()
    {
        _view = new WebView2
        {
            Dock = DockStyle.Fill,
            // 默认 CreateAsync(null, ...) 用系统默认用户数据目录，
            // 多实例时可能互相锁；这里在 InitializeAsync 里显式指定。
        };
    }

    internal WebView2 View => _view;

    /// <summary>
    /// 初始化 WebView2 运行时并调整设置。必须在窗口有句柄之后调用。
    /// 失败时抛出异常，由 MainForm 决定是否进入错误页。
    /// </summary>
    internal async Task InitializeAsync()
    {
        var folder = ResolveDataFolder();

        // DSH_PANEL_DEBUG=1 时开远程调试端口，便于用 CDP 查 DOM / 自动化点按。
        // 生产运行不开，避免面板上多一个本地调试端口。
        var options = new CoreWebView2EnvironmentOptions();
        if (Environment.GetEnvironmentVariable("DSH_PANEL_DEBUG") == "1")
        {
            options.AdditionalBrowserArguments = "--remote-debugging-port=9223";
            Program.LogEvent("已开启远程调试端口 9223（DSH_PANEL_DEBUG=1）");
        }

        var env = await CoreWebView2Environment.CreateAsync(null, folder, options);
        await _view.EnsureCoreWebView2Async(env);
        _core = _view.CoreWebView2;
        if (_core is null) { throw new InvalidOperationException("CoreWebView2 初始化后仍为 null"); }

        // ---- 设置：面板是应用窗口，不是浏览器 ----
        var s = _core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;

        // ---- 内嵌资源：拦截 https://panel.local/* ----
        _core.AddWebResourceRequestedFilter(VirtualOrigin + "*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnWebResourceRequested;

        _core.WebMessageReceived += (_, e) =>
        {
            try
            {
                // WebMessageAsJson 直接给出 JSON 字符串
                MessageReceived?.Invoke(e.WebMessageAsJson);
            }
            catch (Exception ex)
            {
                Program.LogEvent("处理页面消息出错：" + ex.Message);
            }
        };

        _core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                try { Loaded?.Invoke(); }
                catch (Exception ex) { Program.LogEvent("Loaded 回调出错：" + ex.Message); }
            }
            else
            {
                Program.LogEvent($"导航失败：{e.WebErrorStatus}");
            }
        };

        _core.Navigate(BuildStartUrl());
    }

    /// <summary>
    /// 启动地址。默认 index.html；环境变量 DSH_PANEL_PAGE 可指定初始页
    /// （如 settings / log / about），供自动截图验证各页面用。
    /// </summary>
    private static string BuildStartUrl()
    {
        var page = Environment.GetEnvironmentVariable("DSH_PANEL_PAGE");
        if (!string.IsNullOrWhiteSpace(page))
        {
            return VirtualOrigin + "index.html#" + Uri.EscapeDataString(page.Trim());
        }
        return VirtualOrigin + "index.html";
    }

    /// <summary>把界面状态推给网页。任意线程可调（内部自建线程切换，坑 P13）。</summary>
    internal void Post(string json)
    {
        var wv = _view;
        if (wv.IsDisposed) { return; }

        if (wv.InvokeRequired)
        {
            try
            {
                wv.BeginInvoke((Action)(() => Post(json)));
            }
            catch
            {
                // 窗口正在销毁时 BeginInvoke 会抛，忽略即可
            }
            return;
        }

        // 在 UI 线程上了，但仍可能在握手完成前调用
        if (!_ready || _core is null) { _pending = json; return; }

        try { _core.PostWebMessageAsJson(json); }
        catch (Exception ex) { Program.LogEvent("PostWebMessageAsJson 失败：" + ex.Message); }
    }

    private bool _ready;
    private string? _pending;

    /// <summary>页面完成 ready 握手后调用：清 _ready 标记并把最后一条积压推出去。</summary>
    internal void MarkReady()
    {
        _ready = true;
        if (_pending is not null)
        {
            var json = _pending;
            _pending = null;
            Post(json);
        }
    }

    // ==================== 内嵌资源 ====================

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core is null) { return; }

        var uri = new Uri(e.Request.Uri);
        var path = uri.AbsolutePath.TrimStart('/');
        if (path.Length == 0) { path = "index.html"; }

        Stream? stream = FindResource(path);
        if (stream is null)
        {
            var body = $"<html><body style=\"font:14px system-ui;padding:32px\">" +
                       $"<h3>404</h3><p>内嵌资源不存在：<code>{System.Net.WebUtility.HtmlEncode(path)}</code></p></body></html>";
            var bytes = new MemoryStream(Encoding.UTF8.GetBytes(body));
            e.Response = _core.Environment.CreateWebResourceResponse(
                bytes, 404, "Not Found", "Content-Type: text/html; charset=utf-8");
            return;
        }

        var mime = MimeOf(path);
        e.Response = _core.Environment.CreateWebResourceResponse(
            stream, 200, "OK", $"Content-Type: {mime}\r\nCache-Control: no-store");
    }

    /// <summary>
    /// 资源名映射：csproj 里 LogicalName 为 "ui.%(RecursiveDir)%(Filename)%(Extension)"，
    /// 于是 ui/index.html 编译后叫 "ui.index.html"。
    /// </summary>
    private static Stream? FindResource(string path)
    {
        var key = "ui." + path.Replace('/', '.');
        return typeof(WebHost).Assembly.GetManifestResourceStream(key);
    }

    private static string MimeOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js"   => "text/javascript; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg"  => "image/svg+xml",
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico"  => "image/x-icon",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }

    // ==================== 数据目录（坑 J2） ====================

    /// <summary>
    /// WebView2 用户数据目录。三步逐个真写探针，取第一个可用的：
    ///   exe 同级 \WebView2Data → %LOCALAPPDATA%\DshPanel2\WebView2 → %TEMP%\DshPanel2-WebView2
    /// </summary>
    private string ResolveDataFolder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "WebView2Data"),
            Path.Combine(Program.DataDir, "WebView2"),
            Path.Combine(Path.GetTempPath(), "DshPanel2-WebView2"),
        };

        foreach (var dir in candidates)
        {
            if (TryPrepare(dir))
            {
                DataFolderNote = $"{dir}";
                return dir;
            }
        }

        // 全失败：交给运行时自己决定（会有明确异常）
        DataFolderNote = "（全部探测失败，使用运行时默认目录）";
        return "";
    }

    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".probe");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
