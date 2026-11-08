using System.Text;

namespace DshPanel2;

/// <summary>
/// 路径探测。全部为静态纯逻辑，便于单测。
///
/// 相对旧版的两处关键改动：
///   1. <b>DataDir 带回退链</b>：旧版只试 %LOCALAPPDATA%\\DshPanel，写不进去就悄悄退到 exe 目录，
///      而且没有诊断。本版每级都真写探针，并把最终选择记进日志（解决开发期沙箱内写不了 AppData 的问题）。
///   2. <b>去掉桌宠相关路径</b>：dsh-pet 已确认废案。
/// </summary>
internal static class Paths
{
    private static string? _dataDir;

    /// <summary>
    /// 面板自身的数据目录（日志/配置/脚本/浏览器 profile 都放这里）。
    /// 顺序：%LOCALAPPDATA%\\DshPanel2 → exe 同级 DshPanel2-data → %TEMP%\\DshPanel2。
    /// 第一次访问时求值并缓存。
    /// </summary>
    internal static string DataDir => _dataDir ??= ResolveDataDir();

    /// <summary>数据目录最终落在哪一级的说明，写日志用。</summary>
    internal static string DataDirNote { get; private set; } = "";

    /// <summary>
    /// exe 所在目录。
    /// 注意 AppContext.BaseDirectory 本身就是目录（带尾部分隔符），
    /// 早先这里多套了一层 GetDirectoryName，结果配置和日志全落到 bin/Debug 而不是 exe 同级。
    /// </summary>
    internal static string ExeDir()
    {
        try
        {
            var d = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(d)) { return d; }
        }
        catch { }
        return ".";
    }

    private static string ResolveDataDir()
    {
        string local = "";
        try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }

        var candidates = new List<(string Path, string Note)>();

        if (!string.IsNullOrEmpty(local))
        {
            candidates.Add((Path.Combine(local, "DshPanel2"), "用户数据目录"));
        }
        candidates.Add((Path.Combine(ExeDir(), "DshPanel2-data"), "exe 同级（AppData 不可写时的回退）"));
        try { candidates.Add((Path.Combine(Path.GetTempPath(), "DshPanel2"), "临时目录（最后回退）")); } catch { }

        foreach (var (dir, note) in candidates)
        {
            if (CanWriteDir(dir))
            {
                DataDirNote = $"{dir}（{note}）";
                return dir;
            }
        }

        DataDirNote = "（全部候选都不可写，退回当前目录）";
        return ".";
    }

    /// <summary>
    /// 真的去写一个探针文件再删掉。靠 ACL 或只读属性判断「能不能写」经常误判，
    /// 尤其是 AppData 下的重定向/虚拟化场景。
    /// </summary>
    internal static bool CanWriteDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) { return false; }
        var probe = "";
        try
        {
            Directory.CreateDirectory(dir);
            probe = Path.Combine(dir, ".dshpanel-write-test.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { }

        try { if (probe.Length > 0 && File.Exists(probe)) { File.Delete(probe); } } catch { }
        return false;
    }

    /// <summary>判断一个目录「像不像 DSH 的数据目录」：有 profiles 子目录，或配置文件。</summary>
    internal static bool LooksLikeDshHome(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { return false; }
            if (Directory.Exists(Path.Combine(dir, "profiles"))) { return true; }
            if (File.Exists(Path.Combine(dir, "config.json"))) { return true; }
            if (File.Exists(Path.Combine(dir, "config.yaml"))) { return true; }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 自动探测 DSH 目录。只查两个默认位置，绝不扫描磁盘：
    /// 环境变量 DSH_HOME → %USERPROFILE%\\.dsh。
    /// </summary>
    internal static string ProbeDefaultDshHome()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrEmpty(env) && LooksLikeDshHome(env)) { return env.TrimEnd('\\'); }
        }
        catch { }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                var cand = Path.Combine(home, ".dsh");
                if (LooksLikeDshHome(cand)) { return cand; }
            }
        }
        catch { }

        return "";
    }

    internal static string DefaultWorkDir()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) { return home; }
        }
        catch { }
        return ExeDir();
    }

    /// <summary>
    /// 配置文件放哪：优先绿色版（exe 旁边），其次面板数据目录。
    /// 判断顺序：已有便携配置 → 已有数据目录配置 → exe 目录能否写 → 数据目录。
    /// </summary>
    internal static string PickConfigPath()
    {
        string portable = "";
        string inside = "";
        try { portable = Path.Combine(ExeDir(), "panel.config.ini"); } catch { }
        try { inside = Path.Combine(DataDir, "panel.config.ini"); } catch { }

        try { if (portable.Length > 0 && File.Exists(portable)) { return portable; } } catch { }
        try { if (inside.Length > 0 && File.Exists(inside)) { return inside; } } catch { }

        if (CanWriteDir(ExeDir()) && portable.Length > 0) { return portable; }
        if (inside.Length > 0) { return inside; }
        return "panel.config.ini";
    }
}

/// <summary>
/// INI 文本的行级读写。纯函数、无 IO —— 单测直接喂 string[] 就能验。
///
/// 旧版的解析/写回逻辑混在 <c>PanelConfig</c> 里、和 <c>File.ReadAllLines</c> 绑定，
/// 完全测不了；重写时把它剥出来。
/// </summary>
internal static class IniText
{
    /// <summary>逐行解析 key=value。跳过空行与 #、; 注释，允许值两端加引号。</summary>
    internal static IEnumerable<(string Key, string Value)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            if (raw is null) { continue; }
            var s = raw.Trim();
            if (s.Length == 0 || s[0] == '#' || s[0] == ';') { continue; }

            var eq = s.IndexOf('=');
            if (eq <= 0) { continue; }

            var k = s[..eq].Trim().ToLowerInvariant();
            var v = s[(eq + 1)..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') { v = v[1..^1]; }
            yield return (k, v);
        }
    }

    /// <summary>
    /// 就地更新一个键；不存在则追加到末尾。
    /// 不改整体结构，是为了保住用户手写的注释与键顺序。
    /// </summary>
    internal static void SetKey(List<string> lines, string key, string val)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var s = lines[i];
            if (s is null) { continue; }
            var t = s.Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') { continue; }

            var eq = t.IndexOf('=');
            if (eq <= 0) { continue; }

            if (string.Equals(t[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                // 保留原文件里的键名写法（大小写），只换值 —— 避免把用户写的 "port" 改成 "PORT"
                lines[i] = t[..eq].Trim() + " = " + val;
                return;
            }
        }
        lines.Add(key + " = " + val);
    }

    internal static bool Truthy(string v)
    {
        v = v.Trim().ToLowerInvariant();
        return v is "1" or "true" or "yes" or "on" or "是";
    }

    internal static string Flag(bool b) => b ? "1" : "0";
}

/// <summary>
/// 面板配置：纯文本 key=value，# 或 ; 开头为注释。
///
/// 为什么不用 JSON：配置项全是平铺标量，INI 更好手写、更好读，而且不需要引第三方库。
/// 解析失败一律静默退回默认值 —— 配置坏了不该让面板起不来。
/// </summary>
internal sealed class PanelConfig
{
    // ---------------- 路径默认值 ----------------
    // 全部按「任意一台机器」推导，不写死任何人的机器路径。
    // 面板自身的数据（日志/脚本）放 Paths.DataDir，
    // DSH 的数据目录（profiles）自动探测，工作目录默认是用户主目录。

    /// <summary>DSH 的数据目录（= DSH_HOME）。空字符串 = 没找到，需要用户在设置里指定。</summary>
    public string DshHome = Paths.ProbeDefaultDshHome();

    public int Port = 3080;
    public string SvcCmd = Path.Combine(Paths.DataDir, "dsh-run.cmd");
    public string LogFile = Path.Combine(Paths.DataDir, "dsh-service.log");
    public string StateLog = Path.Combine(Paths.DataDir, "panel-state.log");
    public string IcoFile = Path.Combine(Paths.ExeDir(), "DeepSeekHarness-WhaleGirl.ico");
    public string WorkDir = Paths.DefaultWorkDir();
    public string Title = "蓝色大肥鱼 · DSH 控制台";

    /// <summary>app = 独立应用窗口 | tab = 系统默认浏览器标签页</summary>
    public string BrowserMode = "app";
    /// <summary>留空 = 自动探测</summary>
    public string BrowserPath = "";
    /// <summary>留空 = 数据目录\\browser-profile</summary>
    public string BrowserProfile = "";
    /// <summary>插件清单文件。留空 = 跟着 DshHome 走（见 PluginJson）</summary>
    public string PluginJsonRaw = "";

    // ---- 设置页里的开关（全部由面板自己写回本文件）----
    public bool AutoStart = false;    // 开机自启（写 HKCU 的 Run 键，不触发 UAC）
    public bool AutoOpen = true;      // 启动服务后自动打开网页
    public bool AutoTray = false;     // 启动后自动最小化到托盘
    public bool DarkTheme = true;     // 深色主题
    public bool StopWithPanel = false;// 关闭面板时一并停止 DSH 服务
    public bool BalloonOnStop = true; // 服务意外停止时托盘气泡提示
    public bool DevMode = false;      // 开发者模式：每 20 秒写一行心跳

    /// <summary>
    /// 点 X 时收进托盘（而不是退出）。
    /// **默认关**：用户对「点 X 就该关掉」的预期最强；早期默认收托盘，
    /// 结果用户以为关掉了，再双击图标被单实例检查拦住，看起来就是「打不开」。
    /// </summary>
    public bool CloseToTray = false;

    /// <summary>插件清单：显式给了 pluginjson 就用它，否则从 DSH 目录推出来。</summary>
    public string PluginJson
    {
        get
        {
            if (!string.IsNullOrEmpty(PluginJsonRaw)) { return PluginJsonRaw; }
            if (string.IsNullOrEmpty(DshHome)) { return ""; }
            return Path.Combine(DshHome, "profiles", "web", "package.json");
        }
    }

    /// <summary>web profile 的目录（插件 package.json 就在里面）。</summary>
    public string WebProfileDir =>
        string.IsNullOrEmpty(DshHome) ? "" : Path.Combine(DshHome, "profiles", "web");

    /// <summary>「DSH 目录可用」：既配了路径、目录也真的在。</summary>
    public bool DshConfigured => !string.IsNullOrEmpty(DshHome) && Directory.Exists(DshHome);

    /// <summary>浏览器 app 模式的独立配置目录。</summary>
    public string BrowserProfileDir =>
        string.IsNullOrEmpty(BrowserProfile)
            ? Path.Combine(Paths.DataDir, "browser-profile")
            : BrowserProfile;

    private string _path = "";

    /// <summary>
    /// 注意：这里不能叫 Path —— 会和 System.IO.Path 撞名，
    /// 静态辅助里的 Path.Combine 会被解析成这个属性（旧版踩过，坑 P31）。
    /// </summary>
    public string FilePath => _path;

    public string UrlBase => "http://127.0.0.1:" + Port;

    // ==================== 读写 ====================

    public static PanelConfig Load(string path)
    {
        var c = new PanelConfig { _path = path };

        try
        {
            if (!File.Exists(path))
            {
                c.SaveSample();   // 第一次运行：就地生成一份带注释的默认配置
                return c;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (var (k, v) in IniText.Parse(lines))
            {
                switch (k)
                {
                    case "port":
                        if (int.TryParse(v, out var p) && p > 0 && p < 65536) { c.Port = p; }
                        break;
                    case "dshhome":        c.DshHome        = v; break;
                    case "svccmd":         c.SvcCmd         = v; break;
                    case "logfile":        c.LogFile        = v; break;
                    case "statelog":       c.StateLog       = v; break;
                    case "icofile":        c.IcoFile        = v; break;
                    case "workdir":        c.WorkDir        = v; break;
                    case "title":          c.Title          = v; break;
                    case "browsermode":    c.BrowserMode    = v.ToLowerInvariant(); break;
                    case "browserpath":    c.BrowserPath    = v; break;
                    case "browserprofile": c.BrowserProfile = v; break;
                    case "pluginjson":     c.PluginJsonRaw  = v; break;
                    case "autostart":      c.AutoStart      = IniText.Truthy(v); break;
                    case "autoopen":       c.AutoOpen       = IniText.Truthy(v); break;
                    case "autotray":       c.AutoTray       = IniText.Truthy(v); break;
                    case "darktheme":      c.DarkTheme      = IniText.Truthy(v); break;
                    case "stopwithpanel":  c.StopWithPanel  = IniText.Truthy(v); break;
                    case "balloononstop":  c.BalloonOnStop  = IniText.Truthy(v); break;
                    case "devmode":        c.DevMode        = IniText.Truthy(v); break;
                    case "closetotray":    c.CloseToTray    = IniText.Truthy(v); break;
                }
            }
        }
        catch
        {
            // 配置读坏了就整份走默认值，绝不让它阻断启动
        }

        return c;
    }

    /// <summary>
    /// 把内存里的值写回配置文件：已有的键就地替换，没有的追加到末尾。
    /// 不整体重写，是为了保住用户自己写的注释和键顺序。
    /// </summary>
    public void Save()
    {
        try
        {
            var lines = new List<string>();
            if (File.Exists(_path)) { lines.AddRange(File.ReadAllLines(_path, Encoding.UTF8)); }

            IniText.SetKey(lines, "dshhome",       DshHome);
            IniText.SetKey(lines, "port",          Port.ToString());
            IniText.SetKey(lines, "svccmd",        SvcCmd);
            IniText.SetKey(lines, "logfile",       LogFile);
            IniText.SetKey(lines, "statelog",      StateLog);
            IniText.SetKey(lines, "icofile",       IcoFile);
            IniText.SetKey(lines, "workdir",       WorkDir);
            IniText.SetKey(lines, "title",         Title);
            IniText.SetKey(lines, "browsermode",   BrowserMode);
            IniText.SetKey(lines, "browserpath",   BrowserPath);
            IniText.SetKey(lines, "browserprofile", BrowserProfile);
            IniText.SetKey(lines, "pluginjson",    PluginJsonRaw);
            IniText.SetKey(lines, "autostart",     IniText.Flag(AutoStart));
            IniText.SetKey(lines, "autoopen",      IniText.Flag(AutoOpen));
            IniText.SetKey(lines, "autotray",      IniText.Flag(AutoTray));
            IniText.SetKey(lines, "darktheme",     IniText.Flag(DarkTheme));
            IniText.SetKey(lines, "stopwithpanel", IniText.Flag(StopWithPanel));
            IniText.SetKey(lines, "balloononstop", IniText.Flag(BalloonOnStop));
            IniText.SetKey(lines, "devmode",       IniText.Flag(DevMode));
            IniText.SetKey(lines, "closetotray",   IniText.Flag(CloseToTray));

            // 不带 BOM：手写配置文件时 BOM 会让部分编辑器显示怪字符
            File.WriteAllLines(_path, lines, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 写不进去（只读目录 / 被占用）也不该让面板崩掉，内存里的值照旧生效
            Program.LogEvent("配置写入失败：" + ex.Message);
        }
    }

    /// <summary>首次运行生成带注释的样板配置。删掉配置文件就会重新生成。</summary>
    private void SaveSample()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DSH 控制面板配置（key = value 纯文本，# 或 ; 开头为注释）");
        sb.AppendLine("# 改完保存、重新打开面板即可生效。删掉整个文件会重新生成这份默认配置。");
        sb.AppendLine("# 设置页里的大部分选项也会自动写回这里，一般不用手改。");
        sb.AppendLine();
        sb.AppendLine("# ---------------------------------------------------------------------");
        sb.AppendLine("# DSH 目录：DeepSeek Harness 的数据目录（就是 DSH_HOME，里面应当有 profiles 子目录）。");
        sb.AppendLine("#   留空 = 启动时自动探测（先看环境变量 DSH_HOME，再看 %USERPROFILE%\\.dsh）");
        sb.AppendLine("#   插件启停页读写的就是 <DSH 目录>\\profiles\\web\\package.json。");
        sb.AppendLine("dshhome = " + DshHome);
        sb.AppendLine();
        sb.AppendLine("# 服务监听端口");
        sb.AppendLine("port = " + Port);
        sb.AppendLine();
        sb.AppendLine("# 启动脚本的落脚点（面板会在同目录生成 dsh-run.cmd / dsh-clean.cmd）。");
        sb.AppendLine("# 这里只决定「脚本写在哪」，启动方式由面板自己决定（优先 node 直连 CLI）。");
        sb.AppendLine("svccmd = " + SvcCmd);
        sb.AppendLine();
        sb.AppendLine("# 服务输出日志。面板从这里读日志正文，并抓取带令牌的启动地址。");
        sb.AppendLine("logfile = " + LogFile);
        sb.AppendLine();
        sb.AppendLine("# 面板自身的生命周期日志（启动 / 退出 / 崩溃都记在这里）");
        sb.AppendLine("statelog = " + StateLog);
        sb.AppendLine();
        sb.AppendLine("# 运行服务的工作目录");
        sb.AppendLine("workdir = " + WorkDir);
        sb.AppendLine();
        sb.AppendLine("# 面板窗口与快捷方式用的图标");
        sb.AppendLine("icofile = " + IcoFile);
        sb.AppendLine();
        sb.AppendLine("# 窗口标题");
        sb.AppendLine("title = " + Title);
        sb.AppendLine();
        sb.AppendLine("# 打开网页的方式：");
        sb.AppendLine("#   app = 独立应用窗口（无地址栏，像原生应用；需要 Edge / Chrome 等 Chromium 系浏览器）");
        sb.AppendLine("#   tab = 用系统默认浏览器开一个标签页");
        sb.AppendLine("browsermode = " + BrowserMode);
        sb.AppendLine();
        sb.AppendLine("# 浏览器可执行文件。留空 = 自动探测（先 %LOCALAPPDATA%，再 Program Files）");
        sb.AppendLine("browserpath = " + BrowserPath);
        sb.AppendLine();
        sb.AppendLine("# app 模式的独立浏览器配置目录。留空 = 面板数据目录\\browser-profile");
        sb.AppendLine("# 用独立目录是为了不和你日常在用的浏览器实例互相干扰。Chromium 的");
        sb.AppendLine("# --user-data-dir 是独占的：共用同一个目录时，后启动的进程会把命令行交给已有");
        sb.AppendLine("# 实例然后自己立刻退出。DSH 靠地址里的令牌认证，所以这里没有书签和扩展也不影响。");
        sb.AppendLine("browserprofile = " + BrowserProfile);
        sb.AppendLine();
        sb.AppendLine("# 插件清单文件。留空 = 自动跟着上面的 dshhome 走：");
        sb.AppendLine("#   <dshhome>\\profiles\\web\\package.json");
        sb.AppendLine("# 只有在插件清单放在别处时才需要手动填。插件启停页直接读写它的 bundles 数组。");
        sb.AppendLine("pluginjson = " + PluginJsonRaw);
        sb.AppendLine();
        sb.AppendLine("# ---- 设置页里的开关（0 = 关，1 = 开；在面板里点开关会自动写回这里）----");
        sb.AppendLine("# 开机自启（写 HKCU 的 Run 键，不需要管理员、不弹 UAC）");
        sb.AppendLine("autostart = " + IniText.Flag(AutoStart));
        sb.AppendLine();
        sb.AppendLine("# 启动服务后自动打开网页");
        sb.AppendLine("autoopen = " + IniText.Flag(AutoOpen));
        sb.AppendLine();
        sb.AppendLine("# 面板启动后自动最小化到托盘");
        sb.AppendLine("autotray = " + IniText.Flag(AutoTray));
        sb.AppendLine();
        sb.AppendLine("# 深色主题");
        sb.AppendLine("darktheme = " + IniText.Flag(DarkTheme));
        sb.AppendLine();
        sb.AppendLine("# 关闭面板时一并停止 DSH 服务");
        sb.AppendLine("stopwithpanel = " + IniText.Flag(StopWithPanel));
        sb.AppendLine();
        sb.AppendLine("# 服务意外停止时托盘气泡提示");
        sb.AppendLine("balloononstop = " + IniText.Flag(BalloonOnStop));
        sb.AppendLine();
        sb.AppendLine("# 开发者模式：每 20 秒往状态日志写一行心跳，便于判断面板是活着还是卡死");
        sb.AppendLine("devmode = " + IniText.Flag(DevMode));

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 没有写权限也无所谓，默认值在内存里照样能用
            Program.LogEvent("样板配置写入失败：" + ex.Message);
        }
    }
}
