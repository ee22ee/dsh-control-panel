using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DshPanel2;

/// <summary>
/// 打开 DSH 网页：app 模式用 Chromium 系的 <c>--app</c> 开无地址栏独立窗口，
/// tab 模式交回系统默认浏览器。
///
/// 对应旧项目坑位：
///   P26 端口就绪 ≠ 令牌地址就绪 — dsh web 先让端口进 LISTENING，过一会儿才在日志里
///       打印带进程令牌的启动地址。端口一就绪就拿裸地址开浏览器会因为缺令牌而 401，
///       现象是「窗口打开了但连不上」。所以令牌地址出来之前只等不开（最多 20 轮）。
///   P27 <c>--user-data-dir</c> 是独占的 — 多实例共用同一目录时，后启动的进程会把
///       命令行交给已有实例然后自己立刻退出。因此 app 模式必须配独立 profile，
///       并且**任何「靠浏览器进程是否存活来判断窗口是否关闭」的逻辑都不许写**。
/// </summary>
internal sealed class BrowserLauncher
{
    private readonly PanelConfig _cfg;

    internal BrowserLauncher(PanelConfig cfg) => _cfg = cfg;

    // ==================== 打开 ====================

    /// <summary>
    /// 打开网址。
    /// 返回 (是否已打开, 是否走了 --app 独立窗口, 方式说明)；未打开时最后一个是原因。
    /// </summary>
    internal (bool Opened, bool IsApp, string Detail) Open(string url)
    {
        // 注意：空字符串也必须当成「没配置」。
        // 之前写成 `if (!string.IsNullOrEmpty(exe) && !File.Exists(exe)) exe = null;`
        // —— 空字符串不满足条件，exe 保持 ""，于是下面 `exe ??= FindBrowser()` 永远不触发，
        // 结果 app 模式每次都判定「找不到 Chromium」而退回普通窗口。
        var exe = _cfg.BrowserPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { exe = null; }

        // 1) 无地址栏独立窗口：只有 Chromium 系认 --app
        if (_cfg.BrowserMode == "app")
        {
            exe ??= FindBrowser();

            if (!string.IsNullOrEmpty(exe) && File.Exists(exe) && IsChromium(exe))
            {
                var profile = string.IsNullOrEmpty(_cfg.BrowserProfile)
                    ? Path.Combine(Paths.DataDir, "browser-profile")
                    : _cfg.BrowserProfile;

                try { Directory.CreateDirectory(profile); } catch { }

                var args = $"--app=\"{url}\" --user-data-dir=\"{profile}\" " +
                           "--no-first-run --no-default-browser-check --disable-background-mode";

                try
                {
                    Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                    Program.LogEvent($"已用应用窗口打开（{Path.GetFileName(exe)}）：{url}");
                    return (true, true, "应用窗口（" + Path.GetFileName(exe) + "）");
                }
                catch (Exception ex2)
                {
                    Program.LogEvent("应用窗口启动失败，回退普通窗口：" + ex2.Message);
                }
            }
            else
            {
                Program.LogEvent("app 模式找不到可用的 Chromium，回退为普通窗口");
            }
        }

        // 2) 普通窗口：用户指定过浏览器就用它，否则交给系统默认
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, "\"" + url + "\"") { UseShellExecute = false });
                Program.LogEvent($"已用 {Path.GetFileName(exe)} 打开：{url}");
                return (true, false, "浏览器窗口（" + Path.GetFileName(exe) + "）");
            }
            catch (Exception ex3)
            {
                Program.LogEvent("指定浏览器启动失败，回退系统默认：" + ex3.Message);
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Program.LogEvent("已用默认浏览器打开：" + url);
            return (true, false, "系统默认浏览器");
        }
        catch (Exception ex4)
        {
            Program.LogEvent("打开浏览器失败：" + ex4.Message);
            return (false, false, "打开浏览器失败：" + ex4.Message);
        }
    }

    // ==================== 令牌抓取（坑 P26）====================

    /// <summary>
    /// 从服务日志里抓带令牌的启动地址。
    /// dsh web 打印的是 <c>http://127.0.0.1:3080/?token=...</c> 这类形式，
    /// 最后一个已抓到的地址保留在调用方手里。
    /// </summary>
    internal static string? CaptureTokenUrl(string logText, int port)
    {
        var m = Regex.Match(logText, @"https?://127\.0\.0\.1:" + port + @"[^\s""'<>]*");
        return m.Success ? m.Value : null;
    }

    // ==================== 探测 ====================

    /// <summary>
    /// 自动探测 Chromium 系浏览器。先扫 %LOCALAPPDATA% —— Chrome 安装器的默认选项
    /// （单用户安装）就装在那里，不在 Program Files；漏掉这个目录是同类工具常见的坑。
    /// </summary>
    internal static string? FindBrowser()
    {
        foreach (var path in EnumerateCandidates(onlyChromium: true))
        {
            if (File.Exists(path)) { return path; }
        }
        return null;
    }

    /// <summary>设置页「默认浏览器」下拉里的候选：名称 + 完整路径，去重保序。</summary>
    internal static List<(string Name, string Path)> FindBrowsers()
    {
        var candidates = new (string Name, string Rel)[]
        {
            ("Chrome",   @"Google\Chrome\Application\chrome.exe"),
            ("Edge",     @"Microsoft\Edge\Application\msedge.exe"),
            ("Brave",    @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            ("Vivaldi",  @"Vivaldi\Application\vivaldi.exe"),
            ("Chromium", @"Chromium\Application\chrome.exe"),
            ("Firefox",  @"Mozilla Firefox\firefox.exe"),
        };

        var roots = RootDirs();
        var outp = new List<(string, string)>();

        foreach (var (name, rel) in candidates)
        {
            // 同一个浏览器只取第一个命中的安装位置
            foreach (var root in roots)
            {
                var p = Path.Combine(root, rel);
                if (!SafeExists(p)) { continue; }
                if (!outp.Any(x => string.Equals(x.Item2, p, StringComparison.OrdinalIgnoreCase)))
                {
                    outp.Add((name, p));
                }
                break;
            }
        }

        return outp;
    }

    private static IEnumerable<string> EnumerateCandidates(bool onlyChromium)
    {
        var rels = new List<string>
        {
            @"Google\Chrome\Application\chrome.exe",
            @"Microsoft\Edge\Application\msedge.exe",
            @"BraveSoftware\Brave-Browser\Application\brave.exe",
            @"Vivaldi\Application\vivaldi.exe",
            @"Chromium\Application\chrome.exe",
        };
        if (!onlyChromium) { rels.Add(@"Mozilla Firefox\firefox.exe"); }

        foreach (var root in RootDirs())
        {
            foreach (var rel in rels)
            {
                yield return Path.Combine(root, rel);
            }
        }
    }

    private static IEnumerable<string> RootDirs()
    {
        var roots = new List<string>();

        try
        {
            var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(lad)) { roots.Add(lad); }
        }
        catch { }

        // 用 GetFolderPath 而不是 GetEnvironmentVariable("ProgramFiles(x86)")：
        // 后者依赖环境变量在整条启动链上没丢（cmd start / 计划任务 / 服务都可能丢），
        // 而 GetFolderPath 直接查系统，可靠得多。
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            try
            {
                var v = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(v) && !roots.Contains(v, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(v);
                }
            }
            catch { }
        }

        // 兜底：即使上面全失败，这两个默认位置也值得查
        foreach (var fallback in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
        {
            if (!roots.Contains(fallback, StringComparer.OrdinalIgnoreCase)) { roots.Add(fallback); }
        }

        return roots;
    }

    private static bool SafeExists(string p)
    {
        try { return File.Exists(p); } catch { return false; }
    }

    // ==================== 检测已打开的 DSH 网页窗口 ====================

    /// <summary>标题里出现这些标记，就认为是一个 DSH 网页窗口。</summary>
    private static readonly string[] DshWindowMarkers =
    [
        "DeepSeek Harness",
        "DSH",
    ];

    /// <summary>
    /// 是否已经有 DSH 网页窗口开着。
    ///
    /// 用途：**重启服务后不要重复拉开网页**。重启时浏览器窗口通常还开着（只是连不上），
    /// 再开一个新的会变成两个窗口。
    ///
    /// 判断方式：枚举可见顶层窗口，标题含 DSH 标记且**不属于本进程**。
    /// 刻意不用「浏览器进程是否存活」判断——坑 P27：Chromium 的 --user-data-dir 是独占的，
    /// 后启动的进程会把命令行交给已有实例然后自己退出，进程存活与否完全不可靠。
    /// </summary>
    internal static bool IsDshWebWindowOpen()
    {
        var ourPid = (uint)Environment.ProcessId;
        var found = false;
        var sb = new StringBuilder(512);

        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) { return true; }

                GetWindowThreadProcessId(h, out var pid);
                if (pid == ourPid) { return true; }   // 排除面板自己的窗口

                sb.Clear();
                GetWindowText(h, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.Length == 0) { return true; }
                if (IsDshTitle(title))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Program.LogEvent("检测 DSH 网页窗口失败：" + ex.Message);
        }

        return found;
    }

    // ==================== 关闭 DSH 网页窗口 ====================

    private const uint WM_CLOSE = 0x0010;

    /// <summary>
    /// 关闭 DSH 网页窗口，返回请求关闭的窗口数。
    ///
    /// 做法：枚举可见顶层窗口 → 标题含 DSH 标记 → **再加一道「进程是浏览器」的保险** →
    /// 对该窗口 PostMessage(WM_CLOSE)。
    ///
    /// 刻意做成「关窗口」而不是「杀进程」：
    ///   · 不按进程名批量 Kill，就不会误伤用户自己开着的浏览器窗口；
    ///   · app 模式用的是独占 --user-data-dir + --disable-background-mode，
    ///     最后一个窗口关掉后该 Chromium 实例自己就退出了，不需要我们动手。
    /// </summary>
    internal static int CloseDshWindows()
    {
        var ourPid = (uint)Environment.ProcessId;
        var targets = new List<IntPtr>();
        var sb = new StringBuilder(512);

        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) { return true; }

                GetWindowThreadProcessId(h, out var pid);
                if (pid == ourPid) { return true; }        // 面板自己的窗口

                sb.Clear();
                GetWindowText(h, sb, sb.Capacity);
                if (!IsDshTitle(sb.ToString())) { return true; }
                if (!IsBrowserProcess(pid)) { return true; }

                targets.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Program.LogEvent("枚举 DSH 网页窗口失败：" + ex.Message);
            return 0;
        }

        foreach (var h in targets)
        {
            PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        return targets.Count;
    }

    /// <summary>标题是不是一个 DSH 网页窗口。</summary>
    private static bool IsDshTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) { return false; }
        foreach (var marker in DshWindowMarkers)
        {
            if (title.Contains(marker, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    /// <summary>进程名是不是浏览器 —— 关窗口前的第二道保险，避免关掉恰好标题相同的其它程序。</summary>
    private static bool IsBrowserProcess(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant() is
                "msedge" or "chrome" or "brave" or "vivaldi" or "chromium" or "firefox" or "iexplore";
        }
        catch { return false; }
    }

    // ---- Win32 ----
    private delegate bool EnumWindowsProc2(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc2 lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// <c>--app</c> 只有 Chromium 系支持；Firefox / IE 会把它当普通参数忽略，
    /// 结果开出一堆莫名其妙的东西。所以在选它们时主动降级。
    /// </summary>
    internal static bool IsChromium(string exe)
    {
        try
        {
            var n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            return n is not ("firefox" or "iexplore");
        }
        catch
        {
            return true;
        }
    }
}
