using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DshPanel2;

/// <summary>服务状态快照。所有字段都只读，由 <see cref="ServiceController.Snapshot"/> 生成。</summary>
internal readonly record struct ServiceSnapshot(
    bool Running,
    bool Starting,
    bool Restarting,
    bool Clean,
    int Pid,
    string Url,
    string? FailMsg,
    bool WebOpen);

/// <summary>
/// DSH 服务生命周期：启动 / 停止 / 重启 / 纯净模式 / 状态轮询。
///
/// 本类是旧版 DshPanel.cs 里「启停 + 端口 + 状态机」三块逻辑的移植，
/// 逐条保留了下述坑的规避写法（这些都是线上事故换来的）：
///   P14 日志走文件重定向，绝不读管道（读端关闭会让 node 写管道 EPIPE 直接被杀）
///   P15 停止用 taskkill /T /F 杀整棵树；启动优先 node 直连 bin.js，绕开 npx 的缓存锁
///   P16 端口探活三态：查询失败 ≠ 服务已停止
///   P26 端口就绪 ≠ 令牌地址就绪（dsh web 先进 LISTENING，过一会儿才打印带令牌的地址）
///   P27 app 模式必须独占 --user-data-dir，且绝不靠「浏览器进程是否存活」判断窗口关闭
///   P29 状态机去抖五条：_starting 期间不判死、_stopStreak>=2、_probeFail 只在 3 次时记日志、
///       重启时 Stop 失败必须中止、Stop 在本来没监听时返回 true
///
/// 与旧版刻意的行为差异（记在 docs/进度.md）：
///   1. 停止服务后**不再退出面板**（旧版会 Quit，因为旧面板是服务的附庸；新版有托盘常驻）
///   2. 服务意外停止后**不再退出面板**，只更新状态 + 托盘气泡
///   3. 启动失败不再弹对话框，改为把原因推给网页显示在按钮上（P28 的产品意图保留，载体换成网页）
/// </summary>
internal sealed class ServiceController
{
    private readonly PanelConfig _cfg;
    private readonly BrowserLauncher _browser;
    private readonly Action _onStateChanged;

    // ---- 状态机字段（对应旧版 52-64 行的那一组）----
    private bool _starting;
    private bool _restarting;
    private bool _clean;
    private bool _everRunning;
    private bool _pendingOpen;
    private bool _quitting;
    private int _stopStreak;
    private int _probeFail;
    private DateTime _startAt;
    private DateTime _failUntil = DateTime.MinValue;
    private string? _failMsg;
    private string? _tokenUrl;

    /// <summary>Tick 里算出的「端口→PID」结果，供 Snapshot 复用（修旧版每 1.5 秒起两个 netstat 的缺陷）。</summary>
    private int _cachedPid;

    /// <summary>
    /// 是不是**面板自己**用 --app 开出了一个独立窗口。
    /// 不能只看「有没有 DSH 网页窗口」：用户自己用 Edge 标签页打开的 DSH 页面标题一模一样的，
    /// 那样就会把「关闭网页 DSH」摆出来，一点把他正在用的界面关掉。
    /// </summary>
    private bool _webOpenedByUs;

    private int _beatTicks;

    internal ServiceController(PanelConfig cfg, Action onStateChanged)
    {
        _cfg = cfg;
        _browser = new BrowserLauncher(cfg);
        _onStateChanged = onStateChanged;
    }

    internal PanelConfig Config => _cfg;

    // ==================== 对外查询 ====================

    internal bool IsRunning() => ProbePort(_cfg.Port) == 1;

    internal ServiceSnapshot Snapshot() => new(
        Running: IsRunning(),
        Starting: _starting,
        Restarting: _restarting,
        Clean: _clean,
        Pid: _cachedPid,
        Url: _tokenUrl ?? _cfg.UrlBase,
        FailMsg: _failUntil > DateTime.Now ? _failMsg : null,
        // 「面板开过独立窗口」+「窗口现在还在」才算网页开着。前者防止把用户自己的
        // DSH 标签页误判成我们的窗口；后者让用户手动关掉后按钮能自动变回「打开网页」。
        WebOpen: _webOpenedByUs && BrowserLauncher.IsDshWebWindowOpen());

    // ==================== 端口探测（坑 P16）====================

    /// <summary>
    /// 三态：1 = 在监听，0 = 确定没在监听，-1 = 查询本身失败。
    /// 旧实现 catch 后 return false，把「系统调用抖动」和「服务真的停了」混为一谈，
    /// 于是偶发一次异常就足以让面板判定服务已停止并自杀。
    /// </summary>
    internal static int ProbePort(int port)
    {
        try
        {
            var eps = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (var ep in eps)
            {
                if (ep.Port == port) { return 1; }
            }
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 端口 → 监听进程 PID。起 netstat 子进程，用 Encoding.Default 读（中文系统下 UTF-8 会乱码）。
    /// 返回 0 表示找不到。
    /// </summary>
    internal int FindPidOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default,
                RedirectStandardOutput = true,
            };

            using var p = Process.Start(psi);
            if (p is null) { return 0; }

            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) { continue; }
                if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                var m = Regex.Match(line, ":" + port + @"\s+\S+\s+LISTENING\s+(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pid)) { return pid; }
            }
        }
        catch { }
        return 0;
    }

    // ==================== 轮询（坑 P29）====================

    /// <summary>1.5 秒一次。所有「判定服务已停止」的路径都必须过这里的去抖。</summary>
    internal void Tick()
    {
        if (_quitting) { return; }

        ScanLog();

        var probe = ProbePort(_cfg.Port);

        if (probe < 0)
        {
            // 查询失败既不等于「在运行」也不等于「已停止」：保持状态原样，等下一轮。
            // 这一段就是「面板凭空消失」的根治点。
            _probeFail++;
            if (_probeFail == 3)
            {
                Program.LogEvent("端口状态查询连续失败 3 次（暂不判定服务已停止）");
            }
            return;
        }

        _probeFail = 0;
        var running = probe == 1;

        if (running)
        {
            _stopStreak = 0;
            _everRunning = true;
            _starting = false;
            _restarting = false;

            // 端口→PID 每轮只算一次，Snapshot 复用（旧版两处各起一个 netstat）
            _cachedPid = _cachedPid > 0 && PidStillAlive(_cachedPid) ? _cachedPid : FindPidOnPort(_cfg.Port);

            if (_pendingOpen)
            {
                _pendingOpen = false;
                OpenBrowser();
            }
        }
        else
        {
            _cachedPid = 0;

            if (_starting)
            {
                if ((DateTime.Now - _startAt).TotalSeconds > 90)
                {
                    FailStart("90 秒内未检测到端口监听");
                }
            }
            else if (_everRunning)
            {
                // 连续两轮都确认端口消失才收工，单次抖动不再触发
                _stopStreak++;
                if (_stopStreak < 2)
                {
                    Program.LogEvent("未检测到端口监听，正在确认…");
                    _onStateChanged();
                    return;
                }

                Program.LogEvent("确认 DSH 已停止");
                _everRunning = false;
                _clean = false;
                _tokenUrl = null;
                Balloon("DSH 已停止", "DSH 服务已意外停止。");
            }
        }

        // 开发者心跳：托盘程序最难查的故障是「看起来还在、其实已经卡死」
        if (_cfg.DevMode)
        {
            _beatTicks++;
            if (_beatTicks >= 13)
            {
                _beatTicks = 0;
                Program.LogEvent($"heartbeat 端口 {_cfg.Port} {(running ? "监听中" : "未监听")}");
            }
        }
        else { _beatTicks = 0; }

        _onStateChanged();
    }

    private static bool PidStillAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    // ==================== 命令入口 ====================

    internal void HandleCommand(string name)
    {
        switch (name)
        {
            case "start":   OpenDsh(); break;
            case "stop":    ConfirmStop(); break;
            case "restart": ConfirmRestart(); break;
            // 网页上先点了「确认重启」才发这个 —— 二次确认已经在页面上做完了，别再弹 MessageBox。
            // （托盘菜单仍然走上面的 "restart"，托盘没有网页 UI，那一边的确认框要留着）
            case "restartNow":
                Program.LogEvent("用户确认重启 DSH 服务（网页按钮）");
                RestartAsync();
                break;
            case "openWeb": OpenWebOnly(); break;
            case "closeWeb": CloseWebOnly(); break;
            case "clean":   StartCleanDsh(); break;
            case "clearLog": ClearServiceLog(); break;
            case "exit":    Quit(); break;
            default:
                Program.LogEvent("ServiceController 未处理的命令：" + name);
                break;
        }
    }

    // ==================== 启动 ====================

    /// <summary>「启动并打开 DSH」主按钮：端口没在听就先起服务，起来后按 autoopen 决定是否开网页。</summary>
    internal void OpenDsh()
    {
        _failUntil = DateTime.MinValue;
        _failMsg = null;
        _openRetry = 0;

        if (IsPortListening())
        {
            OpenBrowser();
            return;
        }

        // 启动服务后是否自动开网页：既要看配置，也要看当前有没有已打开的 DSH 网页窗口。
        // 已经有窗口就不再拉一个新的（否则会变成两个），让用户在那个窗口上刷新即可。
        _pendingOpen = _cfg.AutoOpen && !BrowserLauncher.IsDshWebWindowOpen();
        if (_cfg.AutoOpen && !_pendingOpen)
        {
            Program.LogEvent("已有 DSH 网页窗口，启动后不重复打开");
        }

        StartService("web", clean: false);
    }

    /// <summary>只打开网页，完全不碰启动逻辑。</summary>
    internal void OpenWebOnly()
    {
        Program.LogEvent("按钮：打开网页");
        _openRetry = 0;
        OpenBrowser();
    }

    /// <summary>
    /// 关闭 DSH 网页窗口。只对「标题含 DSH 标记 + 进程是浏览器」的可见顶层窗口发 WM_CLOSE，
    /// **不杀进程、不按进程名批量结束**，所以不会误伤用户自己的浏览器窗口；
    /// 服务本身完全不受影响（这是「关网页」，不是「停服务」）。
    /// </summary>
    internal void CloseWebOnly()
    {
        Program.LogEvent("按钮：关闭网页");

        var closed = BrowserLauncher.CloseDshWindows();
        if (closed == 0)
        {
            Program.LogEvent("没有找到可关闭的 DSH 网页窗口");
            Balloon("没有找到网页窗口", "DSH 网页可能已经被关掉了。");
        }
        else
        {
            Program.LogEvent($"已请求关闭 {closed} 个 DSH 网页窗口");
        }

        _webOpenedByUs = false;
        _onStateChanged();
    }

    private bool IsPortListening() => ProbePort(_cfg.Port) == 1;

    private void StartService(string dshArgs, bool clean)
    {
        if (IsPortListening())
        {
            Program.LogEvent("启动被跳过：端口 " + _cfg.Port + " 已在监听");
            return;
        }

        if (_starting) { return; }

        PrepareLaunch(clean);

        string script;
        string how;
        try
        {
            script = WriteLaunchScript(clean ? "dsh-clean.cmd" : "dsh-run.cmd", dshArgs, out how);
            Program.LogEvent($"启动脚本：{script}（{how}）");
        }
        catch (Exception ex)
        {
            FailStart("生成启动脚本失败：" + ex.Message);
            return;
        }

        try
        {
            // 启动方式的选择过程（都实测过）：
            //   · CreateProcess + DETACHED_PROCESS → 脱离进程树 ✓，但 node 是控制台程序，
            //     在 detached 状态下 Windows 会**给它新建一个控制台窗口**（就是一个黑框，
            //     还打印完整启动日志和令牌地址）
            //   · Process.Start + CreateNoWindow → **无窗口** ✓，但 DSH 挂在面板进程树里
            //   · start /B 两级启动 → 严重依赖 cmd 解析，重定向行为不稳定
            //
            // 最终选 CreateNoWindow：优先保证用户看不到窗口。
            // 代价是 DSH 在面板的进程树里 —— 所以**绝不能对面板用 taskkill /T**。
            // 面板自己的退出走 StopWithPanel + 正常关闭流程，不会误伤服务。
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _cfg.WorkDir,
            });
            _starting = true;
            _startAt = DateTime.Now;
            Program.LogEvent($"开始启动 DSH 服务（端口 {_cfg.Port}，{how}{(clean ? "，纯净模式" : "")}）");
        }
        catch (Exception ex)
        {
            FailStart("启动失败：" + ex.Message);
            return;
        }

        _onStateChanged();
    }

    /// <summary>启动前的统一清理：删旧服务日志、重置读指针与令牌。</summary>
    private void PrepareLaunch(bool clean)
    {
        try { if (File.Exists(_cfg.LogFile)) { File.Delete(_cfg.LogFile); } } catch { }
        _tokenUrl = null;
        _clean = clean;
        if (!clean) { _restarting = false; }
        ResetLogCursor();
    }

    /// <summary>
    /// 生成启动脚本。优先 node 直连 bin.js（确定性），找不到才退回 npx。
    ///
    /// 为什么绕开 npx：npx 每次都要走一遍 npm 的解析/缓存流程，而重启流程刚 taskkill 掉
    /// 上一棵 node 进程树时，npm 的 _cacache 锁常常还没释放，npx 就卡在锁等待上——
    /// 表现出来正是「重启后 180 秒都没有端口监听」。
    /// </summary>
    private string WriteLaunchScript(string fileName, string dshArgs, out string how)
    {
        var bin = ResolveDshBin();
        string cmdline;
        if (bin is not null)
        {
            cmdline = "node \"" + bin + "\" " + dshArgs;
            how = "node 直连";
        }
        else
        {
            cmdline = "npx.cmd @deepseek-ai/dsh " + dshArgs;
            how = "npx（没找到本地 bin.js，退回慢速通道）";
        }

        var dir = Path.GetDirectoryName(_cfg.SvcCmd);
        if (string.IsNullOrEmpty(dir)) { dir = Path.GetDirectoryName(_cfg.LogFile); }
        if (string.IsNullOrEmpty(dir)) { dir = Paths.DataDir; }
        Directory.CreateDirectory(dir);

        var script = Path.Combine(dir, fileName);

        // 修复缺陷 j：旧版用 Encoding.Default（ANSI）落盘，WorkDir 含当前代码页之外字符时写坏。
        // 这里统一 UTF-8 无 BOM；cmd.exe 读 UTF-8 脚本需要先 CHCP 65001，否则中文路径会乱。
        var body =
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            "cd /d \"" + _cfg.WorkDir + "\"\r\n" +
            cmdline + " --port " + _cfg.Port + " --no-open > \"" + _cfg.LogFile + "\" 2>&1\r\n";

        File.WriteAllText(script, body, new UTF8Encoding(false));
        return script;
    }

    /// <summary>找本机 npx 缓存里最新的 dsh bin.js（按修改时间取最新，避免多版本时选错）。</summary>
    private static string? ResolveDshBin()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) { return null; }

        var root = Path.Combine(local, "npm-cache", "_npx");
        try
        {
            if (!Directory.Exists(root)) { return null; }

            string? best = null;
            var bestTime = DateTime.MinValue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var bin = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (!File.Exists(bin)) { continue; }
                var t = File.GetLastWriteTime(bin);
                if (t > bestTime) { bestTime = t; best = bin; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>
    /// 纯净 DSH（排障用）：官方 CLI 自带逃生通道
    /// <c>dsh rescue --from-default-profile web</c> = 用发行版自带的 web 模板新建一个
    /// 只含官方默认 bundle 的 profile 并启动。profile 已存在时同一条命令会报
    /// 「profile "rescue" already exists」，所以第二次起换成 <c>--profile rescue</c>。
    ///
    /// 修复缺陷 g：旧版只查 DSH_HOME 环境变量和 %USERPROFILE%\.dsh，忽略了配置里的 dshhome。
    /// 本版优先用配置，环境变量作兜底。
    /// </summary>
    internal void StartCleanDsh()
    {
        if (IsPortListening())
        {
            Program.LogEvent("启动纯净 DSH 被跳过：端口已被占用");
            return;
        }
        if (_starting) { return; }

        var home = _cfg.DshHome;
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("DSH_HOME") ?? "";
        }
        if (string.IsNullOrEmpty(home))
        {
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        var exists = File.Exists(Path.Combine(home, "profiles", "rescue", "package.json"));

        PrepareLaunch(clean: true);
        // 纯净启动不是「重启」，图标别转圈
        _restarting = false;

        StartService(exists ? "--profile rescue" : "rescue --from-default-profile web", clean: true);

        Program.LogEvent(exists
            ? "纯净 DSH：复用已有 rescue profile"
            : "纯净 DSH：首次创建 rescue profile");
    }

    /// <summary>启动失败：不弹窗，把原因推给网页显示在按钮上（P28 的产品意图保留）。</summary>
    private void FailStart(string reason)
    {
        _starting = false;
        _restarting = false;
        _failUntil = DateTime.Now.AddSeconds(6);
        _failMsg = reason;

        Program.LogEvent("启动失败：" + reason);
        DumpServiceTailToLog();
        _onStateChanged();
    }

    /// <summary>把服务日志末尾一段抄进面板日志，启动失败时一眼能看到 node/npm 报了什么。</summary>
    private void DumpServiceTailToLog()
    {
        try
        {
            if (!File.Exists(_cfg.LogFile)) { return; }
            string text;
            using (var fs = new FileStream(_cfg.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var take = (int)Math.Min(1600, fs.Length);
                if (take <= 0) { return; }
                fs.Seek(-take, SeekOrigin.End);
                var buf = new byte[take];
                var n = fs.Read(buf, 0, take);
                text = Encoding.UTF8.GetString(buf, 0, n);
            }

            var nl = text.IndexOf('\n');
            if (nl >= 0 && nl < text.Length - 1) { text = text[(nl + 1)..]; }  // 丢掉可能被截断的首行
            text = text.Trim();
            if (text.Length == 0) { return; }

            Program.LogEvent("服务日志最后几行：\n" + text);
        }
        catch { }
    }

    // ==================== 停止 / 重启 ====================

    private void ConfirmStop()
    {
        var r = MessageBox.Show(
            "确定要关闭 DSH 服务吗？\r\n\r\n正在使用的 DSH 网页会立即断开连接。\r\n" +
            "（面板本身不会关闭）",
            "关闭 DSH", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes)
        {
            Program.LogEvent("用户确认关闭 DSH 服务");
            StopAsync();
        }
    }

    /// <summary>
    /// 停止服务。异步等待端口释放，不阻塞 UI 线程。
    /// 与旧版区别：完成后**不退出面板**（旧版 exitAfter=true 会 Quit）。
    /// </summary>
    private async void StopAsync()
    {
        var pid = FindPidOnPort(_cfg.Port);
        if (pid <= 0)
        {
            Program.LogEvent("未发现监听 " + _cfg.Port + " 的进程");
            _pendingOpen = false;
            _starting = false;
            _onStateChanged();
            return;
        }

        Program.LogEvent($"正在停止 DSH（PID {pid}）");
        RunHidden("taskkill.exe", $"/PID {pid} /T /F");

        var stopped = false;
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(200);
            if (ProbePort(_cfg.Port) != 1) { stopped = true; break; }
        }

        if (stopped)
        {
            Program.LogEvent("DSH 已停止");
            _starting = false;
            _pendingOpen = false;
            _everRunning = false;
            _clean = false;
            _tokenUrl = null;
            _cachedPid = 0;
        }
        else
        {
            // 必须留痕：旧版这里只写窗口不写日志，于是「点了没反应」事后查不到原因
            Program.LogEvent($"停止 DSH 失败：端口 {_cfg.Port} 仍被占用（PID {pid}）");
            Balloon("停止失败", $"端口 {_cfg.Port} 仍被占用，请稍后重试。");
        }

        _onStateChanged();
    }

    private void ConfirmRestart()
    {
        var r = MessageBox.Show(
            "要重启 DSH 服务吗？\r\n\r\n正在使用的 DSH 网页会短暂断开，服务起来后需要你在页面上按 F5。",
            "重启 DSH", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) { return; }

        Program.LogEvent("用户确认重启 DSH 服务");
        RestartAsync();
    }

    private async void RestartAsync()
    {
        _stopStreak = 0;
        _pendingOpen = false;

        var pid = FindPidOnPort(_cfg.Port);
        var stopped = true;

        if (pid > 0)
        {
            RunHidden("taskkill.exe", $"/PID {pid} /T /F");
            stopped = false;
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(200);
                if (ProbePort(_cfg.Port) != 1) { stopped = true; break; }
            }
        }

        if (!stopped)
        {
            Program.LogEvent("重启中止：DSH 未能在超时前停止");
            MessageBox.Show(
                $"DSH 没能停下来（端口 {_cfg.Port} 仍被占用），重启已中止。",
                "重启中止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Stop 在「本来就没监听」时会原样返回、不重置 _everRunning，
        // 这里统一清掉，免得随后的 Tick 把「服务不在」误判成「服务刚停止」
        _everRunning = false;
        _stopStreak = 0;
        _probeFail = 0;
        _restarting = true;

        StartService("web", clean: false);

        // 重启后的网页处理：
        //   已经开着 DSH 网页窗口 → 不动它（重启时浏览器窗口通常还在，只是连不上），
        //                          用户在那个窗口上按 F5 就行；
        //   没有窗口 → 按配置自动拉起一个新的。
        if (_cfg.AutoOpen)
        {
            if (BrowserLauncher.IsDshWebWindowOpen())
            {
                _pendingOpen = false;
                Program.LogEvent("重启完成：检测到已有 DSH 网页窗口，不重复打开（在页面上按 F5 即可）");
            }
            else
            {
                _pendingOpen = true;
                Program.LogEvent("重启完成：没有已打开的网页窗口，准备自动打开");
            }
        }
        else
        {
            Program.LogEvent("DSH 已重启（未开启自动打开网页）");
        }
    }

    /// <summary>
    /// 以 DETACHED_PROCESS 启动子进程：没有控制台窗口，且**父进程直接是系统**，
    /// 不在面板的进程树里。用于启动 DSH 服务，避免 `taskkill /T` 面板时连带杀掉服务。
    /// </summary>
    private static bool LaunchDetached(string exe, string args, string workDir)
    {
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf<STARTUPINFO>();

        // DETACHED_PROCESS(0x8) + CREATE_NEW_PROCESS_GROUP(0x200)：
        // 前者让它没有控制台、脱离父进程的会话，后者避免它收到面板 Ctrl+C 之类的影响
        const uint DETACHED_PROCESS = 0x00000008;
        const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

        var cmdline = "\"" + exe + "\" " + args;
        var ok = CreateProcess(
            null, cmdline, IntPtr.Zero, IntPtr.Zero, false,
            DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,
            IntPtr.Zero, workDir, ref si, out var pi);

        if (!ok) { return false; }

        // 句柄必须关掉，否则面板一直持有子进程的引用
        try { CloseHandle(pi.hProcess); } catch { }
        try { CloseHandle(pi.hThread); } catch { }
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static void RunHidden(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(8000);
        }
        catch { }
    }

    // ==================== 退出 ====================

    internal void Quit()
    {
        if (_quitting) { return; }
        _quitting = true;

        if (_cfg.StopWithPanel && IsPortListening())
        {
            Program.LogEvent("按设置一并停止 DSH 服务");
            var pid = FindPidOnPort(_cfg.Port);
            if (pid > 0) { RunHidden("taskkill.exe", $"/PID {pid} /T /F"); }
        }

        Program.LogEvent("Quit()：面板即将退出");
        Application.Exit();
    }

    /// <summary>退出前同步停服务。窗口已经关掉，等不了异步轮询。</summary>
    internal void StopForShutdown()
    {
        var pid = FindPidOnPort(_cfg.Port);
        if (pid <= 0) { return; }

        Program.LogEvent($"退出时一并停止 DSH（PID {pid}）");
        RunHidden("taskkill.exe", $"/PID {pid} /T /F");
    }

    // ==================== 浏览器 ====================

    private int _openRetry;

    /// <summary>
    /// 打开 DSH 网页。
    ///
    /// 坑 P26：dsh web 是先让端口进入 LISTENING、过一会儿才在日志里打印带令牌的启动地址。
    /// 端口一就绪就拿裸地址去开浏览器会因为缺少令牌而 401 —— 页面显示
    /// 「dsh web authentication required; reopen the URL printed by dsh web.」
    ///
    /// 所以：**没拿到令牌就绝不打开**。最多等 20 轮（约 30 秒），超时后不打开裸地址，
    /// 改为提示用户稍后重试（打开一个必定 401 的窗口比不打开更糟）。
    /// </summary>
    private void OpenBrowser()
    {
        var url = _tokenUrl;

        if (string.IsNullOrEmpty(url))
        {
            if (_openRetry < 20)
            {
                _openRetry++;
                _pendingOpen = true;
                if (_openRetry == 1) { Program.LogEvent("等待服务打印带令牌的启动地址…"); }
                return;
            }

            // 超时：不打开裸地址（那必然是 401 错误页），提示重试
            _pendingOpen = false;
            _openRetry = 0;
            Program.LogEvent("30 秒内未取到令牌地址，本次不打开网页");
            Balloon("还没准备好",
                $"服务尚未打印带令牌的地址，请稍后再点「打开网页 DSH」。");
            _onStateChanged();
            return;
        }

        _pendingOpen = false;
        _openRetry = 0;

        var (opened, isApp, detail) = _browser.Open(url);
        if (!opened) { Balloon("打开网页失败", detail); }
        if (opened && isApp) { _webOpenedByUs = true; }   // 只有独立窗口才给「关闭网页」按钮
        _onStateChanged();
    }

    // ==================== 日志（坑 P14）====================

    private long _logPos;

    /// <summary>
    /// 有状态的 UTF-8 解码器：跨轮询保留。旧版每轮 GetString(buf,0,n) 直接把分块当完整文本解，
    /// 多字节字符正好被切在两块之间时就会解出乱码方块。
    /// </summary>
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    private static readonly Regex AnsiRegex = new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>日志增量回调，由 MainForm 接到 UiBridge.PushLog。</summary>
    internal Action<string>? OnLogText;

    /// <summary>
    /// 增量读服务日志。
    ///
    /// 为什么是读文件而不是读管道：旧版把子进程 stdout 重定向到管道再读，一旦读端关闭，
    /// 子进程（node）往管道写就会拿到 EPIPE 直接被杀 —— 表现是「服务莫名死掉」。
    /// 重定向到文件再用 FileShare.ReadWrite 增量读，彻底不碰管道。
    /// </summary>
    private void ScanLog()
    {
        try
        {
            if (!File.Exists(_cfg.LogFile)) { return; }

            using var fs = new FileStream(_cfg.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _logPos) { _logPos = 0; }      // 文件被截断或重建
            if (fs.Length == _logPos) { return; }

            fs.Seek(_logPos, SeekOrigin.Begin);
            var count = (int)Math.Min(fs.Length - _logPos, 64 * 1024);
            var buf = new byte[count];
            var n = fs.Read(buf, 0, count);
            if (n <= 0) { return; }
            _logPos += n;

            // 有状态解码：不足一个完整字符的尾巴留在解码器里等下一轮
            var chars = new char[_decoder.GetCharCount(buf, 0, n) + 1];
            var charCount = _decoder.GetChars(buf, 0, n, chars, 0);
            var text = new string(chars, 0, charCount);

            text = AnsiRegex.Replace(text, string.Empty);   // 剥 ANSI 颜色码
            if (text.Length == 0) { return; }

            // 顺手抓带令牌的启动地址（坑 P26：端口就绪 ≠ 令牌就绪）
            var token = BrowserLauncher.CaptureTokenUrl(text, _cfg.Port);
            if (!string.IsNullOrEmpty(token) && token != _tokenUrl)
            {
                _tokenUrl = token;
                Program.LogEvent("已抓到带令牌的启动地址");
            }

            OnLogText?.Invoke(text);
        }
        catch (Exception ex)
        {
            Program.LogEvent("读服务日志失败：" + ex.Message);
        }
    }

    /// <summary>重置日志读取位置（启动新实例/清空日志时调用）。</summary>
    private void ResetLogCursor()
    {
        _logPos = 0;
        _decoder.Reset();
    }

    /// <summary>把服务日志当前的完整内容推给网页一次，供页面首次进入日志页时显示历史。</summary>
    internal void PushServiceLogTail()
    {
        try
        {
            if (!File.Exists(_cfg.LogFile)) { return; }
            using var fs = new FileStream(_cfg.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(64 * 1024, fs.Length);
            if (take <= 0) { return; }
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var n = fs.Read(buf, 0, take);
            var text = Encoding.UTF8.GetString(buf, 0, n);

            // 丢可能被截断的首行
            var nl = text.IndexOf('\n');
            if (nl >= 0 && nl < text.Length - 1) { text = text[(nl + 1)..]; }

            OnLogText?.Invoke(AnsiRegex.Replace(text, string.Empty));
            _logPos = fs.Length;   // 已把历史推过去，避免下一轮重复推
        }
        catch (Exception ex)
        {
            Program.LogEvent("推送服务日志失败：" + ex.Message);
        }
    }

    private void ClearServiceLog()
    {
        // 缺陷 f 修复：旧版 clearLog 只清内存 TextBox，不清文件、也不重置读指针
        try
        {
            if (File.Exists(_cfg.LogFile)) { File.WriteAllText(_cfg.LogFile, "", new UTF8Encoding(false)); }
            ResetLogCursor();
            Program.LogEvent("服务日志已清空");
            _onStateChanged();
        }
        catch (Exception ex)
        {
            Program.LogEvent("清空服务日志失败：" + ex.Message);
        }
    }

    // ==================== 托盘气泡 ====================

    /// <summary>托盘气泡的可选回调，M6 由 TrayManager 注入。</summary>
    internal Action<string, string>? OnBalloon;

    private void Balloon(string title, string text)
    {
        if (!_cfg.BalloonOnStop) { return; }
        try { OnBalloon?.Invoke(title, text); }
        catch { }
    }
}
