using System.Drawing;
using System.Windows.Forms;

namespace DshPanel2;

/// <summary>
/// 托盘图标、菜单与气泡。
///
/// 对应旧项目坑位：
///   P18 菜单展开期间改 <c>NotifyIcon.Text</c> 会让图标消失
///       —— 用 <c>_menuOpen</c> 标志挡住，菜单关闭后补写。
///   P19 <c>new Icon(file)</c> 只拿 ico 里的默认帧（通常 32×32），200% 缩放下会糊
///       —— 窗口图标按 256×256 取，托盘按 SmallIconSize 单独取帧。
///
/// 另外修复旧版缺陷 b：托盘提示原先靠 <c>_lblState.Text == "运行中"</c> 做字符串比较，
/// 纯净模式文案是「运行中（纯净）」→ 永远不相等 → 显示「已停止」。
/// 本版直接用状态快照生成文本。
/// </summary>
internal sealed class TrayManager : IDisposable
{
    private readonly PanelConfig _cfg;
    private readonly ServiceController _svc;
    private readonly Action _showPanel;
    private readonly Action _openLogPage;
    private readonly Action _quit;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;

    private bool _menuOpen;
    private Icon? _trayIcon;
    private string? _safeIcoPath;
    private ServiceSnapshot _last;

    internal TrayManager(PanelConfig cfg, ServiceController svc,
                         Action showPanel, Action openLogPage, Action quit)
    {
        _cfg = cfg;
        _svc = svc;
        _showPanel = showPanel;
        _openLogPage = openLogPage;
        _quit = quit;

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += (_, _) =>
        {
            _menuOpen = true;
            SyncMenuState();
        };
        _menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            // 菜单关闭后补写一次，把展开期间被挡掉的状态变更补上
            SafeTrayText(BuildTrayText(_last));
        };

        BuildMenu();

        _tray = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "DSH 面板",
            Visible = false,
        };

        // 左键单击唤出面板：托盘程序的常规习惯，省得用户琢磨要不要双击
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) { _showPanel(); }
        };

        ApplyIcon(_cfg.IcoFile);
    }

    // ==================== 图标（坑 P19）====================

    /// <summary>
    /// 加载托盘图标。
    ///
    /// 实测结论（三组对照，都做了）：
    ///   · 中文路径的 ico + <c>new Icon(path, 32, 32)</c> → 对象创建成功、不抛异常，
    ///     但 Shell_NotifyIcon 不接受 → 托盘里不出现（或只显一个灰块）
    ///   · 同一 ico 复制到 %TEMP%（ASCII 路径）再加载 → **正常显示**
    ///   · 从 exe 提取 + <c>ToBitmap()/GetHicon()</c> 缩放 → 能显示但**丢 alpha**，是个灰块
    /// 所以优先走「复制到 ASCII 路径」，exe 提取只做备选。
    /// </summary>
    private void ApplyIcon(string icoFile)
    {
        var size = SystemInformation.SmallIconSize;

        // ---- 1a) ico 已在 ASCII 路径：直接加载，最干净 ----
        try
        {
            if (!string.IsNullOrEmpty(icoFile) && File.Exists(icoFile) && IsAsciiPath(icoFile))
            {
                _trayIcon = new Icon(icoFile, size.Width, size.Height);
                _tray.Icon = _trayIcon;
                Program.LogEvent($"托盘图标：直接加载 {icoFile}（{size.Width}×{size.Height}）");
                return;
            }
        }
        catch (Exception ex)
        {
            Program.LogEvent("托盘图标：直接加载失败 - " + ex.Message);
        }

        // ---- 1b) 外部 ico，先搬到 ASCII 路径再加载（保留 alpha，最可靠）----
        try
        {
            if (!string.IsNullOrEmpty(icoFile) && File.Exists(icoFile))
            {
                // 文件名必须唯一：new Icon(file) 会一直持有文件句柄到 Dispose，
                // 用固定名字时第二次启动会撞上「Access to the path is denied」。
                _safeIcoPath = Path.Combine(
                    Path.GetTempPath(), $"DshPanel2-tray-{Environment.ProcessId}.ico");
                File.Copy(icoFile, _safeIcoPath, true);

                _trayIcon = new Icon(_safeIcoPath, size.Width, size.Height);
                _tray.Icon = _trayIcon;
                Program.LogEvent($"托盘图标：复制到 {_safeIcoPath} 后加载（{size.Width}×{size.Height}）");
                return;
            }
            Program.LogEvent("托盘图标：找不到 ico 文件 " + icoFile);
        }
        catch (Exception ex)
        {
            Program.LogEvent("托盘图标：临时路径加载失败 - " + ex.Message);
        }

        // ---- 2) 从 exe 提取（会丢 alpha，可能显示为灰块，但至少有个图标）----
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                using var src = Icon.ExtractAssociatedIcon(exe);
                if (src is not null)
                {
                    _trayIcon = ScaleIcon(src, size);
                    _tray.Icon = _trayIcon;
                    Program.LogEvent("托盘图标：从 exe 提取（可能丢 alpha）");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogEvent("托盘图标：从 exe 提取失败 - " + ex.Message);
        }

        // ---- 3) 系统默认 ----
        _tray.Icon = SystemIcons.Application;
        Program.LogEvent("托盘图标：降级为系统默认图标");
    }

    /// <summary>路径是否全为 ASCII。中文路径下 new Icon(path) 的句柄会被 Shell 拒绝。</summary>
    private static bool IsAsciiPath(string path)
    {
        foreach (var c in path) { if (c > 127) { return false; } }
        return true;
    }

    /// <summary>把任意图标缩放到目标尺寸（ExtractAssociatedIcon 只给默认帧）。</summary>
    private static Icon ScaleIcon(Icon source, Size size)
    {
        using var bmp = new Bitmap(source.ToBitmap(), size);
        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();   // Clone 之后才能安全销毁句柄
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 窗口图标用大尺寸帧（256×256），任务栏与 Alt-Tab 才清晰。
    ///
    /// 必须带 fallback：<c>new Icon(file, 256, 256)</c> 在某些 ico 上取不到对应帧会抛，
    /// 早先版本把异常吞掉直接返回 null，结果窗口用 Windows 默认的「空白文档」图标，
    /// 任务栏上完全看不出这是面板。
    /// </summary>
    internal static Icon? LoadWindowIcon(string icoFile)
    {
        if (string.IsNullOrEmpty(icoFile) || !File.Exists(icoFile))
        {
            Program.LogEvent("窗口图标文件不存在：" + icoFile);
            return null;
        }

        try
        {
            return new Icon(icoFile, 256, 256);
        }
        catch (Exception ex)
        {
            Program.LogEvent("按 256×256 取图标帧失败，改用默认帧：" + ex.Message);
            try
            {
                return new Icon(icoFile);
            }
            catch (Exception ex2)
            {
                Program.LogEvent("加载窗口图标彻底失败：" + ex2.Message);
                return null;
            }
        }
    }

    // ==================== 菜单 ====================

    private ToolStripMenuItem _miStart = null!;
    private ToolStripMenuItem _miStop = null!;

    private void BuildMenu()
    {
        var miShow = new ToolStripMenuItem("打开面板");
        miShow.Click += (_, _) => _showPanel();

        var miWeb = new ToolStripMenuItem("打开网页 DSH");
        miWeb.Click += (_, _) => _svc.OpenWebOnly();

        _miStart = new ToolStripMenuItem("启动 DSH 服务");
        _miStart.Click += (_, _) => _svc.OpenDsh();

        var miRestart = new ToolStripMenuItem("重启 DSH");
        miRestart.Click += (_, _) => _svc.HandleCommand("restart");

        _miStop = new ToolStripMenuItem("停止 DSH");
        _miStop.Click += (_, _) => _svc.HandleCommand("stop");

        var miLog = new ToolStripMenuItem("运行日志");
        miLog.Click += (_, _) => { _showPanel(); _openLogPage(); };

        var miExit = new ToolStripMenuItem("退出面板");
        miExit.Click += (_, _) => _quit();

        _menu.Items.Add(miShow);
        _menu.Items.Add(miWeb);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_miStart);
        _menu.Items.Add(miRestart);
        _menu.Items.Add(_miStop);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(miLog);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(miExit);
    }

    /// <summary>按当前状态置灰菜单项。菜单展开时调用，不碰 Text（坑 P18）。</summary>
    private void SyncMenuState()
    {
        var running = _svc.IsRunning();
        _miStart.Enabled = !running;
        _miStop.Enabled = running;
    }

    // ==================== 状态与提示 ====================

    internal void UpdateState(ServiceSnapshot snap)
    {
        _last = snap;
        SafeTrayText(BuildTrayText(snap));
    }

    /// <summary>
    /// 托盘提示文本。NotifyIcon.Text 上限 63 字符，这里不会超。
    /// 用状态快照生成，不做字符串比较（修缺陷 b）。
    /// </summary>
    private static string BuildTrayText(ServiceSnapshot s)
    {
        if (s.Starting) { return s.Restarting ? "DSH 正在重启…" : "DSH 启动中…"; }
        if (s.Running) { return s.Clean ? "DSH 运行中（纯净）" : "DSH 运行中"; }
        if (s.FailMsg is not null) { return "DSH 启动失败"; }
        return "DSH 已停止";
    }

    /// <summary>
    /// 安全写 Text：菜单展开期间写它会让托盘图标消失（坑 P18），所以直接跳过。
    /// </summary>
    private void SafeTrayText(string text)
    {
        if (_menuOpen) { return; }
        try
        {
            if (_tray.Text != text) { _tray.Text = text; }
        }
        catch { }
    }

    internal void ShowBalloon(string title, string text, bool warning = true)
    {
        try
        {
            _tray.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch { }
    }

    internal void Show()
    {
        _tray.Visible = true;
        _tray.Text = BuildTrayText(_last);
        Program.LogEvent($"托盘图标已显示（Icon={(_tray.Icon is null ? "null" : "ok")}, Visible={_tray.Visible}）");
    }

    internal void Hide()
    {
        _tray.Visible = false;
    }

    public void Dispose()
    {
        try
        {
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _trayIcon?.Dispose();
            _trayIcon = null;

            // 图标对象释放后才能删掉临时 ico（它一直持有文件句柄）
            if (!string.IsNullOrEmpty(_safeIcoPath) && File.Exists(_safeIcoPath))
            {
                try { File.Delete(_safeIcoPath); } catch { }
            }
        }
        catch { }
    }
}
