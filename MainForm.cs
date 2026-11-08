using System.Runtime.InteropServices;

namespace DshPanel2;

/// <summary>
/// 主窗口：无边框 + 单个铺满的 WebView2，标题栏与导航由网页绘制。
///
/// 对应旧项目坑位：
///   P7  首帧黑屏   — 窗口先摆到 (-32000,-32000)，导航完成后再居中；另有 4 秒兜底。
///                    禁止 Opacity 淡入（WebView2 是独立子 HWND，不受分层 alpha 影响）。
///   P20 运行期不碰 ShowInTaskbar — 它重建句柄，从 FormClosing 里调会重入杀进程。
///   P21 不挂 Resize — 句柄生命周期敏感期碰可见性会重入。
///   P22 FormBorderStyle.None 不带 WS_MINIMIZEBOX — 不补的话点任务栏图标「没反应」。
/// </summary>
internal sealed class MainForm : Form
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_SYSMENU = 0x00080000;

    private readonly WebHost _host;
    private readonly PanelConfig _cfg;
    private readonly ServiceController _svc;
    private readonly UiBridge _bridge;
    private readonly TrayManager _tray;

    private System.Windows.Forms.Timer? _tick;
    private System.Windows.Forms.Timer? _revealGuard;
    private bool _revealed;
    private bool _quitting;
    private bool _trayHintShown;
    private EventWaitHandle? _showEvent;
    private Thread? _showWaiter;
    private Panel? _errorPanel;
    private Icon? _appIcon;

    internal MainForm()
    {
        // ---------- 1. 配置 ----------
        var cfgPath = Paths.PickConfigPath();
        _cfg = PanelConfig.Load(cfgPath);
        Program.LogEvent($"配置文件：{cfgPath}");
        Program.LogEvent($"数据目录：{Paths.DataDirNote}");

        // 缺陷 i：把注册表里的真实自启状态校正回配置
        AutoStart.SyncFromRegistry(_cfg);

        // ---------- 2. 窗口 ----------
        Text = _cfg.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;

        // 关键：PerMonitorV2 下 Form 默认 AutoScaleMode.Font 会按 DPI 再缩放一次 Bounds，
        // 实测 1075 宽被撑到 1580、超出屏幕被裁——与旧版 P2/P3「界面被放大两倍」同源。
        // 本窗口唯一子控件是 WebView2，DPI 由它自己的 RasterizationScale 处理。
        AutoScaleMode = AutoScaleMode.None;

        // 只在构造期设一次；运行期绝不碰（坑 P20）
        ShowInTaskbar = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(20, 22, 27);

        // 窗口/任务栏图标用 256 帧，托盘图标由 TrayManager 单独取帧（坑 P19）
        _appIcon = TrayManager.LoadWindowIcon(_cfg.IcoFile);
        if (_appIcon is not null) { Icon = _appIcon; }

        // 首帧前挪到屏幕外（坑 P7）
        var (w, h) = DesiredSize();
        Bounds = new Rectangle(-32000, -32000, w, h);

        // ---------- 3. 渲染面 ----------
        _host = new WebHost();
        _host.MessageReceived += OnWebMessage;
        Controls.Add(_host.View);

        // ---------- 4. 服务控制与桥 ----------
        _svc = new ServiceController(_cfg, OnServiceStateChanged);

        _bridge = new UiBridge(_host, _cfg, _svc)
        {
            DragRequested = BeginDrag,
            MinimizeRequested = () => WindowState = FormWindowState.Minimized,
            CloseRequested = HandleCloseRequest,
        };

        // 服务日志增量 → 网页日志页
        _svc.OnLogText = text => _bridge.PushLog(text);

        // ---------- 5. 托盘 ----------
        _tray = new TrayManager(_cfg, _svc, ShowPanel, () => _bridge.PushNav("log"), QuitFromTray);
        _svc.OnBalloon = (title, msg) => _tray.ShowBalloon(title, msg);
    }

    // ==================== 生命周期 ====================

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_MINIMIZEBOX | WS_SYSMENU;   // 坑 P22
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            await _host.InitializeAsync();
            Program.LogEvent("WebView2 已就绪，数据目录：" + _host.DataFolderNote);
            _host.Loaded += RevealWindow;
        }
        catch (Exception ex)
        {
            Program.LogEvent("WebView2 初始化失败：" + ex);
            ShowErrorPanel(ex.Message);
        }
        finally
        {
            // 兜底：无论导航成败，4 秒后必须让窗口出现在屏幕上，
            // 否则 WebView2 起不来时窗口会永远停在屏幕外。
            StartRevealGuard();
            StartTick();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 显式发 WM_SETICON。
        // 光设 Form.Icon 在无边框窗口上不可靠：WinForms 可能只在 CreateHandle 前记录字段，
        // 不会真的发 WM_SETICON，任务栏按钮就一直是「空白文档」默认图标。
        ApplyWindowIcon();

        _tray.Show();
        _tray.UpdateState(_svc.Snapshot());
        StartShowWaiter();

        // 用户开了「启动后最小化到托盘」：窗口出现后立刻收走，
        // 但保留托盘图标，否则用户会以为程序没启动。
        if (_cfg.AutoTray)
        {
            Program.LogEvent("按设置自动收进托盘");
            HideToTray();
        }
    }

    /// <summary>
    /// 起一个后台线程等「显示」信号，让第二个实例能把本窗口唤出来。
    ///
    /// 为什么不用窗口消息：实测 PostMessage 到隐藏窗口，对端 WndProc 收不到；
    /// 而外部 ShowWindow 又不可靠（WinForms 内部 Visible 仍为 false，且容易把
    /// .NET-BroadcastEventWindow 那个隐藏窗口显示出来）。命名事件不依赖窗口句柄。
    /// </summary>
    private void StartShowWaiter()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        }
        catch (Exception ex)
        {
            Program.LogEvent("创建显示信号失败（第二个实例将无法唤出本窗口）：" + ex.Message);
            return;
        }

        _showWaiter = new Thread(() =>
        {
            while (!_quitting)
            {
                try
                {
                    var ev = _showEvent;
                    if (ev is null) { break; }
                    if (!ev.WaitOne(500)) { continue; }
                    if (_quitting) { break; }

                    Program.LogEvent("收到跨进程显示请求，唤出窗口");
                    BeginInvoke((Action)ShowPanel);
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ShowWaiter",
        };
        _showWaiter.Start();
    }

    private void StartTick()
    {
        _tick = new System.Windows.Forms.Timer { Interval = 1500 };
        _tick.Tick += (_, _) => _svc.Tick();
        _tick.Start();
    }

    // ==================== 窗口图标 ====================

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    /// <summary>
    /// 把图标真正推到窗口上。
    ///
    /// 只设 <c>Form.Icon</c> 不够：无边框窗口下 WinForms 有时只把它存成字段，
    /// 不会发 WM_SETICON，任务栏按钮就会一直是 Windows 的「空白文档」默认图标。
    /// 这里同时设大/小两个图标，并在 OnShown（Handle 已就绪）之后调用。
    /// </summary>
    private void ApplyWindowIcon()
    {
        if (_appIcon is null) { return; }

        try
        {
            Icon = _appIcon;
            SendMessage(Handle, WM_SETICON, ICON_BIG, _appIcon.Handle);
            SendMessage(Handle, WM_SETICON, ICON_SMALL, _appIcon.Handle);
            Program.LogEvent("窗口图标已应用：" + _cfg.IcoFile);
        }
        catch (Exception ex)
        {
            Program.LogEvent("应用窗口图标失败：" + ex.Message);
        }
    }

    /// <summary>服务状态变了：更新托盘文本并把快照推给网页。任意线程可调。</summary>
    private void OnServiceStateChanged()
    {
        if (IsDisposed) { return; }

        if (InvokeRequired)
        {
            try { BeginInvoke((Action)OnServiceStateChanged); } catch { }
            return;
        }

        var snap = _svc.Snapshot();
        _tray.UpdateState(snap);
        _bridge.PushState();
    }

    private void OnWebMessage(string json) => _bridge.HandleMessage(json);

    // ==================== 窗口动作 ====================

    /// <summary>网页标题栏拖动：转发成 WM_NCLBUTTONDOWN + HTCAPTION。</summary>
    internal void BeginDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    /// <summary>从托盘唤出面板。</summary>
    private void ShowPanel()
    {
        try
        {
            if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; }
            Show();
            BringToFront();
            Activate();
        }
        catch (Exception ex)
        {
            Program.LogEvent("唤出面板失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 点 X / 网页关闭按钮。
    /// 默认**直接退出**（用户对「点 X 就该关掉」的预期最强）；
    /// 只有配置里勾了「关闭时收进托盘」才隐藏到托盘。
    /// </summary>
    private void HandleCloseRequest()
    {
        if (_cfg.CloseToTray)
        {
            HideToTray();
            return;
        }
        QuitFromTray();
    }

    /// <summary>收进托盘。只有开了 CloseToTray 才会走到这里。</summary>
    private void HideToTray()
    {
        Hide();
        _tray.Show();
        Program.LogEvent("窗口收进托盘");

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloon("面板已收进托盘",
                "双击桌面「DSH 控制面板」图标可再次打开；要彻底退出请用托盘菜单。",
                warning: false);
        }
    }

    /// <summary>托盘菜单「退出面板」/ 默认关闭行为：真正结束进程（会按配置决定是否停服务）。</summary>
    private void QuitFromTray()
    {
        if (_quitting) { return; }
        _quitting = true;
        _tick?.Stop();
        _tray.Hide();
        _svc.Quit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_quitting)
        {
            base.OnFormClosing(e);
            return;
        }

        // 只有「用户点 X」且配置要求时才拦下来收托盘
        if (e.CloseReason == CloseReason.UserClosing && _cfg.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // 真正关闭：清理放到 OnFormClosed
        _quitting = true;
        _tick?.Stop();
        _tray.Hide();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        try { _showEvent?.Dispose(); } catch { }
        _showEvent = null;

        // 窗口已关，按配置停服务（同步执行，进程马上就退出）
        if (_cfg.StopWithPanel && _svc.IsRunning())
        {
            _svc.StopForShutdown();
        }
    }

    // ==================== 首帧隐藏三件套（坑 P7）====================

    private void RevealWindow()
    {
        if (_revealed) { return; }
        _revealed = true;
        StopRevealGuard();
        CenterOnScreen();
        _host.View.Focus();
    }

    private void StartRevealGuard()
    {
        _revealGuard = new System.Windows.Forms.Timer { Interval = 4000 };
        _revealGuard.Tick += (_, _) =>
        {
            StopRevealGuard();
            if (!_revealed)
            {
                _revealed = true;
                Program.LogEvent("导航未在 4 秒内完成，强制显示窗口");
                CenterOnScreen();
            }
        };
        _revealGuard.Start();
    }

    private void StopRevealGuard()
    {
        if (_revealGuard is null) { return; }
        _revealGuard.Stop();
        _revealGuard.Dispose();
        _revealGuard = null;
    }

    private void CenterOnScreen()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            wa.Left + (wa.Width - Width) / 2,
            wa.Top + (wa.Height - Height) / 2);
    }

    /// <summary>
    /// WebView2 完全起不来时（没装 Runtime / 数据目录不可写）用一块面板顶替。
    /// 这是本版唯一的 WinForms 自绘内容，替代了旧版「四个旧页面当降级界面」的做法。
    /// </summary>
    private void ShowErrorPanel(string detail)
    {
        _host.View.Visible = false;

        _errorPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 22, 27) };
        var tip = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(226, 230, 238),
            Font = new Font("Microsoft YaHei UI", 10f),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "面板界面无法加载\n\n" +
                   "原因：" + detail + "\n\n" +
                   "常见的三种情况：\n" +
                   "  · 已经有一个面板在运行（结束所有 DshPanel2.exe 后重试）\n" +
                   "  · WebView2 Runtime 未安装或已损坏\n" +
                   "    https://developer.microsoft.com/microsoft-edge/webview2/\n" +
                   "  · WebView2 用户数据目录被占用或损坏（删掉 DshPanel2-data 与 WebView2Data 后重试）\n\n" +
                   "详细日志：" + Path.Combine(Paths.DataDir, "panel.log"),
        };
        _errorPanel.Controls.Add(tip);
        Controls.Add(_errorPanel);
        _errorPanel.BringToFront();
    }

    /// <summary>
    /// 初始窗口尺寸。
    ///
    /// 注意这里拿到的是**物理像素**（PerMonitorV2 进程里 Screen.WorkingArea 不做 DPI 虚拟化）：
    /// 本机屏幕 3072×1920、缩放 200%，工作区物理 3072×1872。
    /// 之前按「逻辑像素」的思路给了 1400 的上限，结果物理只拿到 1400 宽 = 700 CSS px，
    /// 窗口在 3072 宽的屏上显得又小又偏。正确做法是按比例给物理像素，不设像素级上限。
    /// </summary>
    private static (int W, int H) DesiredSize()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var w = Math.Max((int)(wa.Width * 0.74), 900);
        var h = Math.Max((int)(wa.Height * 0.84), 620);
        return (w, h);
    }

    // ---- Win32 ----
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
}
