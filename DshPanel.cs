using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DshPanel
{
    // 蓝色大肥鱼 · DSH 控制台
    // 托盘化控制面板：隐藏黑窗启动 dsh web、启停服务、最小化到系统托盘。
    // 编译：csc.exe /target:winexe /codepage:65001
    //       /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
    //       DshPanel.cs PanelTheme.cs PanelPlugins.cs PanelUi.cs PanelExtra.cs
    // 分工：界面与页面在 PanelUi.cs，配色与自绘控件在 PanelTheme.cs，
    //       插件（bundle）读写与开机自启在 PanelPlugins.cs / PanelExtra.cs。
    // IAnim 让窗体自己也能进动画列表：分体按钮、日志折叠、切页滑入三路插值都靠它驱动。
    public partial class PanelForm : Form, IAnim
    {
        // 配置来自 exe 同目录的 panel.config.ini（首次运行会生成一份带注释的样板）。
        // 这里特意保留下同名的静态属性，好让下面所有引用点原样不动、改动面最小。
        private static PanelConfig _cfg;

        private static int    Port    { get { return _cfg.Port;    } }
        private static string IcoFile { get { return _cfg.IcoFile; } }
        private static string SvcCmd  { get { return _cfg.SvcCmd;  } }
        private static string LogFile { get { return _cfg.LogFile; } }
        // 面板自身的生命周期日志。以前面板的日志只写窗口里那个 TextBox，进程一退原因就没了，
        // 「面板凭空消失」永远查不出是崩溃还是主动退出。这个文件专治这一点。
        private static string StateLog { get { return _cfg.StateLog; } }
        private static string WorkDir  { get { return _cfg.WorkDir;  } }
        private static string UrlBase  { get { return _cfg.UrlBase;  } }
        private static string AppTitle { get { return _cfg.Title;    } }

        // 插件启停页读写的就是这个文件（DSH profile 的 package.json）
        private static string PluginPath { get { return _cfg.PluginJson; } }

        private static Mutex _mtx;

        private NotifyIcon _tray;
        private Icon       _icon;
        private System.Windows.Forms.Timer _timer;
        // 注：界面控件全部声明在 PanelUi.cs（_lblState / _lblInfo / _log / _btnOpen /
        // _btnRestart / _btnWeb 等），这里只放窗口级对象，避免两个文件重复定义。

        private long     _logPos;
        private Decoder  _dec;           // 跨轮询保留 UTF-8 解码状态，避免多字节字符被切块截断
        private bool     _everRunning;   // 曾经处于运行中：用于"服务停止则面板自动关闭"
        private bool     _starting;      // 启动中：此阶段端口未监听属正常，不触发自动关闭
        private bool     _restarting;    // 本次拉起是「重启」（只有它才让重启图标转圈）
        internal bool    CleanMode;      // 当前跑的是「纯净 DSH」，界面上会标一下
        private bool     _quitting;
        private volatile bool _pendingOpen; // 启动完成后自动打开浏览器
        private DateTime _startAt;
        private string   _tokenUrl;      // 从 dsh 日志里抓到的带令牌地址
        private int      _openRetry;     // 等待带令牌地址出现时的重试计数
        private int      _probeFail;     // 端口查询连续失败次数：查询异常绝不等于"服务已停止"
        private int      _stopStreak;    // 连续确认端口消失的轮数，满 2 才允许自动退出

        // 服务运行期间锁住插件开关：此时热改 profile 会把正在跑的服务弄崩。
        
        // 开发者模式心跳计数：Tick 每 1.5 秒一次，累计到 13 次约 20 秒写一行状态。
        private int      _beatTicks;
        // 「服务意外停止」的托盘气泡只提示一次，别每轮都弹。
        private bool     _stopBalloonShown;
        // 启动失败提示在按钮上的保持截止时间（用户要求：失败提示做在按钮上，原因进日志）
        private DateTime _failUntil;
        private bool     _menuOpen;      // 托盘菜单展开期间冻结 NotifyIcon.Text，否则图标会消失

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDPIAware(); } catch { }

            // 配置最先加载：端口、路径、浏览器打开方式都由它决定。
            // 位置优先 exe 旁边（绿色版），那个目录不可写时退到 %LOCALAPPDATA%\DshPanel。
            string cfgPath = PanelConfig.PickConfigPath();
            _cfg = PanelConfig.Load(cfgPath);

            bool createdNew;
            _mtx = new Mutex(true, "DshPanel_SingleInstance_v1", out createdNew);

            // 命名 Mutex 会变成孤儿：面板被强杀（任务管理器 / 宿主作业对象回收）后句柄可能
            // 被别的进程继承而不释放，此后 createdNew 永远为 false，面板将再也打不开。
            // 所以用「同名进程是否真的存在」二次确认，孤儿 Mutex 直接忽略。
            if (!createdNew && AnotherPanelAlive())
            {
                // 注意：这里一个字都不写。日志属于那个正在运行的实例，
                // 第二个实例既不能清掉它，也不该往里掺东西。
                FocusExistingPanel();   // 已在运行：把现有面板调到前台，而不是默默退出
                return;
            }

            // 成为唯一的运行实例之后才重置：日志只保留「最后一次运行」的记录，
            // 上上次的启动/退出痕迹没有任何追因价值，留着只会干扰阅读。
            ResetStateLog();

            // 两类兜底异常：真崩溃也必须留下证据，不能只留一个空托盘。
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs ea)
            {
                LogEvent("【崩溃·UI 线程未处理异常】\r\n" + ea.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ea)
            {
                LogEvent("【崩溃·AppDomain 未处理异常】\r\n" + (ea.ExceptionObject == null ? "(null)" : ea.ExceptionObject.ToString()));
            };

            LogEvent("面板启动（PID " + Process.GetCurrentProcess().Id + "）");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PanelForm());

            LogEvent("面板正常退出（消息循环结束）");
            GC.KeepAlive(_mtx);
        }

        // 每次真正启动面板时清空一次：日志只保留最后一次运行的记录。
        private static void ResetStateLog()
        {
            try { File.WriteAllText(StateLog, "", Encoding.UTF8); } catch { }
        }

        // 面板自身的生命周期与异常落盘。任何 exit 都能事后查出原因。
        private static void LogEvent(string msg)
        {
            try
            {
                File.AppendAllText(StateLog,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }

        private static bool AnotherPanelAlive()
        {
            int me = Process.GetCurrentProcess().Id;
            Process[] all = null;
            try
            {
                all = Process.GetProcessesByName("DshPanel");
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Id != me) { return true; }
                }
            }
            catch { }
            finally
            {
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++) { all[i].Dispose(); }
                }
            }
            return false;
        }

        private static void FocusExistingPanel()
        {
            try
            {
                IntPtr h = FindWindow(null, AppTitle);
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9);        // SW_RESTORE：现有面板可能正缩在托盘里
                    SetForegroundWindow(h);
                }
            }
            catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            float s = _scale;
            Theme.S = s;                         // 自绘代码里的字面量靠它换算（见 Theme.Px）
            // 这行是给「界面被放大成 2 倍」这类问题留的证据：Once 面板一启动就把
            // 真实 DPI、算出来的缩放系数、以及窗体客户区尺寸写进状态日志。
            LogEvent("OnLoad  DPI=" + (int)Math.Round(SystemDpi() * 96f) + "  缩放=" + s.ToString("0.###") +
                     "  ClientSize=" + this.ClientSize.Width + "x" + this.ClientSize.Height);
            if (s > 0f && s != 1f)
            {
                ScaleLayout(this, s);            // 逐控件手写缩放，不依赖 Control.Scale
                // 字体不用在这里动：pt 是物理单位，系统在高 DPI 下已经把它渲染成 2 倍像素；
                // 屏幕放不下时的整体缩量已经在 Theme.FontScale 里处理过了。
                this.ClientSize = new Size(Theme.Px(W + 2), Theme.Px(H + 2));
                LogEvent("OnLoad  缩放后 ClientSize=" + this.ClientSize.Width + "x" + this.ClientSize.Height);
            }
            CenterOnScreen();
            // 日志页一打开就有东西可看（不然新装的面板点「日志」是一片空白）
            AppendLog("[面板] 控制台已启动 · 端口 " + Port + " · 缩放 " + s.ToString("0.##") + "\r\n");
        }

        // 打开时严格居中：StartPosition.CenterScreen 只按「缩放前」的尺寸算过一次，
        // OnLoad 里尺寸一变它就偏了 —— 所以缩放完成后自己再算一遍。
        private void CenterOnScreen()
        {
            Screen scr = Screen.FromControl(this);
            if (scr == null) { scr = Screen.PrimaryScreen; }
            if (scr == null) { return; }
            Rectangle wa = scr.WorkingArea;
            int x = wa.Left + (wa.Width - this.Width) / 2;
            int y = wa.Top + (wa.Height - this.Height) / 2;
            if (x < wa.Left) { x = wa.Left; }
            if (y < wa.Top) { y = wa.Top; }
            this.Location = new Point(x, y);
        }

        private float _scale = 1f;

        // 系统 DPI（不依赖窗口句柄，构造函数里就能问）
        private static float SystemDpi()
        {
            float s = 0f;
            try { s = GetDpiForSystem() / 96f; } catch { }
            if (s <= 0f)
            {
                try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { s = g.DpiX / 96f; } }
                catch { }
            }
            if (s <= 0f) { s = 1f; }
            return s;
        }

        private float DpiScale()
        {
            float s = 0f;
            try { s = GetDpiForWindow(this.Handle) / 96f; } catch { }
            if (s <= 0f) { s = SystemDpi(); }
            if (s <= 0f) { s = 1f; }
            return s;
        }

        // 【屏幕适配】设计尺寸是按 1000x780(@100%) 定的。1920x1080 起步的屏幕都放得下，
        // 但 1080p 挂 150% 缩放时逻辑区只有 1280x720 —— 面板会被切掉一截。
        // 所以缩放系数取 min(DPI比例, 屏幕能容纳的比例)，放不下就整体缩小。
        private void ComputeScale()
        {
            float dpi = DpiScale();
            float s   = dpi;

            Rectangle wa = Rectangle.Empty;
            try { wa = Screen.FromPoint(Cursor.Position).WorkingArea; } catch { }
            if (wa.Width <= 0 || wa.Height <= 0)
            {
                try { wa = Screen.PrimaryScreen.WorkingArea; } catch { }
            }
            if (wa.Width > 0 && wa.Height > 0)
            {
                // 留 5% 边距，别贴着任务栏和屏幕边缘
                float maxS = Math.Min(wa.Width * 0.95f / (W + 2), wa.Height * 0.95f / (H + 2));
                if (maxS < s) { s = maxS; }
            }

            if (s < 0.70f) { s = 0.70f; }
            if (s > 3.00f) { s = 3.00f; }

            // 【注意】这里只算 FontScale，不碰 Theme.S。
            // BuildUi 是按 100% 逻辑坐标搭界面的，此刻如果 Theme.S != 1，
            // 布局函数里的 Theme.Px() 会提前乘一次，随后 OnLoad 的 ScaleLayout 又乘一次 → 双重放大。
            _scale = s;
            Theme.FontScale = (dpi > 0.1f) ? (s / dpi) : 1f;
            if (Theme.FontScale > 1f) { Theme.FontScale = 1f; }
        }

        private static void ScaleLayout(Control parent, float s)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control c = parent.Controls[i];
                c.Location = new Point((int)Math.Round(c.Left * s), (int)Math.Round(c.Top * s));
                // 只有真正按内容自动撑开的 Label 才交给它自己算尺寸；
                // 多行 TextBox 的 AutoSize 是摆设，必须显式缩放，否则日志框不会跟着变大。
                bool autoSized = c.AutoSize && (c is Label);
                if (!autoSized)
                {
                    c.Size = new Size((int)Math.Round(c.Width * s), (int)Math.Round(c.Height * s));
                }
                ScaleLayout(c, s);
                // 滚动区自己记着「每行在内容坐标系里的位置」，bounds 放大了这份记录也必须放大，
                // 否则第一次滚动就会用逻辑坐标去摆物理坐标的行 → 行全叠在一起（用户反馈的「文字重叠」）。
                ScrollArea sa = c as ScrollArea;
                if (sa != null) { sa.ScaleContent(s); }
            }
        }

        // 无边框窗口（FormBorderStyle.None）默认不带 WS_MINIMIZEBOX，
        // 于是任务栏图标被点（面板本来就在最上层）时系统不知道该怎么收它 —— 表现就是「点了没反应」。
        // 补上 MINIMIZEBOX + SYSMENU，任务栏那套标准行为（点图标最小化/还原）才会生效。
        //
        // WS_EX_COMPOSITED（0x02000000）是「整窗原子合成」：窗口和它名下所有子窗口的绘制
        // 先落到同一张离屏表面，等这一轮画完整了才一次性送上屏幕。WinForms 的
        // DoubleBuffered / OptimizedDoubleBuffer 只管单个控件，兄弟控件之间照样是一个画完
        // 再画下一个，于是会看到「内容区已经变白、左边导航还是黑的」「启动时控件一个个冒出来」。
        // 只有把合成单位从「控件」提到「整窗」，屏幕上才只可能出现完整的一帧。
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style  |= 0x00020000;   // WS_MINIMIZEBOX
                cp.Style  |= 0x00080000;   // WS_SYSMENU
                cp.ExStyle |= 0x02000000;  // WS_EX_COMPOSITED
                return cp;
            }
        }

        public PanelForm()
        {
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.AutoScaleMode       = AutoScaleMode.None;
            this.Text                = AppTitle;
            // 【无边框】标题栏、主题/最小化/关闭按钮全部自绘（参考 302 布局）。
            // 因此最大化按钮这件事交给自绘按钮管，系统边框一概不要。
            this.FormBorderStyle     = FormBorderStyle.None;
            this.MaximizeBox         = false;
            this.MinimizeBox         = true;
            this.StartPosition       = FormStartPosition.Manual;         // 位置由 OnLoad→CenterOnScreen 算
            Theme.Dark               = _cfg.DarkTheme;                   // 先定主题再建界面
            this.BackColor           = Theme.Border;                     // 外层 1px 描边的颜色
            ComputeScale();          // 缩放系数必须在建界面之前算好（字号要按它定 pt）
            this.Font                = Theme.Ui(9F);
            this.DoubleBuffered      = true;

            LoadIcon();
            BuildUi();     // 页面与控件：PanelUi.cs
            BuildTray();   // 托盘图标与右键菜单

            // 点「-」就是普通最小化（留在任务栏），点 X 才收进托盘 —— 见 OnClosing。
            // 【不要】在这里挂 Resize 处理器去拦截最小化：Resize 处在窗口句柄生命周期的
            // 敏感期，在里面碰窗口可见性会重入，句柄重建时重入会直接终止进程。
            this.FormClosing += OnClosing;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1500;
            _timer.Tick += delegate { Tick(); };
            _timer.Start();

            Tick();
        }

        // 设置里的「启动后自动最小化到托盘」：必须在窗口真正显示之后再收，
        // 否则句柄还没建好。
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            bool tray = _cfg.AutoTray;
            if (tray)
            {
                HideToTray();
                AppendLog("[面板] 已按设置自动最小化到托盘。\r\n");
            }

            // 第一次启动（或者 DSH 被挪走了）：找不到数据目录就当场说清楚，别让人对着一屏红字猜。
            if (!_cfg.DshConfigured)
            {
                LogEvent("启动引导：没有找到 DSH 目录");
                AppendLog("[面板] 没有找到 DSH 目录（环境变量 DSH_HOME / %USERPROFILE%\\.dsh 都没有）。\r\n");
                if (tray) { return; }        // 已收进托盘就别弹窗打扰，用户点开面板会看到设置页提示
                ShowDshGuide();
            }
        }

        // 引导新用户指定 DSH 目录：直接把面板切到设置页，弹窗只说「去点哪里」。
        private void ShowDshGuide()
        {
            SwitchPage(1);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string home = _cfg.DshHome;
            string why;
            if (home == null || home.Length == 0)
            {
                why = "面板会先看环境变量 DSH_HOME，再看 " + userProfile + "\\.dsh —— 两处都没有找到。";
            }
            else
            {
                why = "配置里记的是：\r\n" + home + "\r\n但这个目录不存在，或者里面没有 profiles 子目录。";
            }

            MessageBox.Show(this,
                "没有找到 DeepSeek Harness 的数据目录。\r\n\r\n" + why + "\r\n\r\n" +
                "面板已经切到「设置」页，请在「DSH 目录」里点「选择…」，" +
                "指到 DSH 的数据目录（里面应当有一个 profiles 文件夹）。\r\n\r\n" +
                "如果你还没装过 DeepSeek Harness，先装好它，再回来点「重新自动探测」。",
                "需要先指定 DSH 目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 图标必须按尺寸取帧：new Icon(文件) 只拿到 ico 里的默认帧（通常 32×32），
        // 200% 缩放的任务栏需要 64~96 像素，被系统放大后就是糊的（用户实测反馈）。
        private void LoadIcon()
        {
            try
            {
                _icon = new Icon(IcoFile, 256, 256);
                this.Icon = _icon;
            }
            catch
            {
                try { _icon = new Icon(IcoFile); this.Icon = _icon; }
                catch { _icon = SystemIcons.Application; this.Icon = _icon; }
            }
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();

            // 通知区域有自己的尺寸（SystemInformation.SmallIconSize，200% 下是 32×32）。
            // 直接把 256 那一帧塞进去会被系统缩糊，所以单独按托盘尺寸取一帧。
            try
            {
                Size traySize = SystemInformation.SmallIconSize;
                _tray.Icon = new Icon(IcoFile, traySize.Width, traySize.Height);
            }
            catch { _tray.Icon = _icon; }

            _tray.Text    = CurrentTrayText();
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowPanel(); };

            // 【托盘菜单】跟界面同一套配色（自定义 Renderer，见 PanelTheme.cs），
            // 条目也按现在的功能重排：网页/启动/重启/停止/日志。
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer        = new TrayMenuRenderer();
            menu.Font            = Theme.Ui(9.5f);
            menu.BackColor       = Theme.Card;
            menu.ForeColor       = Theme.Text;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Padding         = new Padding(0, Theme.Px(4), 0, Theme.Px(4));

            ToolStripMenuItem miOpen    = new ToolStripMenuItem("打开面板",     null, delegate { ShowPanel(); });
            ToolStripMenuItem miWeb     = new ToolStripMenuItem("打开网页 DSH", null, delegate { LogEvent("托盘菜单：打开网页 DSH"); OpenDsh(); });
            ToolStripMenuItem miStart   = new ToolStripMenuItem("启动 DSH 服务", null, delegate { LogEvent("托盘菜单：启动 DSH 服务"); StartService(); });
            ToolStripMenuItem miRestart = new ToolStripMenuItem("重启 DSH",     null, delegate { LogEvent("托盘菜单：重启 DSH"); ConfirmRestart(); });
            ToolStripMenuItem miStop    = new ToolStripMenuItem("停止 DSH",     null, delegate { LogEvent("托盘菜单：停止 DSH"); ConfirmStop(); });
            ToolStripMenuItem miLog     = new ToolStripMenuItem("运行日志",     null, delegate { ShowPanel(); SwitchPage(2); });
            ToolStripMenuItem miQuit    = new ToolStripMenuItem("退出面板",     null, delegate { LogEvent("托盘菜单：退出面板"); Quit(); });
            miQuit.ForeColor = Theme.Danger;

            menu.Items.AddRange(new ToolStripItem[]
            {
                miOpen, new ToolStripSeparator(),
                miWeb, miStart, miRestart, miStop, new ToolStripSeparator(),
                miLog, new ToolStripSeparator(),
                miQuit
            });

            // 通知区域的老毛病：托盘菜单展开期间改写 NotifyIcon.Text，图标会直接从通知区域
            // 消失，而进程还活得好好的。「双击左键后图标不见了」多半就是它。
            menu.Opening += delegate
            {
                _menuOpen = true;
                // 按当前服务状态置灰，不让用户点到没意义的那一项
                bool run = IsPortListening(Port);
                miWeb.Enabled     = run;
                miStart.Enabled   = !run && !_starting;
                miRestart.Enabled = run;
                miStop.Enabled    = run;
            };
            menu.Closed  += delegate { _menuOpen = false; SafeTrayText(CurrentTrayText()); };

            // 不交给 shell 自己弹（它会以光标为左上角展开，在屏幕右下角就成了「往左上长」，
            // 贴着任务栏还容易被切掉）。改成自己按「光标右上方」展开，跟绝大多数托盘程序一致。
            _tray.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    Point p = Cursor.Position;
                    menu.Show(p, ToolStripDropDownDirection.AboveRight);
                    return;
                }
                // 左键单击也叫出面板：托盘程序的常规习惯，省得用户琢磨要不要双击。
                if (e.Button == MouseButtons.Left) { ShowPanel(); }
            };
        }

        private Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text      = text;
            b.Location  = new Point(x, y);
            b.Size      = new Size(w, h);
            b.FlatStyle = FlatStyle.System;
            this.Controls.Add(b);
            return b;
        }

        // ---------- 端口状态 ----------

        // 纯托管 API 读取本机监听端点：不起子进程、不产生连接副作用。
        // 三态返回：1 = 在监听，0 = 确定没在监听，-1 = 查询本身失败。
        // 旧实现 catch { } 后 return false，把「GetActiveTcpListeners 抛异常」和「服务真的停了」
        // 混成同一个结果，于是偶发一次系统调用异常就足以让 Tick 判定服务已停止并自杀。
        private static int ProbePort(int port)
        {
            try
            {
                IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
                IPEndPoint[] eps = props.GetActiveTcpListeners();
                for (int i = 0; i < eps.Length; i++)
                {
                    if (eps[i].Port == port) { return 1; }
                }
                return 0;
            }
            catch { return -1; }
        }

        private static bool IsPortListening(int port)
        {
            return ProbePort(port) == 1;
        }

        private static int FindPidOnPort(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP");
                psi.UseShellExecute        = false;
                psi.CreateNoWindow         = true;
                psi.StandardOutputEncoding = Encoding.Default;
                psi.RedirectStandardOutput = true;

                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);

                    string[] lines = outp.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0) { continue; }
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                        Match m = Regex.Match(line, ":" + port + @"\s+\S+\s+LISTENING\s+(\d+)");
                        if (m.Success)
                        {
                            int pid;
                            if (int.TryParse(m.Groups[1].Value, out pid)) { return pid; }
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        // ---------- 启停 ----------

        // 找本机已安装的 dsh 入口（node bin.js）。
        // 为什么要绕开 npx：npx 每次启动都要走一遍 npm 的解析/缓存流程，而重启流程刚
        // taskkill 掉上一棵 node 进程树时，npm 的 _cacache 锁/临时目录常常还没释放，
        // npx 就会卡在锁等待上 —— 表现出来正是「重启后 180 秒都没有端口监听」。
        // 直接 `node <bin.js>` 完全不碰 npm，启动是确定性的。
        private static string ResolveDshBin()
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) { return null; }

            string root = Path.Combine(local, Path.Combine("npm-cache", "_npx"));
            try
            {
                if (!Directory.Exists(root)) { return null; }
                string   best     = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string bin = Path.Combine(dir, Path.Combine("node_modules",
                                 Path.Combine("@deepseek-ai",
                                 Path.Combine("dsh", Path.Combine("lib", "bin.js")))));
                    if (!File.Exists(bin)) { continue; }
                    DateTime t = File.GetLastWriteTime(bin);
                    if (t > bestTime) { bestTime = t; best = bin; }
                }
                return best;
            }
            catch { return null; }
        }

        // 生成（覆盖）一个启动脚本并返回路径；how 用来写日志，标明走的是哪条启动链
        private string WriteLaunchScript(string fileName, string dshArgs, out string how)
        {
            string bin = ResolveDshBin();
            string cmdline;
            if (bin != null)
            {
                cmdline = "node \"" + bin + "\" " + dshArgs;
                how     = "node 直连";
            }
            else
            {
                cmdline = "npx.cmd @deepseek-ai/dsh " + dshArgs;
                how     = "npx（没找到本地 bin.js，退回慢速通道）";
            }

            string dir = Path.GetDirectoryName(SvcCmd);
            if (string.IsNullOrEmpty(dir)) { dir = Path.GetDirectoryName(LogFile); }
            string script = Path.Combine(dir, fileName);

            string body =
                "@echo off\r\n" +
                "cd /d \"" + WorkDir + "\"\r\n" +
                cmdline + " --port " + Port + " --no-open > \"" + LogFile + "\" 2>&1\r\n";

            File.WriteAllText(script, body, Encoding.Default);
            return script;
        }

        // 把服务日志的末尾几行抄进面板日志区 —— 启动失败时用户（和我）一眼就能看到
        // node/npm 到底报了什么错，不用再去翻 panel.log。
        private void DumpServiceTailToLog()
        {
            try
            {
                if (!File.Exists(LogFile)) { return; }
                string text;
                using (FileStream fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int take = (int)Math.Min(1600, fs.Length);
                    if (take <= 0) { return; }
                    fs.Seek(-take, SeekOrigin.End);
                    byte[] buf = new byte[take];
                    int n = fs.Read(buf, 0, take);
                    text = Encoding.UTF8.GetString(buf, 0, n);
                }
                int nl = text.IndexOf('\n');
                if (nl >= 0 && nl < text.Length - 1) { text = text.Substring(nl + 1); }  // 丢掉可能被截断的首行
                text = text.Trim();
                if (text.Length == 0) { return; }
                AppendLog("\r\n[面板] 服务日志最后几行：\r\n" + text + "\r\n");
            }
            catch { }
        }

        private void StartService()
        {
            if (IsPortListening(Port))
            {
                AppendLog("[面板] 服务已在运行。\r\n");
                LogEvent("启动被跳过：端口 " + Port + " 已在监听");
                return;
            }
            if (_starting) { return; }

            try { if (File.Exists(LogFile)) { File.Delete(LogFile); } }
            catch { }
            _logPos   = 0;
            _tokenUrl = null;
            _log.Clear();
            CleanMode = false;   // 普通启动：清掉「纯净」标记

            // 用面板自己生成的启动脚本（node 直连 bin.js，绕开 npx 的缓存锁）
            string script; string how;
            try
            {
                script = WriteLaunchScript("dsh-run.cmd", "web", out how);
                LogEvent("启动脚本：" + script + "（" + how + "）");
            }
            catch (Exception ex)
            {
                AppendLog("[面板] 生成启动脚本失败：" + ex.Message + "\r\n");
                LogEvent("生成启动脚本失败：" + ex.Message);
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName        = "cmd.exe";
                psi.Arguments       = "/c \"" + script + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow  = true;
                psi.WindowStyle     = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = WorkDir;

                Process.Start(psi);   // 不持有句柄：面板退出不会连带杀掉服务

                _starting = true;
                _startAt  = DateTime.Now;
                AppendLog("[面板] 正在启动 DSH 服务（端口 " + Port + "，" + how + "）…\r\n");
                LogEvent("开始启动 DSH 服务");
            }
            catch (Exception ex)
            {
                AppendLog("[面板] 启动失败：" + ex.Message + "\r\n");
                LogEvent("启动 DSH 服务失败：" + ex.Message);
                _starting = false;
            }
            UpdateUi(false);
        }

        // ---------------------------------------------------------------------
        //  纯净 DSH（排障用）
        //  官方 CLI 自带这条逃生通道：`dsh rescue --from-default-profile web`
        //  = 用发行版自带的 web 模板新建一个只含官方默认 bundle 的 profile（不含
        //  任何第三方插件）并启动它。profile 目录已存在时同一条命令会报
        //  「profile "rescue" already exists」，所以第二次起要换成 `--profile rescue`。
        // ---------------------------------------------------------------------
        internal void StartCleanDsh()
        {
            if (IsPortListening(Port))
            {
                AppendLog("[面板] 启动纯净 DSH 被跳过：端口 " + Port + " 已被占用，请先停止当前服务。\r\n");
                LogEvent("启动纯净 DSH 被跳过：端口被占用");
                return;
            }
            if (_starting) { return; }

            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }
            bool exists = File.Exists(Path.Combine(home, Path.Combine("profiles", Path.Combine("rescue", "package.json"))));

            string script; string how;
            try
            {
                script = WriteLaunchScript("dsh-clean.cmd",
                                           exists ? "--profile rescue" : "rescue --from-default-profile web",
                                           out how);
                LogEvent("已生成纯净 DSH 启动脚本：" + script + "（" + how + "，" +
                         (exists ? "复用已有 rescue profile" : "首次创建 rescue profile") + "）");
            }
            catch (Exception ex)
            {
                AppendLog("[面板] 生成纯净 DSH 脚本失败：" + ex.Message + "\r\n");
                LogEvent("生成纯净 DSH 脚本失败：" + ex.Message);
                return;
            }

            try { if (File.Exists(LogFile)) { File.Delete(LogFile); } }
            catch { }
            _logPos   = 0;
            _tokenUrl = null;
            _log.Clear();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName         = "cmd.exe";
                psi.Arguments        = "/c \"" + script + "\"";
                psi.UseShellExecute  = false;
                psi.CreateNoWindow   = true;
                psi.WindowStyle      = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = WorkDir;

                Process.Start(psi);   // 不持有句柄

                _starting  = true;
                _restarting = false;  // 纯净启动不是「重启」，图标别转
                CleanMode = true;
                _startAt   = DateTime.Now;
                AppendLog("[面板] 正在启动「纯净 DSH」（官方默认插件集，端口 " + Port + "）…\r\n");
                LogEvent("开始启动纯净 DSH");
            }
            catch (Exception ex)
            {
                AppendLog("[面板] 启动纯净 DSH 失败：" + ex.Message + "\r\n");
                LogEvent("启动纯净 DSH 失败：" + ex.Message);
                _starting = false;
            }
            UpdateUi(false);
        }

        private void OpenDsh()
        {
            _openRetry   = 0;
            _failUntil   = DateTime.MinValue;

            if (IsPortListening(Port))
            {
                OpenBrowser();
                return;
            }

            _pendingOpen = true;   // 等端口就绪后由 Tick 自动打开
            StartService();
        }

        // 只打开网页、完全不碰启动逻辑：给服务控制卡里那个纯图案按钮用。
        private void OpenWebOnly()
        {
            _openRetry = 0;
            LogEvent("按钮：打开网页");
            OpenBrowser();
        }

        // 用户要求：启动失败的提示做在「启动」按钮上（不是弹对话框），
        // 失败原因丢进日志，这样窗口不打断手头的事，事后又能查。
        private void FailStart(string reason)
        {
            _starting  = false;
            _failUntil = DateTime.Now.AddSeconds(6);
            if (_btnOpen != null) { _btnOpen.Text = "✗ 启动失败"; }
            AppendLog("\r\n[面板] 启动失败：" + reason + "\r\n");
            LogEvent("启动失败：" + reason);
            DumpServiceTailToLog();   // 顺手把服务日志尾巴贴出来，免得只有结论没有原因
        }

        // 重启 = 停 + 起 + 自动打开网页，把原来「先点关闭、再点启动」两步合成一步。
        private void ConfirmRestart()
        {
            DialogResult r = MessageBox.Show(
                "要重启 DSH 服务吗？\r\n\r\n正在使用的 DSH 网页会短暂断开，服务起来后面板会自动帮你重新打开。",
                "重启 DSH",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { return; }

            LogEvent("用户确认重启 DSH 服务");
            AppendLog("\r\n[面板] 开始重启 DSH…\r\n");

            _stopStreak  = 0;
            _openRetry   = 0;
            _pendingOpen = false;

            // 内含等待端口释放 + 一次 Tick 刷新；返回 false = 没停下来，此时绝不能继续启动，
            // 否则 StartService 会撞上「端口仍在监听」，静默返回，用户看到的就是「点了没反应」。
            if (!StopDsh(false))
            {
                LogEvent("重启中止：DSH 未能在超时前停止");
                AppendLog("[面板] 重启中止：DSH 没能停下来，请稍后重试。\r\n");
                MessageBox.Show(
                    "DSH 没能停下来（端口 " + Port + " 仍被占用），重启已中止。\r\n\r\n" +
                    "请稍后再试一次；若持续失败，可在任务管理器里结束占用该端口的进程。",
                    "重启中止",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // StopDsh 在「端口本来就没在监听」时会原样返回、不重置 _everRunning，
            // 这里统一清掉，免得随后的 Tick 把「服务不在」误判成「服务刚停止」而自杀。
            _everRunning = false;
            _stopStreak  = 0;
            _probeFail   = 0;

            // 用户明确要求：重启不碰浏览器，由他自己在已开的页面上按 F5。
            // （重启后 DSH 会换新的令牌地址，旧页面若提示未授权，用主按钮取新地址即可。）
            _pendingOpen = false;
            _restarting  = true;     // 只有这次拉起是重启 → 重启图标转圈
            StartService();
            AppendLog("[面板] DSH 已重启，请在网页上按 F5 刷新；若提示未授权，点「打开网页 DSH」。\r\n");
        }

        // 关闭 DSH 会 taskkill 掉整棵进程树，正在使用网页版的页面会立即断开，所以要确认。
        private void ConfirmStop()
        {
            DialogResult r = MessageBox.Show(
                "确定要关闭 DSH 服务吗？\r\n\r\n正在使用的 DSH 网页会立即断开连接。",
                "关闭 DSH",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
            {
                LogEvent("用户确认关闭 DSH 服务");
                StopDsh(true);
            }
        }

        // 返回 true 表示「端口上确实没有服务在跑了」（本来就没在跑也算 true），
        // false 表示超时后端口仍被占用、没能停下来 —— 调用方必须据此中止后续动作。
        private bool StopDsh(bool exitAfter)
        {
            int pid = FindPidOnPort(Port);
            if (pid <= 0)
            {
                AppendLog("[面板] 未发现监听 " + Port + " 的进程。\r\n");
                _pendingOpen = false;
                _starting    = false;
                if (exitAfter) { _everRunning = false; Quit(); }
                return true;
            }

            AppendLog("[面板] 正在停止 DSH（PID " + pid + "）…\r\n");
            LogEvent("正在停止 DSH（PID " + pid + "）");
            RunHidden("taskkill.exe", "/PID " + pid + " /T /F");

            for (int i = 0; i < 25; i++)
            {
                Thread.Sleep(200);
                Application.DoEvents();
                if (!IsPortListening(Port)) { break; }
            }

            bool still = IsPortListening(Port);
            AppendLog(still ? "[面板] 停止失败：端口仍被占用。\r\n" : "[面板] DSH 已停止。\r\n");

            if (!still)
            {
                _starting    = false;
                _pendingOpen = false;
                if (exitAfter)
                {
                    _everRunning = false;   // 主动停止，不由 Tick 再触发一次
                    AppendLog("[面板] 服务已关闭，控制台自动关闭。\r\n");
                    LogEvent("DSH 已停止，面板随服务一起退出");
                    Quit();
                }
                else { Tick(); }
            }
            else
            {
                // 必须留痕：以前这里只写窗口不写日志，于是「重启没反应」事后完全查不到原因。
                LogEvent("停止 DSH 失败：端口 " + Port + " 仍被占用（PID " + pid + "）");
            }

            return !still;
        }

        private static void RunHidden(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow  = true;
                psi.WindowStyle     = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                p.WaitForExit(8000);
            }
            catch { }
        }

        // ---------- 轮询 ----------

        private void Tick()
        {
            if (_quitting) { return; }

            ScanLog();

            int probe = ProbePort(Port);
            if (probe < 0)
            {
                // 查询失败既不等于"在运行"也不等于"已停止"。保持界面与内部状态原样，等下一轮再说。
                // 这一段就是"面板凭空消失"的根治点：以前这里会被当成"服务已停止"。
                _probeFail++;
                if (_probeFail == 3)
                {
                    AppendLog("[面板] 端口状态查询失败（已连续 3 次），暂不判定服务已停止。\r\n");
                    LogEvent("端口状态查询连续失败 3 次");
                }
                return;
            }
            _probeFail = 0;
            bool running = (probe == 1);

            if (running)
            {
                _stopStreak  = 0;
                _everRunning = true;
                _starting    = false;
                _stopBalloonShown = false;
                if (_pendingOpen)
                {
                    _pendingOpen = false;
                    OpenBrowser();
                }
            }
            else if (_starting)
            {
                if ((DateTime.Now - _startAt).TotalSeconds > 90)
                {
                    AppendLog("\r\n[面板] 启动超时：90 秒内未检测到端口监听，请查看上方日志。\r\n");
                    FailStart("90 秒内未检测到端口监听");
                }
            }
            else if (_everRunning)
            {
                // 连续两轮都确认端口消失才收工，单次抖动不再让面板自杀。
                _stopStreak++;
                if (_stopStreak < 2)
                {
                    AppendLog("\r\n[面板] 未检测到端口监听，正在确认…\r\n");
                    UpdateUi(false);
                    return;
                }

                // 服务"意外停止"的托盘气泡（用户开的开关），只提示一次。
                if (_cfg.BalloonOnStop && !_stopBalloonShown && _tray != null)
                {
                    _stopBalloonShown = true;
                    try
                    {
                        _tray.ShowBalloonTip(5000, "DSH 已停止",
                            "DSH 服务已意外停止，控制面板即将自动关闭。", ToolTipIcon.Warning);
                    }
                    catch { }
                }

                AppendLog("\r\n[面板] 检测到 DSH 已停止，控制台自动关闭。\r\n");
                LogEvent("检测到 DSH 已停止，面板自动关闭");
                _everRunning = false;
                Quit();
                return;
            }

            UpdateUi(running);

            // 开发者模式心跳：每约 20 秒（Tick 是 1.5 秒一次）往状态日志写一行。
            // 托盘程序最难查的故障是"看起来还在、其实已经卡死"，心跳就是给它做尸检用的。
            if (_cfg.DevMode)
            {
                _beatTicks++;
                if (_beatTicks >= 13)
                {
                    _beatTicks = 0;
                    LogEvent("heartbeat  " + _lblState.Text + "  端口 " + Port + "  " +
                             (running ? "监听中" : "未监听"));
                }
            }
            else { _beatTicks = 0; }
        }

        private void UpdateUi(bool running)
        {
            // 文案与按钮形态全部交给界面层：SetServiceState 会顺带驱动
            // 「主按钮缩短 + 右侧浮现重启/网页图标」的分体动画。
            if (_lblInfo != null)
            {
                if (running)       { _lblInfo.Text = UrlBase + " · 端口 " + Port; }
                else if (_starting) { _lblInfo.Text = "正在拉起服务，请稍候…"; }
                else               { _lblInfo.Text = "未运行 · 端口 " + Port; }
            }

            SetServiceState(running, _starting, _restarting);
            // 连上之后本次「重启」就算结束了，标志位要清掉，否则下次普通启动又会转圈
            if (running) { _restarting = false; }
            if (!running && !_starting) { CleanMode = false; }

            // 服务在跑（或正在起来）就锁住插件开关：此时热改 profile 会把服务弄崩。
            // 用户明确要求：启动服务之后插件开关要变灰。
            LockPluginRows(running || _starting);

            // 启动失败时按钮上保持 6 秒「✗ 启动失败」，之后自动回到正常文案
            if (DateTime.Now < _failUntil && _btnOpen != null) { _btnOpen.Text = "✗ 启动失败"; }

            RefreshNetInfo();
            SafeTrayText(CurrentTrayText());
        }

        // 日志默认自动滚到末尾（用户要求：自动滚动是默认行为，不做成开关）
        private void ScrollLogToEnd()
        {
            if (_log == null) { return; }
            try
            {
                _log.SelectionStart  = _log.TextLength;
                _log.SelectionLength = 0;
                _log.ScrollToCaret();
            }
            catch { }
        }

        private string CurrentTrayText()
        {
            if (_lblState == null || _lblState.Text == null) { return "蓝色大肥鱼 · DSH"; }
            if (_lblState.Text == "运行中")   { return "蓝色大肥鱼 · DSH（运行中）"; }
            if (_lblState.Text == "启动中…") { return "蓝色大肥鱼 · DSH（启动中）"; }
            return "蓝色大肥鱼 · DSH（已停止）";
        }

        // 托盘菜单展开时不碰 NotifyIcon.Text —— 否则图标会从通知区域消失而进程仍在。
        private void SafeTrayText(string t)
        {
            if (_menuOpen || _tray == null) { return; }
            try { _tray.Text = t; }
            catch { }
        }

        // ---------- 日志（重定向到文件后增量读取，避免管道 EPIPE 杀死服务） ----------

        private void ScanLog()
        {
            try
            {
                if (!File.Exists(LogFile)) { return; }

                using (FileStream fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < _logPos) { _logPos = 0; _dec = null; }   // 文件被重建
                    if (fs.Length == _logPos) { return; }

                    fs.Seek(_logPos, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - _logPos];
                    int n = fs.Read(buf, 0, buf.Length);
                    _logPos = fs.Position;

                    // 用有状态的 Decoder 解码：日志是分块增量读的，直接 GetString 会在块边界
                    // 切断多字节字符，日志里就会出现乱码方块。
                    if (_dec == null) { _dec = Encoding.UTF8.GetDecoder(); }
                    char[] chars = new char[Encoding.UTF8.GetMaxCharCount(n)];
                    int cn = _dec.GetChars(buf, 0, n, chars, 0);
                    string text = new string(chars, 0, cn);

                    text = Regex.Replace(text, "\x1b\\[[0-9;]*[A-Za-z]", "");
                    // Node 在 Windows 上只输出裸 \n，而 WinForms TextBox 只按 \r\n 分行，
                    // 不规范化的话整份日志会挤成一坨。
                    text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

                    Match m = Regex.Match(text, "https?://127\\.0\\.0\\.1:" + Port + "[^\\s\"'<>]*");
                    if (m.Success) { _tokenUrl = m.Value; }

                    AppendLog(text);
                }
            }
            catch { }
        }

        private void AppendLog(string s)
        {
            if (_log == null || s == null || s.Length == 0) { return; }
            if (_quitting) { return; }

            if (_log.InvokeRequired)
            {
                try { _log.BeginInvoke((MethodInvoker)delegate { AppendLog(s); }); }
                catch { }
                return;
            }

            _log.AppendText(s);
            if (_log.TextLength > 60000)
            {
                // 从整行边界截断，别把半行留在最上面
                int cut = _log.TextLength - 20000;
                int nl  = _log.Text.IndexOf("\r\n", cut);
                _log.Text = (nl >= 0) ? _log.Text.Substring(nl + 2) : _log.Text.Substring(cut);
            }

            ScrollLogToEnd();
        }

        private void OpenBrowser()
        {
            // dsh web 是先让端口进入 LISTENING、过一会儿才在日志里打印带进程令牌的启动地址
            // （要等 loader 与启动审计走完）。端口一就绪就拿裸地址去开浏览器会因为缺少令牌而
            // 401 —— 现象就是"窗口打开了但连不上"。所以令牌地址出来之前只等不开。
            string url = _tokenUrl;
            if (url == null || url.Length == 0)
            {
                if (_openRetry < 20)
                {
                    _openRetry++;
                    _pendingOpen = true;   // 交给下一次 Tick 继续等
                    if (_openRetry == 1)
                    {
                        AppendLog("[面板] 等待服务打印带令牌的启动地址…\r\n");
                    }
                    return;
                }
                url = UrlBase;
                AppendLog("[面板] 未取到令牌地址，回退为 " + url + "。\r\n");
            }

            _pendingOpen = false;
            try
            {
                OpenUrl(url);
            }
            catch (Exception ex)
            {
                AppendLog("[面板] 打开浏览器失败：" + ex.Message + "\r\n");
                LogEvent("打开浏览器失败：" + ex.Message);
            }
        }

        // 打开 DSH 网页。app 模式用 Chromium 系的 --app 开一个无地址栏的独立窗口，
        // 并给它一个独立的用户配置目录 —— 这一点很关键：Chromium 的 --user-data-dir 是
        // 独占的，多实例共用同一个目录时，后启动的进程不会开新窗口，而是把命令行交给
        // 已有实例然后自己立刻退出。任何"靠浏览器进程是否存活来判断窗口是否关闭"的逻辑，
        // 都会因此误判成"窗口关了"。（本机另有一个 dsh-win-quick-launcher 正是这么翻车的。）
        // tab 模式则交回系统默认浏览器。
        private void OpenUrl(string url)
        {
            string exe = _cfg.BrowserPath;
            if (exe != null && exe.Length > 0 && !File.Exists(exe)) { exe = null; }

            // 1) 无地址栏独立窗口：只有 Chromium 系认 --app，Firefox/IE 传了也白传。
            if (_cfg.BrowserMode == "app")
            {
                if (exe == null || exe.Length == 0) { exe = FindBrowser(); }
                if (exe != null && exe.Length > 0 && File.Exists(exe) && IsChromium(exe))
                {
                    string profile = _cfg.BrowserProfile;
                    if (profile == null || profile.Length == 0)
                    {
                        // 跟着面板自己的数据目录走，不再写死 %LOCALAPPDATA%\DSH
                        profile = Path.Combine(PanelConfig.DataDir, "browser-profile");
                    }
                    try { Directory.CreateDirectory(profile); } catch { }

                    string args = "--app=\"" + url + "\""
                                + " --user-data-dir=\"" + profile + "\""
                                + " --no-first-run --no-default-browser-check --disable-background-mode";
                    ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    AppendLog("[面板] 已用应用窗口打开：" + url + "\r\n");
                    LogEvent("已用应用窗口打开（" + Path.GetFileName(exe) + "）：" + url);
                    return;
                }
                AppendLog("[面板] 选中的浏览器不支持应用窗口，改用普通窗口。\r\n");
                LogEvent("app 模式找不到可用的 Chromium，回退为普通窗口");
            }

            // 2) 普通窗口：用户指定过浏览器就用它，否则交给系统默认。
            if (exe != null && exe.Length > 0 && File.Exists(exe))
            {
                ProcessStartInfo p2 = new ProcessStartInfo(exe, "\"" + url + "\"");
                p2.UseShellExecute = false;
                Process.Start(p2);
                AppendLog("[面板] 已用 " + Path.GetFileName(exe) + " 打开：" + url + "\r\n");
                LogEvent("已用 " + Path.GetFileName(exe) + " 打开：" + url);
                return;
            }

            ProcessStartInfo pin = new ProcessStartInfo(url);
            pin.UseShellExecute = true;
            Process.Start(pin);
            AppendLog("[面板] 已用默认浏览器打开：" + url + "\r\n");
            LogEvent("已用默认浏览器打开：" + url);
        }

        // --app 只有 Chromium 系支持；Firefox / IE 会把它当命令行参数直接忽略，
        // 结果就是开出一堆莫名其妙的标签页。所以在选它们时主动降级。
        private static bool IsChromium(string exe)
        {
            try
            {
                string n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                return n != "firefox" && n != "iexplore";
            }
            catch { return true; }
        }

        // 自动探测 Chromium 系浏览器。先扫 %LOCALAPPDATA% —— Chrome 安装器的默认选项
        // （单用户安装）就装在那里，不在 Program Files；漏掉这个目录是同类工具常见的坑。
        private static string FindBrowser()
        {
            string pf  = Environment.GetEnvironmentVariable("ProgramFiles");
            string pfx = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] roots = new string[] { lad, pf, pfx };
            string[] rels = new string[]
            {
                @"Google\Chrome\Application\chrome.exe",
                @"Microsoft\Edge\Application\msedge.exe",
                @"BraveSoftware\Brave-Browser\Application\brave.exe",
                @"Vivaldi\Application\vivaldi.exe",
                @"Chromium\Application\chrome.exe",
            };
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] == null || roots[i].Length == 0) { continue; }
                for (int j = 0; j < rels.Length; j++)
                {
                    string p = Path.Combine(roots[i], rels[j]);
                    try { if (File.Exists(p)) { return p; } } catch { }
                }
            }
            return null;
        }

        // 设置页「默认浏览器」下拉里列出来的候选：名称 + 完整路径，去重保序。
        private static List<string[]> FindBrowsers()
        {
            string pf  = Environment.GetEnvironmentVariable("ProgramFiles");
            string pfx = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] roots = new string[] { lad, pf, pfx };
            string[][] cand = new string[][]
            {
                new string[] { "Chrome",   @"Google\Chrome\Application\chrome.exe" },
                new string[] { "Edge",     @"Microsoft\Edge\Application\msedge.exe" },
                new string[] { "Brave",    @"BraveSoftware\Brave-Browser\Application\brave.exe" },
                new string[] { "Vivaldi",  @"Vivaldi\Application\vivaldi.exe" },
                new string[] { "Chromium", @"Chromium\Application\chrome.exe" },
                new string[] { "Firefox",  @"Mozilla Firefox\firefox.exe" },
            };

            List<string[]> outp = new List<string[]>();
            for (int j = 0; j < cand.Length; j++)
            {
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] == null || roots[i].Length == 0) { continue; }
                    string p = Path.Combine(roots[i], cand[j][1]);
                    bool ok = false;
                    try { ok = File.Exists(p); } catch { }
                    if (!ok) { continue; }
                    bool dup = false;
                    for (int k = 0; k < outp.Count; k++)
                    {
                        if (string.Equals(outp[k][1], p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    }
                    if (!dup) { outp.Add(new string[] { cand[j][0], p }); }
                    break;   // 同一个浏览器只取第一个命中的安装位置
                }
            }
            return outp;
        }

        // ---------- 托盘 / 窗口 ----------

        // 【绝对不要】在这两个方法里碰 ShowInTaskbar。
        // 运行期修改 ShowInTaskbar 会走 RecreateHandle()（销毁并重建窗口句柄），而
        // HideToTray 正是从 FormClosing 里被调用的 —— 在句柄重建过程中重入会以
        // StackOverflow / AccessViolation 直接终止进程，且 Application.ThreadException 与
        // AppDomain.UnhandledException 都来不及跑（日志空白正是这个特征，曾误导我以为是
        // 外部强杀）。Hide() / Show() 本身已经正确处理任务栏按钮，无需手动切换。
        private void HideToTray()
        {
            this.Hide();
        }

        private void ShowPanel()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            // 点「-」不会走到这里（Resize 处理器已移除，最小化只是最小化）。
            if (!_quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;   // 点 X = 收进托盘，托盘菜单里才真正退出
                HideToTray();
                LogEvent("窗口收进托盘");
            }
        }

        private void Quit()
        {
            if (_quitting) { return; }

            // 用户开的开关：关闭面板时一并停掉 DSH 服务。
            // 默认关，保持「面板退出不动服务」的老行为。
            if (_cfg.StopWithPanel && IsPortListening(Port))
            {
                LogEvent("按设置一并停止 DSH 服务");
                StopDsh(false);
            }

            _quitting = true;

            LogEvent("Quit()：面板即将退出");
            try { _timer.Stop(); }
            catch { }
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch { }

            Application.Exit();
        }
    }

    // ---------- 配置 ----------

    // 与 exe 同目录的 panel.config.ini：纯文本 key=value，# 或 ; 开头是注释。
    // 之所以不用 JSON：.NET Framework 没有内置解析器，引第三方库又违反「零依赖单文件」；
    // 而这里的配置项全是平铺的标量，INI 反而更好手写、更好读。
    // 解析失败一律静默退回默认值 —— 配置坏了不该让面板起不来。
    internal sealed class PanelConfig
    {
        // ---------------- 路径默认值：全部按「任意一台机器」推导 ----------------
        // 以前这里写死了作者机器上的 C:\Users\lqh\...，发给别人必然全错。
        // 现在：面板自己的数据（日志/脚本）放 %LOCALAPPDATA%\DshPanel，
        //       DSH 的数据目录（profiles）自动探测，工作目录默认是用户主目录。

        // 面板自身的数据目录。%LOCALAPPDATA% 不可写时退回 exe 目录，再退回临时目录。
        public static readonly string DataDir = ResolveDataDir();

        // DSH 的数据目录（= DSH_HOME）。空字符串 = 没找到，需要用户在设置里指定。
        public string DshHome = ProbeDefaultDshHome();

        public int    Port    = 3080;
        public string SvcCmd  = Path.Combine(DataDir, "dsh-service.cmd");
        public string LogFile = Path.Combine(DataDir, "panel.log");
        public string StateLog = Path.Combine(DataDir, "panel-state.log");
        public string IcoFile = Path.Combine(ExeDir(), "DeepSeekHarness-WhaleGirl.ico");
        public string WorkDir = DefaultWorkDir();
        public string Title   = "蓝色大肥鱼 · DSH 控制台";
        public string BrowserMode    = "app";   // app = 独立应用窗口 | tab = 系统默认浏览器标签页
        public string BrowserPath    = "";      // 留空 = 自动探测
        public string BrowserProfile = "";      // 留空 = %LOCALAPPDATA%\DSH\browser-profile
        // 插件列表文件。留空 = 跟着 DshHome 走（见下面的 PluginJson 属性）。
        public string PluginJsonRaw  = "";

        // ---- 设置页里的开关（全部由面板自己写回本文件）----
        public bool AutoStart     = false;  // 开机自启（写 HKCU 的 Run 键，不触发 UAC）
        public bool AutoOpen      = true;   // 启动服务后自动打开网页
        public bool AutoTray      = false;  // 启动后自动最小化到托盘
        public bool DarkTheme     = true;   // 深色主题（这是面板的默认外观，浅色可以在配置里关掉）
        public bool StopWithPanel = false;  // 关闭面板时一并停止 DSH 服务
        public bool BalloonOnStop = true;   // 服务意外停止时托盘气泡提示
        public bool DevMode       = false;  // 开发者模式：每 20 秒写一行状态心跳

        // 插件列表：显式给了 pluginjson 就用它，否则从 DSH 目录推出来
        public string PluginJson
        {
            get
            {
                if (!string.IsNullOrEmpty(PluginJsonRaw)) { return PluginJsonRaw; }
                if (string.IsNullOrEmpty(DshHome)) { return ""; }
                return Path.Combine(Path.Combine(DshHome, "profiles"), Path.Combine("web", "package.json"));
            }
        }

        // web profile 的目录（插件 package.json 就在里面）
        public string WebProfileDir
        {
            get
            {
                if (string.IsNullOrEmpty(DshHome)) { return ""; }
                return Path.Combine(Path.Combine(DshHome, "profiles"), "web");
            }
        }

        // 「DSH 目录可用」：既配了路径、目录也真的在
        public bool DshConfigured
        {
            get { return !string.IsNullOrEmpty(DshHome) && Directory.Exists(DshHome); }
        }

        private string _path;
        // 注意：这里不能叫 Path —— 会和 System.IO.Path 撞名，静态辅助里的 Path.Combine 会被解析成这个属性。
        public string FilePath { get { return _path; } }

        public string UrlBase { get { return "http://127.0.0.1:" + Port; } }

        // ---------------- 默认路径探测 ----------------

        private static string ExeDir()
        {
            try
            {
                string d = Path.GetDirectoryName(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(d)) { return d; }
            }
            catch { }
            return ".";
        }

        private static string ResolveDataDir()
        {
            try
            {
                string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(b))
                {
                    string d = Path.Combine(b, "DshPanel");
                    try { if (!Directory.Exists(d)) { Directory.CreateDirectory(d); } }
                    catch { }
                    if (Directory.Exists(d)) { return d; }
                }
            }
            catch { }
            string exe = ExeDir();
            try { if (!string.IsNullOrEmpty(exe)) { return exe; } } catch { }
            try { return Path.GetTempPath(); } catch { }
            return ".";
        }

        // 判断一个目录「像不像 DSH 的数据目录」：有 profiles 子目录或配置文件
        public static bool LooksLikeDshHome(string dir)
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

        // 【只找默认位置】发布版不能一启动就扫硬盘。
        // 顺序：环境变量 DSH_HOME → %USERPROFILE%\.dsh
        public static string ProbeDefaultDshHome()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("DSH_HOME");
                if (!string.IsNullOrEmpty(env) && LooksLikeDshHome(env))
                {
                    return env.TrimEnd('\\');
                }
            }
            catch { }
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    string cand = Path.Combine(home, ".dsh");
                    if (LooksLikeDshHome(cand)) { return cand; }
                }
            }
            catch { }
            return "";
        }

        private static string DefaultWorkDir()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) { return home; }
            }
            catch { }
            return ExeDir();
        }

        // 配置文件放哪：优先「绿色版」——exe 旁边那份，拷走 exe 就带着全部设置；
        // 但 exe 被放到 Program Files 之类的只读位置时写不进去，那就退到面板数据目录。
        // 判断顺序：已有便携配置 → 已有数据目录配置 → 当前 exe 目录能不能写 → 数据目录。
        public static string PickConfigPath()
        {
            string portable = "";
            string inside   = "";
            try { portable = Path.Combine(ExeDir(), "panel.config.ini"); } catch { }
            try { inside   = Path.Combine(DataDir, "panel.config.ini"); } catch { }

            try { if (portable.Length > 0 && File.Exists(portable)) { return portable; } } catch { }
            try { if (inside.Length   > 0 && File.Exists(inside))   { return inside; } } catch { }

            if (CanWriteDir(ExeDir()) && portable.Length > 0) { return portable; }
            if (inside.Length > 0) { return inside; }
            return "panel.config.ini";
        }

        // 真的去写一个临时文件：靠 ACL/只读属性判断「能不能写」经常误判。
        private static bool CanWriteDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) { return false; }
            string probe = "";
            try
            {
                probe = Path.Combine(dir, ".dshpanel-write-test.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { }
            try { if (probe.Length > 0 && File.Exists(probe)) { File.Delete(probe); } } catch { }
            return false;
        }


        public static PanelConfig Load(string path)
        {
            PanelConfig c = new PanelConfig();
            c._path = path;

            try
            {
                if (!File.Exists(path))
                {
                    c.SaveSample();   // 第一次运行：就地生成一份带注释的默认配置
                    return c;
                }

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i];
                    if (s == null) { continue; }
                    s = s.Trim();
                    if (s.Length == 0 || s[0] == '#' || s[0] == ';') { continue; }

                    int eq = s.IndexOf('=');
                    if (eq <= 0) { continue; }

                    string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = s.Substring(eq + 1).Trim();
                    // 允许值两端加引号，方便写带空格的路径
                    if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                    {
                        v = v.Substring(1, v.Length - 2);
                    }

                    if (k == "port")
                    {
                        int p;
                        if (int.TryParse(v, out p) && p > 0 && p < 65536) { c.Port = p; }
                    }
                    else if (k == "dshhome")        { c.DshHome        = v; }
                    else if (k == "svccmd")         { c.SvcCmd         = v; }
                    else if (k == "logfile")        { c.LogFile        = v; }
                    else if (k == "statelog")       { c.StateLog       = v; }
                    else if (k == "icofile")        { c.IcoFile        = v; }
                    else if (k == "workdir")        { c.WorkDir        = v; }
                    else if (k == "title")          { c.Title          = v; }
                    else if (k == "browsermode")    { c.BrowserMode    = v.ToLowerInvariant(); }
                    else if (k == "browserpath")    { c.BrowserPath    = v; }
                    else if (k == "browserprofile") { c.BrowserProfile = v; }
                    else if (k == "pluginjson")     { c.PluginJsonRaw  = v; }
                    else if (k == "autostart")      { c.AutoStart      = Truthy(v); }
                    else if (k == "autoopen")       { c.AutoOpen       = Truthy(v); }
                    else if (k == "autotray")       { c.AutoTray       = Truthy(v); }
                    else if (k == "darktheme")      { c.DarkTheme      = Truthy(v); }
                    else if (k == "stopwithpanel")  { c.StopWithPanel  = Truthy(v); }
                    else if (k == "balloononstop")  { c.BalloonOnStop  = Truthy(v); }
                    else if (k == "devmode")        { c.DevMode        = Truthy(v); }
                }
            }
            catch
            {
                // 配置读坏了就整份走默认值，绝不让它阻断启动
            }
            return c;
        }

        // 把内存里的值写回配置文件：已有的键就地替换，没有的追加到末尾。
        // 不整体重写，是为了保住用户自己写的注释和键顺序。
        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(_path)) { lines.AddRange(File.ReadAllLines(_path, Encoding.UTF8)); }

                SetKey(lines, "dshhome",       DshHome);
                SetKey(lines, "port",           Port.ToString());
                SetKey(lines, "svccmd",         SvcCmd);
                SetKey(lines, "logfile",        LogFile);
                SetKey(lines, "statelog",       StateLog);
                SetKey(lines, "icofile",        IcoFile);
                SetKey(lines, "workdir",        WorkDir);
                SetKey(lines, "title",          Title);
                SetKey(lines, "browsermode",    BrowserMode);
                SetKey(lines, "browserpath",    BrowserPath);
                SetKey(lines, "browserprofile", BrowserProfile);
                SetKey(lines, "pluginjson",     PluginJsonRaw);
                SetKey(lines, "autostart",      Flag(AutoStart));
                SetKey(lines, "autoopen",       Flag(AutoOpen));
                SetKey(lines, "autotray",       Flag(AutoTray));
                SetKey(lines, "darktheme",      Flag(DarkTheme));
                SetKey(lines, "stopwithpanel",  Flag(StopWithPanel));
                SetKey(lines, "balloononstop",  Flag(BalloonOnStop));
                SetKey(lines, "devmode",        Flag(DevMode));

                File.WriteAllLines(_path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch
            {
                // 写不进去（只读目录 / 被占用）也不该让面板崩掉，内存里的值照旧生效
            }
        }

        public static string Flag(bool b) { return b ? "1" : "0"; }

        public static bool Truthy(string v)
        {
            if (v == null) { return false; }
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on" || v == "是";
        }

        private static void SetKey(List<string> lines, string key, string val)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i];
                if (s == null) { continue; }
                string t = s.Trim();
                if (t.Length == 0 || t[0] == '#' || t[0] == ';') { continue; }

                int eq = t.IndexOf('=');
                if (eq <= 0) { continue; }

                if (string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + " = " + val;
                    return;
                }
            }
            lines.Add(key + " = " + val);
        }

        private void SaveSample()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# DSH 控制面板配置（key = value 纯文本，# 或 ; 开头为注释）");
                sb.AppendLine("# 改完保存、重新打开面板即可生效。删掉整个文件会重新生成这份默认配置。");
                sb.AppendLine("# 面板设置页里的大部分选项也会自动写回这里，一般不用手改。");
                sb.AppendLine();
                sb.AppendLine("# ---------------------------------------------------------------------");
                sb.AppendLine("# DSH 目录：DeepSeek Harness 的数据目录（就是 DSH_HOME，里面应当有 profiles 子目录）。");
                sb.AppendLine("#   留空 = 启动时自动探测（先看环境变量 DSH_HOME，再看 %USERPROFILE%\\.dsh）");
                sb.AppendLine("#   探测不到时面板会弹窗提示，你也可以在「设置 → DSH 目录」里直接选目录。");
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
                sb.AppendLine("# 例如：browserpath = C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe");
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
                sb.AppendLine("autostart = " + Flag(AutoStart));
                sb.AppendLine();
                sb.AppendLine("# 启动服务后自动打开网页");
                sb.AppendLine("autoopen = " + Flag(AutoOpen));
                sb.AppendLine();
                sb.AppendLine("# 面板启动后自动最小化到托盘");
                sb.AppendLine("autotray = " + Flag(AutoTray));
                sb.AppendLine();
                sb.AppendLine("# 深色主题");
                sb.AppendLine("darktheme = " + Flag(DarkTheme));
                sb.AppendLine();
                sb.AppendLine("# 关闭面板时一并停止 DSH 服务");
                sb.AppendLine("stopwithpanel = " + Flag(StopWithPanel));
                sb.AppendLine();
                sb.AppendLine("# 服务意外停止时托盘气泡提示");
                sb.AppendLine("balloononstop = " + Flag(BalloonOnStop));
                sb.AppendLine();
                sb.AppendLine("# 开发者模式：每 20 秒往状态日志写一行心跳，便于判断面板是活着还是卡死");
                sb.AppendLine("devmode = " + Flag(DevMode));

                // 不带 BOM：手写配置文件时 BOM 会让部分编辑器显示怪字符
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // 没有写权限也无所谓，默认值在内存里照样能用
            }
        }
    }
}
