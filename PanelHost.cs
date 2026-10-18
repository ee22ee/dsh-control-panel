using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshPanel
{
    /// <summary>
    /// WebView2 内容区宿主。
    ///
    /// 为什么要换掉 WinForms 自绘内容区：
    ///   WinForms 每个控件都是一个独立 HWND（微软 Control.Handle 文档：「The value of the
    ///   Handle property is a Windows HWND」），整窗画面是这些独立窗口被系统逐个合成出来的，
    ///   所以「改主题时一半新一半旧」「启动时只渲染一部分」是架构决定的，调任何参数都救不回
    ///   （WS_EX_COMPOSITED 也只能靠 100ms 超时兜底，实测 10FPS 且启动会透出后面的窗口）。
    ///   WebView2 整个内容区只有一个 Chromium 合成表面，换主题就是改一个 CSS 变量，天然原子。
    ///
    /// 资源加载：ui/ 下所有文件在编译期用 /resource 打进 exe，运行时拦截
    /// https://panel.local/* 直接把内存流还给 WebView2 —— 不解压、不落盘，保持单文件绿色。
    /// </summary>
    internal sealed class PanelHost : UserControl
    {
        public const string VirtualOrigin = "https://panel.local/";

        private WebView2 _wv;
        private bool     _ready;
        private string   _pendingNav;     // 页面还没就绪时要跳的地址，就绪后补发

        /// <summary>页面里发来的消息（原始 JSON 文本）。</summary>
        public event Action<string> MessageFromJs;

        /// <summary>首次导航完成。</summary>
        public event Action Loaded;

        public bool IsReady { get { return _ready; } }

        /// <summary>调试用：DPI 缩放的实际生效情况（写进面板日志排查布局问题）。</summary>
        public string DiagZoom = "?";

        public PanelHost()
        {
            BackColor = Color.FromArgb(15, 18, 24);

            _wv = new WebView2();
            _wv.Dock = DockStyle.Fill;
            _wv.DefaultBackgroundColor = Color.FromArgb(15, 18, 24);   // 首帧底衬，避免白闪
            Controls.Add(_wv);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_wv != null) { _wv.Dispose(); } }
                catch { }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 给 WebView2 挑一个真的能写进去的用户数据目录。
        /// 顺序：exe 同目录 → %LOCALAPPDATA%\DshPanel → %TEMP%。
        /// 每一步都真写一个探针文件来验证（只看 Directory.Exists 会骗人）。
        /// </summary>
        private static string PickDataDir(out string trace)
        {
            var cand = new List<string>();
            try { cand.Add(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "WebView2Data")); } catch { }
            try { cand.Add(Path.Combine(PanelConfig.DataDir, "WebView2")); } catch { }
            try { cand.Add(Path.Combine(Path.GetTempPath(), "DshPanel-WebView2")); } catch { }

            StringBuilder log = new StringBuilder();
            for (int i = 0; i < cand.Count; i++)
            {
                string d = cand[i];
                if (string.IsNullOrEmpty(d)) { continue; }
                try
                {
                    Directory.CreateDirectory(d);
                    string probe = Path.Combine(d, ".probe");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    trace = log.ToString();
                    return d;
                }
                catch (Exception ex)
                {
                    log.Append(d).Append(" → ").Append(ex.GetType().Name).Append(": ")
                       .Append(ex.Message).Append('\n');
                }
            }
            trace = log.ToString();
            return "";
        }

        // ------------------------------------------------------------------ 初始化

        public async Task<bool> InitAsync()
        {
            try
            {
                string trace;
                string dataDir = PickDataDir(out trace);
                if (dataDir.Length == 0)
                {
                    LastError = "找不到可写的 WebView2 数据目录：\n" + trace;
                    return false;
                }

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dataDir, null);
                await _wv.EnsureCoreWebView2Async(env);

                CoreWebView2 core = _wv.CoreWebView2;

                // 【DPI】面板整套自绘控件是「手动乘缩放系数」的架构：窗口客户区在 200%
                // 缩放下是 2004×1564 设备像素，控件 Size 也按设备像素给。而 WebView2 把控件
                // Size 当 CSS px、再按 RasterizationScale(2) 渲染 —— 页面被放大一倍、右侧被
                // 裁掉（实测 innerWidth=1810，而 46px 的行显示成 92px）。
                // 用 ZoomFactor 抵消：视口 CSS 宽变成 1810/2≈905，每 CSS px 占 2 设备像素，
                // 字号与间距的视觉尺寸才和 WinForms 版一致。
                ApplyDpiZoom();   // 用面板的 Theme.S 校正（DeviceDpi 在本程序里恒为 96）
                try { _wv.CoreWebView2.Settings.IsZoomControlEnabled = false; } catch { }
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled          = false;
                core.Settings.IsZoomControlEnabled        = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled   = false;
                core.Settings.IsGeneralAutofillEnabled    = false;

                core.AddWebResourceRequestedFilter(VirtualOrigin + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnResourceRequested;
                core.WebMessageReceived   += OnWebMessage;
                core.NavigationCompleted  += OnNavigationCompleted;

                core.Navigate(VirtualOrigin + "index.html");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                Exception inner = ex.InnerException;
                while (inner != null)
                {
                    LastError += "\n" + inner.GetType().Name + ": " + inner.Message;
                    inner = inner.InnerException;
                }
                return false;
            }
        }

        public string LastError = "";

        /// <summary>初始化失败（多半是本机没装 WebView2 Runtime）。</summary>
        public event Action<string> Failed;

        /// <summary>在窗口显示之后调用：异步初始化并把页面载入。</summary>
        public async void Start()
        {
            bool ok = await InitAsync();
            if (!ok)
            {
                Action<string> f = Failed;
                if (f != null) { f(LastError); }
            }
        }

        // ------------------------------------------------------------------ 资源

        private void OnResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            if (path.Length == 0) { path = "index.html"; }

            string resName = "ui." + path.Replace('/', '.');
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);

            if (stream == null)
            {
                byte[] miss = Encoding.UTF8.GetBytes(
                    "<body style='background:#0f1218;color:#e5484d;font:14px sans-serif;padding:24px'>" +
                    "404 &nbsp;" + path + "</body>");
                e.Response = _wv.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(miss), 404, "Not Found", "Content-Type: text/html; charset=utf-8");
                return;
            }

            e.Response = _wv.CoreWebView2.Environment.CreateWebResourceResponse(
                stream, 200, "OK", "Content-Type: " + MimeOf(path) + "; charset=utf-8");
        }

        private static string MimeOf(string path)
        {
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) { return "text/html"; }
            if (path.EndsWith(".css",  StringComparison.OrdinalIgnoreCase)) { return "text/css"; }
            if (path.EndsWith(".js",   StringComparison.OrdinalIgnoreCase)) { return "application/javascript"; }
            if (path.EndsWith(".svg",  StringComparison.OrdinalIgnoreCase)) { return "image/svg+xml"; }
            if (path.EndsWith(".png",  StringComparison.OrdinalIgnoreCase)) { return "image/png"; }
            if (path.EndsWith(".ico",  StringComparison.OrdinalIgnoreCase)) { return "image/x-icon"; }
            return "text/plain";
        }

        // ------------------------------------------------------------------ 通信

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            ApplyDpiZoom();
            _ready = true;
            Action h = Loaded;
            if (h != null) { h(); }
        }

        /// <summary>
        /// 【DPI】把 WebView2 的缩放对齐到面板的「设备像素」坐标系。
        /// 面板自绘控件在 200% 缩放下 ClientSize = 2004×1564（设备像素），而 WebView2
        /// 把控件尺寸当 CSS px、再按 RasterizationScale 放大，于是页面大了一倍、右侧被裁。
        /// ZoomFactor = DPI 系数后：视口 CSS 宽 = 1810/2 ≈ 905，每 CSS px 占 2 设备像素。
        /// 初始化后与每次导航完成后都设一次（避免首次设置被 WebView2 内部重置换掉）。
        /// </summary>
        private void ApplyDpiZoom()
        {
            try
            {
                if (_wv == null) { DiagZoom = "no-ctl"; return; }

                // 【官方口径 · CoreWebView2BoundsMode】
                //   UseRasterizationScale（默认）= Bounds 是「逻辑像素」，
                //                                物理尺寸 = Bounds × RasterizationScale
                //   UseRawPixels               = Bounds 就是「物理像素」
                // 这里保持默认的 UseRasterizationScale：Host 的 Location/Size 用逻辑像素，
                // WebView2 自己乘 RasterizationScale(2) 得到物理尺寸 —— 位置和大小都对得上。
                // （试过 UseRawPixels：位置会被忽略、WebView2 从父窗口左上角铺开，盖住导航。）
                DiagZoom = "bounds=" + (GetControllerProp("BoundsMode") ?? "?")
                         + " rs=" + (GetControllerProp("RasterizationScale") ?? "?")
                         + " host=" + Bounds
                         + " ctl=" + (_wv == null ? "-" : _wv.Bounds.ToString())
                         + " wnd=" + WinRect(_wv == null ? IntPtr.Zero : _wv.Handle);
            }
            catch (Exception ex)
            {
                DiagZoom = "err:" + ex.GetType().Name + ":" + ex.Message;
            }
        }

        /// <summary>
        /// 直接告诉 WebView2 用哪块「物理像素」矩形。BoundsMode 默认 UseRawPixels 时
        /// controller.Bounds 就是物理像素，绕开 WinForms 控件定位（实测控件 Location
        /// 会被 WebView2 内部换算错，页面老是偏到导航栏上）。
        /// </summary>
        public void ApplyPhysicalBounds(int x, int y, int w, int h)
        {
            _physBounds = new Rectangle(x, y, w, h);
            try
            {
                System.Reflection.FieldInfo fi = typeof(WebView2).GetField(
                    "_coreWebView2Controller",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                object ctrl = (fi != null && _wv != null) ? fi.GetValue(_wv) : null;
                if (ctrl == null) { return; }
                System.Reflection.PropertyInfo pb = ctrl.GetType().GetProperty("Bounds");
                if (pb != null) { pb.SetValue(ctrl, _physBounds, null); }
            }
            catch { }
        }

        private Rectangle _physBounds = Rectangle.Empty;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        /// <summary>诊断用：WebView2 那个子窗口在屏幕上的真实矩形。</summary>
        private static string WinRect(IntPtr h)
        {
            if (h == IntPtr.Zero) { return "-"; }
            RECT r;
            if (!GetWindowRect(h, out r)) { return "fail"; }
            return (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "@" + r.Left + "," + r.Top;
        }

        private object GetControllerProp(string name)
        {
            try
            {
                System.Reflection.FieldInfo fi = typeof(WebView2).GetField(
                    "_coreWebView2Controller",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                object ctrl = (fi != null && _wv != null) ? fi.GetValue(_wv) : null;
                if (ctrl == null) { return null; }
                System.Reflection.PropertyInfo pi = ctrl.GetType().GetProperty(name);
                return (pi != null) ? pi.GetValue(ctrl, null) : null;
            }
            catch { return null; }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json = e.WebMessageAsJson;
            Action<string> h = MessageFromJs;
            if (h != null) { h(json); }
        }

        /// <summary>C# → 页面。调用方负责保证 json 是合法 JSON。</summary>
        public void Post(string json)
        {
            if (!_ready || _wv == null || _wv.CoreWebView2 == null) { _pending = json; return; }
            try { _wv.CoreWebView2.PostWebMessageAsJson(json); }
            catch { }
        }

        private string _pending;

        /// <summary>页面就绪后补发最后一次被缓存的消息。</summary>
        public void FlushPending()
        {
            if (_pending == null) { return; }
            string s = _pending;
            _pending = null;
            Post(s);
        }

        /// <summary>导航到某个页面（单页应用内部路由，仍走 postMessage 更简单，这里留作备用）。</summary>
        public void Navigate(string hash)
        {
            if (!_ready) { _pendingNav = hash; return; }
            try { _wv.CoreWebView2.Navigate(VirtualOrigin + "index.html" + (hash == null ? "" : "#" + hash)); }
            catch { }
        }

        /// <summary>开发期用：打开 DevTools（右键菜单里给个入口）。</summary>
        public void OpenDevTools()
        {
            try { if (_wv != null && _wv.CoreWebView2 != null) { _wv.CoreWebView2.OpenDevToolsWindow(); } }
            catch { }
        }

        /// <summary>从资源里读一个文件（给需要内联的场景用）。</summary>
        public static string ReadResourceText(string path)
        {
            string resName = "ui." + path.Replace('/', '.');
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
            {
                if (s == null) { return null; }
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) { return r.ReadToEnd(); }
            }
        }
    }
}
