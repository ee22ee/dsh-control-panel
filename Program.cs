using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DshPanel2;

/// <summary>
/// 入口：DPI 初始化、单实例互斥、全局异常兜底、启动主窗口。
///
/// 对应旧项目坑位：
///   P17 单实例与孤儿 Mutex — 命名 Mutex 的句柄可能被别的进程继承后不释放，
///       上个进程被强杀时 Mutex 还在，此时直接退出会导致「面板再也打不开」。
///       所以 createdNew==false 时必须用进程名二次确认，无同名进程则忽略孤儿 Mutex 继续运行。
///   P1/P2/P3 高 DPI — 本版由 app.manifest 的 PerMonitorV2 统一接管，代码里不再做任何
///       DPI 推算（旧版因为 DeviceDpi 恒返回 96 而写了一整套补偿，正是连带 bug 的源头）。
/// </summary>
internal static class Program
{
    internal const string AppTitle  = "DSH 面板";
    internal const string MutexName = "DshPanel2_SingleInstance_v1";
    internal const string ProcessName = "DshPanel2";

    /// <summary>
    /// 「把面板显示出来」的跨进程消息。
    ///
    /// 为什么不让新实例直接 <c>ShowWindow(hwnd, SW_SHOW)</c>：WinForms 的 <c>Form.Hide()</c>
    /// 会把内部的 Visible 置为 false，外部再 ShowWindow 只是把 WS_VISIBLE 加回去，
    /// WinForms 之后仍按「不可见」处理，实测窗口不出现。让**已有实例自己**调 Show() 才可靠。
    /// </summary>
    /// <summary>
    /// 「把面板显示出来」的跨进程信号名。
    ///
    /// 试过两条路，都不行：
    ///   1. 新实例直接 <c>ShowWindow(hwnd, SW_SHOW)</c> —— WinForms 的 Hide() 会让内部
    ///      Visible=false，外部 Show 之后窗口仍不出现；更糟的是 EnumWindows 会先抓到
    ///      WinForms 自动创建的 <c>.NET-BroadcastEventWindow</c> 隐藏窗口，把它显示出来。
    ///   2. <c>PostMessage</c> 自定义消息 —— 实测对端 WndProc 收不到（句柄/消息循环时序问题）。
    /// 所以改成命名 EventWaitHandle：已有实例起后台线程 WaitOne，新实例 Set 一下即可。
    /// 这条路径不依赖窗口句柄，也不要求窗口可见。
    /// </summary>
    internal const string ShowEventName = "DshPanel2.ShowPanel.v1";

    /// <summary>通知已在运行的实例显示窗口。返回 false 表示没有实例在等这个信号。</summary>
    internal static bool TrySignalExisting()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // 内置自检：不建窗口、不起 WebView2，只验纯逻辑。
        // 退出码 0 = 全通过，1 = 有失败；详细结果写 selftest.log。
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        LogEvent($"===== 启动 pid={Environment.ProcessId} " +
                 $"local={Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)} " +
                 $"exe={AppContext.BaseDirectory} =====");

        // 必须在创建任何窗口之前。manifest 里已声明 PerMonitorV2，
        // 若此处返回 false 属正常（说明由 manifest 接管，不会叠加）。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool createdNew;
        var mutex = new Mutex(true, MutexName, out createdNew);

        if (!createdNew)
        {
            // 先尝试通知已在运行的实例把窗口显示出来（命名事件，跨进程可靠）
            if (TrySignalExisting())
            {
                LogEvent("已通知既有实例显示窗口，本次启动退出");
                return;
            }

            // 事件不存在：再确认有没有同名进程
            if (HasOtherInstance(out var other))
            {
                LogEvent($"已有面板进程 PID {other?.Id} 在运行但未响应显示信号，本次启动退出");
                return;
            }

            // 走到这里说明 Mutex 是孤儿（没有同名进程活着），继续运行，
            // 并继续持有句柄，直到本进程退出。
            LogEvent("发现孤儿 Mutex（无同名进程），已接管继续启动");
        }

        // 兜底：UI 线程异常不会静默杀进程
        Application.ThreadException += (_, e) => Fatal("UI 线程异常", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal("未处理异常", e.ExceptionObject as Exception);

        try
        {
            Application.Run(new MainForm());
        }
        finally
        {
            GC.KeepAlive(mutex);
        }

        LogEvent("面板退出");
    }

    /// <summary>
    /// 是否已有别的面板实例在运行（排除自己）。
    /// 查不到进程列表时返回 false —— 宁可多开一次，也不要在权限受限时误拒绝启动。
    /// </summary>
    private static bool HasOtherInstance(out Process? other)
    {
        other = null;
        try
        {
            var list = Process.GetProcessesByName(ProcessName)
                              .Where(p => p.Id != Environment.ProcessId)
                              .ToArray();
            if (list.Length == 0) { return false; }

            // 优先取有主窗口的那个（可能还有正在退出的残留进程）
            other = list.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? list[0];
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>记录致命异常。日志路径与主数据目录一致。</summary>
    private static void Fatal(string where, Exception? ex)
    {
        try
        {
            LogEvent($"[{where}] {ex}");
            MessageBox.Show(
                $"{where}：\n{ex?.Message}\n\n详情已写入 crash.log",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 兜底路径也不能再抛
        }
    }

    /// <summary>
    /// 写一行面板日志。首选 %LOCALAPPDATA%\DshPanel2\panel.log，
    /// 失败时回落到 exe 同级目录——启动早期调它时 %LOCALAPPDATA% 可能还没就绪，
    /// 只有回落到 exe 目录才能保证「为什么没日志」这件事本身有记录。
    /// </summary>
    internal static void LogEvent(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}";
        var failures = new List<string>();
        var wrote = false;

        foreach (var dir in new[] { DataDir, AppContext.BaseDirectory })
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) { failures.Add("(空目录名)"); continue; }
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "panel.log"), line, Encoding.UTF8);
                wrote = true;
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"{dir} -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 只要有目录写失败，就单独留一份诊断，避免「悄悄回落到 exe 目录」掩盖真实原因
        if (!wrote || failures.Count > 0)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "panel.log.failure"),
                    $"{line.TrimEnd()}{Environment.NewLine}" +
                    $"    写成功={wrote} 失败={string.Join(" ;; ", failures)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // 真写不了就算了
            }
        }
    }

    /// <summary>
    /// 面板自己的数据目录，由 <see cref="Paths"/> 统一探测并带回退链：
    /// %LOCALAPPDATA%\DshPanel2 → exe 同级 DshPanel2-data → %TEMP%\DshPanel2。
    /// 与旧面板的 %LOCALAPPDATA%\DSH 完全隔离，两个版本可以同时存在。
    /// </summary>
    internal static string DataDir => Paths.DataDir;

    // ---- Win32 ----
    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
