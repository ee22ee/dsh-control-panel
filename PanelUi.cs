using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace DshPanel
{
    // ==========================================================================
    //  界面：自绘标题栏 + 左侧导航 + 双列卡片 + 可折叠日志
    //  布局参照 Steamcommunity_302：低对比、大圆角、状态信息点击直达设置。
    //
    //  【高 DPI】这里所有数字都是 96 DPI 的逻辑值，由 OnLoad 里的 ScaleLayout
    //  统一放大一次（只放一次！）。字体走 Theme 的缓存、按 pt 给，绝不乘缩放系数。
    // ==========================================================================
    public partial class PanelForm
    {
        internal const string VerText = "0.1";

        // 外壳（不含 1px 外框）
        private const int W = 1000;
        private const int H = 780;
        private const int TITLE_H = 46;
        private const int NAV_W = 190;

        // 页面内布局
        private const int M = 16;          // 左右留白
        private const int MT = 14;         // 上留白
        private const int COL1 = 430;      // 左列（服务列表）
        private const int GAP = 14;
        private const int COL2 = 334;      // 右列（服务控制 / 网络信息）
        private const int CARD_H = H - TITLE_H - MT * 2;   // 服务页卡片高度（日志改成独立页面后整页铺满）
        private const int NET_Y = 218;                     // 右侧第二张卡的 y
        private const int NET_ROW = 34;                    // 网络信息每行的高度（含间距）

        // ---- 外壳控件 ----
        private Panel      _shell;
        private Panel      _titleBar;
        private PictureBox _picTitle;
        private Panel      _navBar;
        private NavButton  _navService;
        private NavButton  _navSettings;
        private NavButton  _navLog;
        private NavButton  _navAbout;
        private NavButton  _navExit;
        private bool       _quitArm;     // 「退出面板」是否处于「确认退出吗？」待确认态
        private System.Windows.Forms.Timer _exitTimer;
        private TitleButton _tbTheme;
        private TitleButton _tbMin;
        private TitleButton _tbClose;
        private Panel      _content;
        private Image      _logoImg;

        // ---- 页面 ----
        private Panel _pageService;
        private Panel _pageSettings;
        private Panel _pageLog;
        private Panel _pageAbout;
        private int   _page;
        private bool  _pageReady;      // 首次 BuildUi 之后的切页才做动画
        private FadeStage _fade;       // 切页交叉溶解的舞台（见 PanelTheme.cs）

        // ---- 服务控制卡 ----
        private StatusDot  _dot;
        private Label      _lblState;
        private Label      _lblInfo;
        private Label      _lblPortChip;
        private Label      _lblCardHint;
        private FlatButton _btnOpen;         // 主按钮：启动 / 停止
        private IconButton _btnRestart;      // 重启（循环箭头）
        private IconButton _btnWeb;          // 打开网页（地球）
        private float      _splitT;          // 0 = 单个大按钮，1 = 缩短并浮现两个图标
        private bool       _svcRunning;
        private bool       _svcStarting;
        

        // ---- 插件列表 ----
        private Card       _cardPlugins;
        private ScrollArea _pluginHost;
        private WheelRouter _wheel;
        private Label      _lblPluginSummary;
        private FlatButton _btnPluginReset;
        private FlatButton _btnPluginAll;
        private List<PluginItem> _plugins    = new List<PluginItem>();
        private List<PluginRow>  _pluginRows = new List<PluginRow>();

        // ---- 设置页（整页可滚：以后加设置项不用重排） ----
        private ScrollArea  _settingsHost;
        private Card        _cardDsh;
        private Label       _lblDshPath;
        private Label       _lblDshState;
        private FlatButton  _btnDshPick;
        private FlatButton  _btnDshProbe;

        // ---- 网络监听 / 设置信息（可点击行） ----
        private Card    _cardNet;
        private NetRow  _rowAddr;
        private NetRow  _rowPort;
        private NetRow  _rowPid;
        private NetRow  _rowMode;
        private NetRow  _rowBrowser;
        private NetRow  _rowPlugins;
        private NetRow  _rowSvcCmd;
        private NetRow  _rowWorkDir;
        private NetRow  _rowSvcLog;
        private NetRow  _rowStateLog;
        private NetRow  _rowCfg;

        // ---- 日志 ----
        private Card       _cardLog;
        private FlatButton _btnLogClear;
        private Panel      _logHost;
        private TextBox    _log;

        // ---- 设置页 ----
        private Card         _cardGeneral;
        private Card         _cardOpen;
        private Card         _cardBehavior;
        private Card         _cardAdv;
        private FlatButton   _btnClean;
        private ToggleSwitch _swAutoStart;
        private ToggleSwitch _swAutoOpen;
        private ToggleSwitch _swAutoTray;
        private ToggleSwitch _swStopWithPanel;
        private ToggleSwitch _swBalloon;
        private ToggleSwitch _swDevMode;
        private Segmented    _segOpen;
        private FlatButton   _btnBrowser;
        private TextBox      _txtPort;
        private Label        _lblPortHint;
        private FlatButton   _btnPortSave;

        // ---- 关于页 ----
        private Label _lblAboutSign;

        // 卡片高亮（点击信息行跳转时闪一下目标卡片）
        

        // ---------------------------------------------------------------- 构建

        private void BuildUi()
        {
            _pageReady      = false;
            ClearSnaps();                               // 页面控件要重建，旧快照作废
            this.ClientSize = new Size(W + 2, H + 2);   // 外面 1px 是描边框
            this.BackColor  = Theme.Border;

            _shell = new Panel();
            _shell.Location  = new Point(1, 1);
            _shell.Size      = new Size(W, H);
            _shell.BackColor = Theme.Bg;
            this.Controls.Add(_shell);

            BuildTitleBar();
            BuildNav();
            BuildContent();

            BuildServicePage();
            BuildSettingsPage();
            BuildLogPage();
            BuildAboutPage();

            SwitchPage(_page);
            RefreshPluginList();
            LayoutServiceControls();
            LayoutLogPage();

            // 滚轮不会自己落到 Panel 上（见 WheelRouter 的注释），在这里把路由器挂上，
            // 每次重建界面只换目标，不重复 AddMessageFilter。
            if (_wheel == null)
            {
                _wheel = new WheelRouter();
                Application.AddMessageFilter(_wheel);
            }
            _wheel.Target = _pluginHost;
            SyncWheelTarget();
        }

        // 滚轮目标跟着当前页走：服务页滚插件列表、设置页滚整页内容，其余页没有可滚的。
        private void SyncWheelTarget()
        {
            if (_wheel == null) { return; }
            if (_page == 0)      { _wheel.Target = _pluginHost; }
            else if (_page == 1) { _wheel.Target = _settingsHost; }
            else                 { _wheel.Target = null; }
        }

        // ---- 自绘标题栏 ----

        private void BuildTitleBar()
        {
            _titleBar = new Panel();
            _titleBar.Location  = new Point(0, 0);
            _titleBar.Size      = new Size(W, TITLE_H);
            _titleBar.BackColor = Theme.TitleBg;
            _titleBar.Paint    += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border, 1f))
                {
                    e.Graphics.DrawLine(p, 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
                }
            };
            _shell.Controls.Add(_titleBar);

            _picTitle = new PictureBox();
            _picTitle.Location  = new Point(16, 11);
            _picTitle.Size      = new Size(24, 24);
            _picTitle.SizeMode  = PictureBoxSizeMode.Zoom;
            _picTitle.BackColor = Color.Transparent;
            _picTitle.Image     = LoadLogo();
            _titleBar.Controls.Add(_picTitle);

            Label t1 = MakeLabel("DeepSeek Harness 控制台", 11.5f, FontStyle.Bold, Theme.TitleText);
            t1.Location = new Point(48, 9);
            _titleBar.Controls.Add(t1);

            Label ver = MakeLabel("Ver " + VerText, 7.8f, FontStyle.Regular, Theme.TitleSub);
            ver.Location = new Point(48, 27);
            _titleBar.Controls.Add(ver);

            _tbClose = MakeTitleButton(1, new Point(W - 42, 8));
            _tbMin   = MakeTitleButton(0, new Point(W - 42 - 38, 8));
            _tbTheme = MakeTitleButton(2, new Point(W - 42 - 76, 8));
            _tbClose.Click += delegate { Close(); };
            _tbMin.Click   += delegate { this.WindowState = FormWindowState.Minimized; };
            _tbTheme.Click += delegate { ToggleTheme(); };

            MakeDraggable(_titleBar);
            MakeDraggable(_picTitle);
            MakeDraggable(t1);
            MakeDraggable(ver);
        }

        private TitleButton MakeTitleButton(int kind, Point at)
        {
            TitleButton b = new TitleButton(kind);
            b.Location = at;
            b.IsDark   = Theme.Dark;
            _titleBar.Controls.Add(b);
            return b;
        }

        // 无边框窗口的拖动：把「在标题栏按下左键」翻译成系统级的拖动标题栏
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION        = 0x2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private void MakeDraggable(Control c)
        {
            c.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) { return; }
                try
                {
                    ReleaseCapture();
                    SendMessage(this.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
                catch { }
            };
        }

        private Image LoadLogo()
        {
            try { return Image.FromFile(IcoFile); }
            catch
            {
                try { return new Icon(IcoFile, 96, 96).ToBitmap(); }
                catch { return null; }
            }
        }

        private Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Text      = text;
            l.Font      = Theme.Ui(size, style);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.AutoSize  = true;
            return l;
        }

        // ---- 左侧导航（logo + 项目）----

        private void BuildNav()
        {
            _navBar = new Panel();
            _navBar.Location  = new Point(0, TITLE_H);
            _navBar.Size      = new Size(NAV_W, H - TITLE_H);
            _navBar.BackColor = Theme.Shell;
            _navBar.Paint    += NavBarPaint;
            _shell.Controls.Add(_navBar);

            int navH = H - TITLE_H;
            _navService  = MakeNav("服务", Glyphs.Grid, 0, 172);
            _navSettings = MakeNav("设置", Glyphs.Gear, 1, 216);
            _navLog      = MakeNav("日志", Glyphs.List, 2, 260);
            _navAbout    = MakeNav("关于", Glyphs.Info, 3, 304);

            _navExit = MakeNav("退出面板", Glyphs.Exit, -1, navH - 44 - 16);
            _quitArm = false;
            _navExit.Click += delegate { OnExitNav(); };
            // 待确认状态最多留 4 秒，超时自动还原，防止「点了一次就去干别的」之后误触退出
            if (_exitTimer == null)
            {
                _exitTimer = new System.Windows.Forms.Timer();
                _exitTimer.Interval = 4000;
                _exitTimer.Tick += delegate { _exitTimer.Stop(); DisarmExit(); };
            }
        }

        // 「退出面板」点一次变成「确认退出吗？」，再点一次才真退；不弹对话框。
        private void OnExitNav()
        {
            if (_quitArm)
            {
                _quitArm = false;
                if (_exitTimer != null) { _exitTimer.Stop(); }
                LogEvent("按钮：确认退出面板");
                Quit();
                return;
            }
            _quitArm = true;
            _navExit.Text = "确认退出吗？";
            _navExit.Invalidate();
            if (_exitTimer != null) { _exitTimer.Stop(); _exitTimer.Start(); }
            LogEvent("按钮：退出面板（待确认）");
        }

        private void DisarmExit()
        {
            if (!_quitArm || _navExit == null) { return; }
            _quitArm = false;
            _navExit.Text = "退出面板";
            _navExit.Invalidate();
        }

        private NavButton MakeNav(string text, int glyph, int page, int y)
        {
            NavButton b = new NavButton(text, glyph);
            b.Location = new Point(12, y);
            b.Size     = new Size(NAV_W - 24, 40);
            if (page >= 0) { b.Click += delegate { SwitchPage(page); }; }
            _navBar.Controls.Add(b);
            return b;
        }

        private void NavBarPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 【只加载一次】Image.FromFile 每次都会新建一个 GDI+ 位图句柄，
            // 每帧重载会把句柄打满（整窗变黑）。这里缓存到字段里。
            if (_logoImg == null) { _logoImg = LoadLogo(); }

            int navW = (this._navBar == null) ? Theme.Px(NAV_W) : this._navBar.Width;
            int size = Theme.Px(64);

            if (_logoImg != null)
            {
                Rectangle lr = new Rectangle((navW - size) / 2, Theme.Px(24), size, size);
                using (GraphicsPath clip = Draw.Round(lr, Theme.Px(16)))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(clip);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_logoImg, lr);
                    g.Restore(st);
                }
                Draw.Stroke(g, lr, Theme.BorderSoft, Theme.Px(16), 1f);
            }

            int ty = Theme.Px(24) + size + Theme.Px(10);
            Draw.Text(g, "DeepSeek Harness", Theme.Ui(10f, FontStyle.Bold), Theme.Text,
                new Rectangle(Theme.Px(8), ty, navW - Theme.Px(16), Theme.Px(20)), ContentAlignment.MiddleCenter);
            Draw.Text(g, "控制台  Ver " + VerText, Theme.Ui(8f), Theme.SubText,
                new Rectangle(Theme.Px(8), ty + Theme.Px(20), navW - Theme.Px(16), Theme.Px(18)),
                ContentAlignment.MiddleCenter);

            int ly = ty + Theme.Px(38) + Theme.Px(12);
            using (Pen p = new Pen(Theme.BorderSoft, 1f))
            {
                g.DrawLine(p, Theme.Px(20), ly, navW - Theme.Px(20), ly);
            }
        }

        // ---- 内容区 ----

        private void BuildContent()
        {
            _content = new Panel();
            _content.Location  = new Point(NAV_W, TITLE_H);
            _content.Size      = new Size(W - NAV_W, H - TITLE_H);
            _content.BackColor = Theme.Bg;
            _shell.Controls.Add(_content);

            _pageService  = MakePage();
            _pageSettings = MakePage();
            _pageLog      = MakePage();
            _pageAbout    = MakePage();

            // 切页溶解用的舞台：铺满内容区、压在最上层，平时隐藏
            _fade = new FadeStage();
            _fade.Location = new Point(0, 0);
            _fade.Size     = new Size(_content.Width, _content.Height);
            _fade.Done    += delegate { ShowCurrentOnly(); RefreshSnapAsync(_page, CurrentPage()); };
            _content.Controls.Add(_fade);
        }

        private Panel MakePage()
        {
            Panel p = new Panel();
            p.Location  = new Point(0, 0);
            p.Size      = new Size(_content.Width, _content.Height);
            p.BackColor = Theme.Bg;
            _content.Controls.Add(p);
            return p;
        }

        private Card MakeCard(Control parent, string title, int glyph, int x, int y, int w, int h)
        {
            Card c = new Card(title);
            c.Glyph    = glyph;
            c.Location = new Point(x, y);
            c.Size     = new Size(w, h);
            parent.Controls.Add(c);
            return c;
        }

        // ---------------------------------------------------------- 服务页

        private void BuildServicePage()
        {
            // ---- 左列：服务列表（插件启停） ----
            _cardPlugins = MakeCard(_pageService, "服务列表 · 插件启停", Glyphs.List,
                                    M, MT, COL1, CARD_H);

            _btnPluginReset = new FlatButton("恢复默认", 1);
            _btnPluginReset.Location = new Point(COL1 - M - 76, 6);
            _btnPluginReset.Size     = new Size(76, 24);
            _btnPluginReset.Font     = Theme.Ui(8.2f);
            _btnPluginReset.Click   += delegate { LogEvent("按钮：恢复默认插件列表"); ResetPlugins(); };
            _cardPlugins.Controls.Add(_btnPluginReset);

            _btnPluginAll = new FlatButton("全不选", 1);
            _btnPluginAll.Location = new Point(COL1 - M - 76 - 8 - 60, 6);
            _btnPluginAll.Size     = new Size(60, 24);
            _btnPluginAll.Font     = Theme.Ui(8.2f);
            _btnPluginAll.Click   += delegate { ToggleAllPlugins(); };
            _cardPlugins.Controls.Add(_btnPluginAll);

            _lblPluginSummary = MakeLabel("", 7.8f, FontStyle.Regular, Theme.SubText);
            _lblPluginSummary.Location = new Point(M - 2, 40);
            _cardPlugins.Controls.Add(_lblPluginSummary);

            _pluginHost = new ScrollArea();
            _pluginHost.Location  = new Point(12, 60);
            _pluginHost.Size      = new Size(COL1 - 24, CARD_H - 60 - 12);
            _pluginHost.BackColor = Theme.Card;
            _cardPlugins.Controls.Add(_pluginHost);

            // ---- 右列第一张：服务控制 ----
            Card ctrl = MakeCard(_pageService, "服务控制", Glyphs.Play, M + COL1 + GAP, MT, COL2, 190);

            _dot = new StatusDot();
            _dot.Location = new Point(14, 38);
            ctrl.Controls.Add(_dot);

            _lblState = MakeLabel("已停止", 10f, FontStyle.Bold, Theme.SubText);
            _lblState.Location = new Point(36, 36);
            ctrl.Controls.Add(_lblState);

            _lblPortChip = MakeLabel("端口 " + Port, 7.8f, FontStyle.Regular, Theme.SubText);
            _lblPortChip.AutoSize  = false;
            _lblPortChip.TextAlign = ContentAlignment.MiddleRight;
            _lblPortChip.Location  = new Point(COL2 - 14 - 110, 38);
            _lblPortChip.Size      = new Size(110, 18);
            ctrl.Controls.Add(_lblPortChip);

            _lblInfo = MakeLabel("", 8.2f, FontStyle.Regular, Theme.SubText);
            _lblInfo.AutoSize = false;
            _lblInfo.Location = new Point(14, 62);
            _lblInfo.Size     = new Size(COL2 - 28, 18);
            ctrl.Controls.Add(_lblInfo);

            _btnOpen = new FlatButton("启动并打开 DSH", 0);
            _btnOpen.Glyph    = Glyphs.Play;
            _btnOpen.Location = new Point(14, 88);
            _btnOpen.Size     = new Size(COL2 - 28, 42);
            _btnOpen.Font     = Theme.Ui(9.8f, FontStyle.Bold);
            _btnOpen.Click   += delegate { OnMainButton(); };
            ctrl.Controls.Add(_btnOpen);

            _btnRestart = new IconButton(Glyphs.Restart);
            _btnRestart.Radius      = 12;
            _btnRestart.Size        = new Size(42, 42);
            _btnRestart.Location    = new Point(14, 88);
            _btnRestart.Alpha       = 0f;
            _btnRestart.Visible     = false;
            _btnRestart.Click      += delegate { LogEvent("按钮：重启 DSH"); ConfirmRestart(); };
            ctrl.Controls.Add(_btnRestart);

            _btnWeb = new IconButton(Glyphs.Globe);
            _btnWeb.Radius = 12;
            _btnWeb.Size        = new Size(42, 42);
            _btnWeb.Location    = new Point(14, 88);
            _btnWeb.Alpha       = 0f;
            _btnWeb.Visible     = false;
            _btnWeb.Click      += delegate { LogEvent("按钮：打开网页 DSH"); OpenWebOnly(); };
            ctrl.Controls.Add(_btnWeb);

            _lblCardHint = MakeLabel("启动后插件开关会自动锁定", 7.6f, FontStyle.Regular, Theme.SubText);
            _lblCardHint.Location = new Point(14, 142);
            ctrl.Controls.Add(_lblCardHint);

            // ---- 右列第二张：网络监听 / 设置信息 ----
            _cardNet = MakeCard(_pageService, "网络监听 / 设置信息", Glyphs.List,
                                M + COL1 + GAP, NET_Y, COL2, CARD_H - NET_Y + MT);

            int y = 38;
            _rowAddr = AddNetRow(_cardNet, "监听地址", Glyphs.Globe, true, y, "点击打开端口设置"); y += NET_ROW;
            _rowPort = AddNetRow(_cardNet, "监听端口", Glyphs.Plug,  true, y, "点击打开端口设置"); y += NET_ROW;
            _rowMode = AddNetRow(_cardNet, "打开方式", Glyphs.Link,  true, y, "点击打开网页设置"); y += NET_ROW;
            _rowBrowser = AddNetRow(_cardNet, "默认浏览器", Glyphs.Folder, true, y, "点击打开浏览器设置"); y += NET_ROW;
            _rowPlugins = AddNetRow(_cardNet, "已启用插件", Glyphs.List, true, y, "跳到插件列表"); y += NET_ROW;
            _rowPid    = AddNetRow(_cardNet, "服务 PID", Glyphs.Plug, false, y, ""); y += NET_ROW;
            _rowSvcCmd = AddNetRow(_cardNet, "启动脚本", Glyphs.Folder, true, y, "点击打开所在文件夹"); y += NET_ROW;
            _rowWorkDir = AddNetRow(_cardNet, "工作目录", Glyphs.Folder, true, y, "点击打开文件夹"); y += NET_ROW;
            _rowSvcLog = AddNetRow(_cardNet, "服务日志", Glyphs.Folder, true, y, "点击打开日志文件"); y += NET_ROW;
            _rowStateLog = AddNetRow(_cardNet, "面板日志", Glyphs.Folder, true, y, "点击打开日志文件"); y += NET_ROW;
            _rowCfg    = AddNetRow(_cardNet, "配置文件", Glyphs.Folder, true, y, "点击打开配置文件");

            _rowAddr.Click     += delegate { JumpToPort(); };
            _rowPort.Click     += delegate { JumpToPort(); };
            _rowMode.Click     += delegate { JumpToOpenMode(); };
            _rowBrowser.Click  += delegate { JumpToOpenMode(); };
            _rowPlugins.Click  += delegate { FlashCard(_cardPlugins); ScrollPluginsTop(); };
            _rowSvcCmd.Click   += delegate { RevealFile(SvcCmd); };
            _rowWorkDir.Click  += delegate { RevealFile(WorkDir); };
            _rowSvcLog.Click   += delegate { RevealFile(LogFile); };
            _rowStateLog.Click += delegate { RevealFile(StateLog); };
            _rowCfg.Click      += delegate { RevealFile(CfgPath); };
        }

        private NetRow AddNetRow(Card host, string label, int glyph, bool clickable, int y, string tip)
        {
            NetRow r = new NetRow(label, glyph, clickable);
            r.Location = new Point(8, y);
            r.Size     = new Size(COL2 - 16, NET_ROW - 4);
            r.Tip      = tip;
            host.Controls.Add(r);
            return r;
        }

        // ---------------------------------------------------------- 日志页
        // 【布局参考 302】日志是左侧导航的独立一页，不再挤在服务页底部 ——
        // 服务页因此能整列展示插件列表，日志也能一次看到几十行。

        private void BuildLogPage()
        {
            int cardW = COL1 + GAP + COL2;
            int cardH = H - TITLE_H - MT * 2;

            _cardLog = MakeCard(_pageLog, "运行日志", Glyphs.List, M, MT, cardW, cardH);

            _btnLogClear = new FlatButton("清空", 1);
            _btnLogClear.Location = new Point(cardW - M - 58, 6);
            _btnLogClear.Size     = new Size(58, 24);
            _btnLogClear.Font     = Theme.Ui(8.2f);
            _btnLogClear.Click   += delegate { if (_log != null) { _log.Clear(); } };
            _cardLog.Controls.Add(_btnLogClear);

            _logHost = new Panel();
            _logHost.Location  = new Point(12, 44);
            _logHost.Size      = new Size(cardW - 24, cardH - 56);
            _logHost.BackColor = Theme.LogBg;
            _logHost.Resize   += delegate
            {
                try
                {
                    _logHost.Region = new Region(Draw.Round(
                        new Rectangle(0, 0, _logHost.Width, _logHost.Height), 10));
                }
                catch { }
            };
            _cardLog.Controls.Add(_logHost);

            _log = new TextBox();
            _log.Multiline   = true;
            _log.ReadOnly    = true;
            _log.ScrollBars  = ScrollBars.Vertical;
            _log.WordWrap    = false;
            _log.BackColor   = Theme.LogBg;
            _log.ForeColor   = Theme.LogFg;
            _log.Font        = Theme.Mono(8.25f);
            _log.BorderStyle = BorderStyle.None;
            _log.Location    = new Point(10, 9);
            _log.Size        = new Size(_logHost.Width - 20, _logHost.Height - 18);
            _log.TabStop     = false;
            _logHost.Controls.Add(_log);
        }

        // ---------------------------------------------------------- 设置页

        private void BuildSettingsPage()
        {
            // 设置项只会越来越多（以后还要加），所以整页放进一个滚动宿主里：
            // 内容超出一屏就能滚，加卡片不用再重排高度。
            _settingsHost = new ScrollArea();
            _settingsHost.Location  = new Point(0, 0);
            _settingsHost.Size      = new Size(_pageSettings.Width, _pageSettings.Height);
            _settingsHost.BackColor = Theme.Bg;      // 页面底色；卡片才是那层浅色面板
            _pageSettings.Controls.Add(_settingsHost);

            int cardW = _pageSettings.Width - M * 2 - 10;   // 右侧留出滚动条的位置
            int y = MT;

            // ------------------------------------------------ DSH 目录（先让新用户把路指对）
            _cardDsh = AddSettingCard("DSH 目录", Glyphs.Folder, y, 148);

            Label lpDir = MakeLabel("目录", 9f, FontStyle.Regular, Theme.Text);
            lpDir.Location = new Point(M, 47);
            _cardDsh.Controls.Add(lpDir);

            _lblDshPath = MakeLabel("", 8.8f, FontStyle.Regular, Theme.Text);
            _lblDshPath.AutoSize     = false;
            _lblDshPath.AutoEllipsis = true;
            _lblDshPath.TextAlign    = ContentAlignment.MiddleLeft;
            _lblDshPath.Location     = new Point(M + 52, 46);
            _lblDshPath.Size         = new Size(cardW - M * 2 - 52 - 92, 22);
            _cardDsh.Controls.Add(_lblDshPath);

            _btnDshPick = new FlatButton("选择…", 1);
            _btnDshPick.Location = new Point(cardW - M - 84, 40);
            _btnDshPick.Size     = new Size(84, 30);
            _btnDshPick.Font     = Theme.Ui(8.8f);
            _btnDshPick.Click   += delegate { PickDshHome(); };
            _cardDsh.Controls.Add(_btnDshPick);

            _lblDshState = MakeLabel("", 7.6f, FontStyle.Regular, Theme.SubText);
            _lblDshState.AutoSize     = false;
            _lblDshState.AutoEllipsis = true;
            _lblDshState.TextAlign    = ContentAlignment.MiddleLeft;
            _lblDshState.Location     = new Point(M, 78);
            _lblDshState.Size         = new Size(cardW - M * 2 - 96, 20);
            _cardDsh.Controls.Add(_lblDshState);

            _btnDshProbe = new FlatButton("重新自动探测", 1);
            _btnDshProbe.Location = new Point(M, 106);
            _btnDshProbe.Size     = new Size(104, 28);
            _btnDshProbe.Font     = Theme.Ui(8.6f);
            _btnDshProbe.Click   += delegate { ProbeDshHomeAgain(); };
            _cardDsh.Controls.Add(_btnDshProbe);

            Label lh = MakeLabel("插件启停页读写的就是「目录 \\ profiles \\ web \\ package.json」", 7.6f,
                                 FontStyle.Regular, Theme.SubText);
            lh.Location = new Point(M + 116, 111);
            _cardDsh.Controls.Add(lh);

            y += 148 + 12;

            _cardGeneral = AddSettingCard("常规", Glyphs.Gear, y, 176);
            _swAutoStart = AddSwitchRow(_cardGeneral, "开机自启", "登录 Windows 后自动打开这个控制面板",
                                        40, _cfg.AutoStart, delegate { OnAutoStartChanged(); });
            _swAutoOpen  = AddSwitchRow(_cardGeneral, "启动服务后自动打开网页", "点「启动并打开 DSH」后自动拉起浏览器",
                                        82, _cfg.AutoOpen, delegate { OnSimpleSettingChanged(); });
            _swAutoTray  = AddSwitchRow(_cardGeneral, "启动后自动最小化到托盘", "面板起来后直接收进通知区域，不打扰你",
                                        124, _cfg.AutoTray, delegate { OnSimpleSettingChanged(); });
            y += 176 + 12;

            _cardOpen = AddSettingCard("网页打开方式", Glyphs.Globe, y, 150);

            Label lo = MakeLabel("打开方式", 9f, FontStyle.Regular, Theme.Text);
            lo.Location = new Point(M, 44);
            _cardOpen.Controls.Add(lo);

            _segOpen = new Segmented(new string[] { "普通窗口", "无地址栏独立窗口" });
            _segOpen.Location = new Point(M + 100, 40);
            _segOpen.Size     = new Size(250, 34);
            _segOpen.SetIndexSilent(_cfg.BrowserMode == "app" ? 1 : 0);
            _segOpen.Changed += delegate { OnOpenModeChanged(); };
            _cardOpen.Controls.Add(_segOpen);

            Label lb = MakeLabel("默认浏览器", 9f, FontStyle.Regular, Theme.Text);
            lb.Location = new Point(M, 88);
            _cardOpen.Controls.Add(lb);

            _btnBrowser = new FlatButton("", 1);
            _btnBrowser.Location = new Point(M + 100, 84);
            _btnBrowser.Size     = new Size(250, 34);
            _btnBrowser.Font     = Theme.Ui(9f);
            _btnBrowser.Click   += delegate { ShowBrowserMenu(); };
            _cardOpen.Controls.Add(_btnBrowser);

            Label hint = MakeLabel("独立窗口（无地址栏）需要 Chromium 系浏览器；选 Firefox 会自动降级为普通窗口。",
                                   7.6f, FontStyle.Regular, Theme.SubText);
            hint.Location = new Point(M, 128);
            _cardOpen.Controls.Add(hint);
            y += 150 + 12;

            _cardBehavior = AddSettingCard("服务行为", Glyphs.Plug, y, 132);
            _swStopWithPanel = AddSwitchRow(_cardBehavior, "关闭面板时一并停止 DSH 服务",
                                            "不开则关掉面板不影响正在运行的 DSH", 40, _cfg.StopWithPanel,
                                            delegate { OnSimpleSettingChanged(); });
            _swBalloon = AddSwitchRow(_cardBehavior, "服务意外停止时托盘气泡提示",
                                      "DSH 自己崩了或被外部杀掉时提醒你", 82, _cfg.BalloonOnStop,
                                      delegate { OnSimpleSettingChanged(); });
            y += 132 + 12;

            _cardAdv = AddSettingCard("高级", Glyphs.Gear, y, 206);

            Label lp = MakeLabel("服务端口", 9f, FontStyle.Regular, Theme.Text);
            lp.Location = new Point(M, 48);
            _cardAdv.Controls.Add(lp);

            _txtPort = new TextBox();
            _txtPort.Location    = new Point(M + 100, 44);
            _txtPort.Size        = new Size(90, 24);
            _txtPort.Font        = Theme.Ui(9f);
            _txtPort.Text        = _cfg.Port.ToString();
            _txtPort.BackColor   = Theme.Panel2;
            _txtPort.ForeColor   = Theme.Text;
            _txtPort.BorderStyle = BorderStyle.FixedSingle;
            _cardAdv.Controls.Add(_txtPort);

            _btnPortSave = new FlatButton("保存", 1);
            _btnPortSave.Location = new Point(M + 200, 43);
            _btnPortSave.Size     = new Size(66, 26);
            _btnPortSave.Font     = Theme.Ui(8.6f);
            _btnPortSave.Click   += delegate { OnSavePort(); };
            _cardAdv.Controls.Add(_btnPortSave);

            _lblPortHint = MakeLabel("改动会在下次启动面板后生效", 7.6f, FontStyle.Regular, Theme.SubText);
            _lblPortHint.Location = new Point(M + 278, 48);
            _cardAdv.Controls.Add(_lblPortHint);

            // 纯净 DSH：官方默认插件集另起一个实例，专门用来排查「是不是某个插件把 DSH 弄坏了」
            Label lc = MakeLabel("启动纯净 DSH", 9.2f, FontStyle.Regular, Theme.Text);
            lc.Location = new Point(M, 100);
            _cardAdv.Controls.Add(lc);

            Label lcd = MakeLabel("插件出问题时，用官方默认插件集另起一个实例来排查", 7.6f, FontStyle.Regular, Theme.SubText);
            lcd.Location = new Point(M, 118);
            _cardAdv.Controls.Add(lcd);

            _btnClean = new FlatButton("启动", 1);
            _btnClean.Location = new Point(cardW - M - 78, 104);
            _btnClean.Size     = new Size(78, 28);
            _btnClean.Font     = Theme.Ui(8.8f);
            _btnClean.Click   += delegate { LogEvent("按钮：启动纯净 DSH"); StartCleanDsh(); };
            _cardAdv.Controls.Add(_btnClean);

            _swDevMode = AddSwitchRow(_cardAdv, "开发者模式", "记录面板与服务状态，并每 20 秒写一次心跳",
                                      150, _cfg.DevMode, delegate { OnSimpleSettingChanged(); });

            y += 206;

            // 内容总高交给滚动区（多留一点底部呼吸位）
            _settingsHost.SetContent(y + 8);
            RefreshDshCard();
        }

        // 设置页的卡片走这里：除了造卡片，还要在滚动区登记它的「内容坐标」，
        // 否则 ScrollArea.Apply() 不认识它，滚动时卡片纹丝不动。
        private Card AddSettingCard(string title, int glyph, int y, int h)
        {
            int w = _pageSettings.Width - M * 2 - 10;
            Card c = MakeCard(_settingsHost, title, glyph, M, y, w, h);
            _settingsHost.Register(c, y);
            return c;
        }

        // ---------------------------------------------------------- DSH 目录卡

        // 把当前配置反映到卡上：路径文本 + 一句人话状态（找不到就红字，让新用户一眼看到）
        private void RefreshDshCard()
        {
            if (_lblDshPath == null) { return; }

            string home = _cfg.DshHome;
            bool ok = _cfg.DshConfigured;

            _lblDshPath.Text = (home == null || home.Length == 0) ? "（未设置）" : home;
            _lblDshPath.ForeColor = ok ? Theme.Text : Theme.Danger;

            if (ok)
            {
                _lblDshState.Text = "已找到 DeepSeek Harness 的数据目录。";
                _lblDshState.ForeColor = Theme.SubText;
            }
            else if (home != null && home.Length > 0)
            {
                _lblDshState.Text = "这个目录不存在，或者不像 DSH 的数据目录（应当包含 profiles 子目录）。";
                _lblDshState.ForeColor = Theme.Danger;
            }
            else
            {
                _lblDshState.Text = "没有自动找到 DSH 目录。点「选择…」指定一个（里面应当有 profiles 文件夹）。";
                _lblDshState.ForeColor = Theme.Danger;
            }
        }

        // 弹目录选择框；选完立刻写回配置并刷新插件列表
        private void PickDshHome()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择 DeepSeek Harness 的数据目录（里面应当有 profiles 文件夹）";
                dlg.ShowNewFolderButton = false;
                if (_cfg.DshHome != null && _cfg.DshHome.Length > 0 && Directory.Exists(_cfg.DshHome))
                {
                    dlg.SelectedPath = _cfg.DshHome;
                }
                if (dlg.ShowDialog(this) != DialogResult.OK) { return; }

                string picked = dlg.SelectedPath;
                if (!PanelConfig.LooksLikeDshHome(picked))
                {
                    DialogResult r = MessageBox.Show(this,
                        "这个目录里没有 profiles 子目录，看起来不像 DeepSeek Harness 的数据目录。\r\n\r\n" +
                        "仍然要用它吗？（选错了插件列表会读不到东西）",
                        "确认目录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) { return; }
                }

                _cfg.DshHome = picked;
                _cfg.PluginJsonRaw = "";                  // 换目录后让插件清单重新跟着目录走
                _cfg.Save();
                RefreshDshCard();
                MarkPageDirty();
                RefreshPluginList();
                LogEvent("设置：DSH 目录 = " + picked);
                AppendLog("[面板] DSH 目录已设为：" + picked + "\r\n");
            }
        }

        // 重新跑一遍自动探测（用户可能刚装好 DSH）
        private void ProbeDshHomeAgain()
        {
            string found = PanelConfig.ProbeDefaultDshHome();
            if (found == null || found.Length == 0)
            {
                MessageBox.Show(this,
                    "还是没找到。面板只会看这两处：\r\n\r\n" +
                    "  1) 环境变量 DSH_HOME\r\n" +
                    "  2) " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\.dsh\r\n\r\n" +
                    "如果 DSH 装在别处，请点「选择…」手动指定。",
                    "没有找到 DSH 目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _cfg.DshHome = found;
            _cfg.PluginJsonRaw = "";
            _cfg.Save();
            RefreshDshCard();
            MarkPageDirty();
            RefreshPluginList();
            LogEvent("设置：自动探测到 DSH 目录 " + found);
        }

        private ToggleSwitch AddSwitchRow(Control parent, string name, string desc,
                                          int y, bool value, EventHandler onChange)
        {
            Label ln = MakeLabel(name, 9.2f, FontStyle.Regular, Theme.Text);
            ln.Location = new Point(M, y + 4);
            parent.Controls.Add(ln);

            Label ld = MakeLabel(desc, 7.6f, FontStyle.Regular, Theme.SubText);
            ld.Location = new Point(M, y + 22);
            parent.Controls.Add(ld);

            ToggleSwitch sw = new ToggleSwitch();
            sw.Location = new Point(parent.Width - M - 46, y + 9);
            // 【顺序很重要】必须先把初值写进去、再挂事件：ToggleSwitch.Checked 的 setter 会
            // 触发 CheckedChanged，而构建期的回调会去读还没赋值的字段 —— 那会让面板一启动
            // 就崩，日志里只留一行 NullReferenceException。所以这里用静默赋值 + 之后挂事件。
            sw.SetCheckedSilent(value);
            if (onChange != null) { sw.CheckedChanged += onChange; }
            parent.Controls.Add(sw);

            Panel line = new Panel();
            line.Location  = new Point(M, y + 40);
            line.Size      = new Size(parent.Width - M * 2, 1);
            line.BackColor = Theme.BorderSoft;
            parent.Controls.Add(line);
            return sw;
        }

        // ---------------------------------------------------------- 关于页

        private void BuildAboutPage()
        {
            int cardW = COL1 + GAP + COL2;
            int cardH = H - TITLE_H - MT * 2;
            Card a = MakeCard(_pageAbout, "", Glyphs.None, M, MT, cardW, cardH);
            a.Flat = true;                       // 无标题行，别在顶部空出 36px

            int cx = cardW / 2;

            // 整组内容（logo / 标题 / 版本 / 分隔线 / 署名）垂直居中
            const int logoSize = 104, hTitle = 36, hVer = 22, lineW = 180, hSign = 28;
            const int gap1 = 24, gap2 = 26, gap3 = 26;
            int groupH = logoSize + gap1 + hTitle + hVer + gap2 + 1 + gap3 + hSign;
            int y = Math.Max(48, (cardH - groupH) / 2);

            Panel logo = new Panel();
            logo.Location  = new Point(cx - logoSize / 2, y);
            logo.Size      = new Size(logoSize, logoSize);
            logo.BackColor = Theme.Panel2;
            Image logoImg = LoadLogo();          // 用局部变量：别覆盖导航栏正在用的 _logoImg
            if (logoImg != null)
            {
                PictureBox pb = new PictureBox();
                pb.Image     = logoImg;
                pb.SizeMode  = PictureBoxSizeMode.Zoom;
                pb.Dock      = DockStyle.Fill;
                pb.BackColor = Color.Transparent;
                logo.Controls.Add(pb);
            }
            logo.Resize += delegate
            {
                try { logo.Region = new Region(Draw.Round(new Rectangle(0, 0, logo.Width, logo.Height), 24)); }
                catch { }
            };
            a.Controls.Add(logo);
            y += logoSize + gap1;

            // 从这段开始全部 AutoSize=false + MiddleCenter：不管字体度量怎么变，永远正中
            Label big = MakeLabel("DeepSeek Harness 控制台", 18f, FontStyle.Bold, Theme.Text);
            big.AutoSize  = false;
            big.TextAlign = ContentAlignment.MiddleCenter;
            big.Location  = new Point(0, y);
            big.Size      = new Size(cardW, hTitle);
            a.Controls.Add(big);
            y += hTitle;

            Label ver = MakeLabel("Ver " + VerText, 10f, FontStyle.Regular, Theme.SubText);
            ver.AutoSize  = false;
            ver.TextAlign = ContentAlignment.MiddleCenter;
            ver.Location  = new Point(0, y);
            ver.Size      = new Size(cardW, hVer);
            a.Controls.Add(ver);
            y += hVer + gap2;

            Panel line = new Panel();
            line.Location  = new Point(cx - lineW / 2, y);
            line.Size      = new Size(lineW, 1);
            line.BackColor = Theme.BorderSoft;
            a.Controls.Add(line);
            y += 1 + gap3;

            _lblAboutSign = MakeLabel("by deepseek 和大米饭｜她的主人 lqh", 11.5f, FontStyle.Bold, Theme.AccentText);
            _lblAboutSign.AutoSize  = false;
            _lblAboutSign.TextAlign = ContentAlignment.MiddleCenter;
            _lblAboutSign.Location  = new Point(0, y);
            _lblAboutSign.Size      = new Size(cardW, hSign);
            a.Controls.Add(_lblAboutSign);
        }

        // ---------------------------------------------------------- 布局动画

        private void LayoutServiceControls()
        {
            if (_btnOpen == null) { return; }
            int   full = Theme.Px(COL2 - 28);
            float t    = Anim.Ease(_splitT);

            _btnOpen.Width = full - (int)Math.Round(Theme.Pxf(100f) * t);
            if (_btnOpen.Width < Theme.Px(150)) { _btnOpen.Width = Theme.Px(150); }

            int gap = (int)Math.Round(Theme.Pxf(8f) * t);
            int bx  = Theme.Px(14) + _btnOpen.Width + gap;
            int by  = Theme.Px(88);
            int bw  = Theme.Px(42);
            _btnRestart.Location = new Point(bx, by);
            _btnWeb.Location     = new Point(bx + bw + gap, by);
            _btnRestart.Alpha    = t;
            _btnWeb.Alpha        = t;
            _btnRestart.Visible  = t > 0.02f;
            _btnWeb.Visible      = t > 0.02f;
        }

        private void LayoutLogPage()
        {
            if (_cardLog == null || _logHost == null) { return; }

            int cardH = Theme.Px(H - TITLE_H - MT * 2);
            _cardLog.Height  = cardH;
            _logHost.Width   = _cardLog.Width - Theme.Px(24);
            _logHost.Height  = cardH - _logHost.Top - Theme.Px(12);
            _log.Width       = Math.Max(0, _logHost.Width - Theme.Px(20));
            _log.Height      = Math.Max(0, _logHost.Height - Theme.Px(18));
        }

        public bool AnimStep()
        {
            bool more = false;

            // 服务「在跑」或「正在拉起」都把图标按钮留在原位：重启的整个过程中
            // 重启图标要能露出来转圈，等连上（running）才收回去。
            float st = (_svcRunning || _svcStarting) ? 1f : 0f;
            if (_splitT != st) { _splitT = Anim.Approach(_splitT, st, 0.10f); LayoutServiceControls(); more = true; }

            // 切页动画已改成 FadeStage 的交叉溶解（自己带时间轴），这里不用管
            return more;
        }

        // ---------------------------------------------------------- 切页

        private Panel CurrentPage()
        {
            if (_page == 1) { return _pageSettings; }
            if (_page == 2) { return _pageLog; }
            if (_page == 3) { return _pageAbout; }
            return _pageService;
        }

        // 只显示当前页、其余藏起来，并把当前页摆回原位
        private void ShowCurrentOnly()
        {
            if (_pageService == null) { return; }
            _pageService.Visible  = (_page == 0);
            _pageSettings.Visible = (_page == 1);
            _pageLog.Visible      = (_page == 2);
            _pageAbout.Visible    = (_page == 3);

            Panel p = CurrentPage();
            if (p != null)
            {
                p.Left = 0;
                p.Top  = 0;
                p.BringToFront();   // 提到舞台上面
                p.Refresh();        // 趁舞台还盖着，先把它真画出来，撤舞台时才不会闪一帧空的
            }
        }

        private static Bitmap CapturePage(Control c)
        {
            return Shot.Window(c);
        }


        // ------------------------------------------------------ 页面快照缓存
        //
        // PrintWindow 会把整页软件重绘一遍（200% 缩放下 ~85ms），放在切页的点击
        // 路径上就是「点一下卡一下」。所以给 4 个页面各缓存一张位图：
        //   · 切页时直接用缓存，0ms
        //   · 溶解结束后在后台线程把「当前页」重抓一次（此时它是可见的，才抓得到内容）
        //   · 用户改了内容就把那一页标脏，下次切过去时同步重抓一次（保证看到的是真的）
        // 缓存位图在 FadeStage 里只做 GetHbitmap 拷贝，不转移所有权。

        private readonly Bitmap[] _snap      = new Bitmap[4];
        private readonly bool[]   _snapDirty = new bool[4];
        private readonly object   _snapLock  = new object();
        private bool _snapQueued;

        private Panel PageAt(int idx)
        {
            if (idx == 0) { return _pageService; }
            if (idx == 1) { return _pageSettings; }
            if (idx == 2) { return _pageLog; }
            return _pageAbout;
        }

        // 内容变了，缓存作废
        private void MarkPageDirty()
        {
            if (_page >= 0 && _page < 4) { _snapDirty[_page] = true; }
        }

        // 同步取快照：有干净的缓存就用缓存，否则现抓一张（调用方需持有 _snapLock）
        private Bitmap SnapOf(int idx, Panel page)
        {
            if (idx >= 0 && idx < 4 && !_snapDirty[idx] && _snap[idx] != null) { return _snap[idx]; }
            Bitmap fresh = CapturePage(page);
            if (fresh != null && idx >= 0 && idx < 4)
            {
                Bitmap old = _snap[idx];
                _snap[idx] = fresh;
                _snapDirty[idx] = false;
                if (old != null && !ReferenceEquals(old, fresh)) { try { old.Dispose(); } catch { } }
            }
            return fresh;
        }

        // 后台重抓当前页的快照（必须在页面可见时调用，否则抓到空白）
        private void RefreshSnapAsync(int idx, Panel page)
        {
            if (idx < 0 || idx >= 4 || page == null || !page.IsHandleCreated) { return; }
            if (_snapQueued) { return; }
            _snapQueued = true;
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    Bitmap b = null;
                    try { b = Shot.Window(page); } catch { }
                    _snapQueued = false;
                    if (b == null) { return; }
                    Bitmap old;
                    lock (_snapLock) { old = _snap[idx]; _snap[idx] = b; _snapDirty[idx] = false; }
                    if (old != null && !ReferenceEquals(old, b)) { try { old.Dispose(); } catch { } }
                });
            }
            catch { _snapQueued = false; }
        }

        private void ClearSnaps()
        {
            lock (_snapLock)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (_snap[i] != null) { try { _snap[i].Dispose(); } catch { } _snap[i] = null; }
                    _snapDirty[i] = false;
                }
            }
        }

        private void SwitchPage(int page)
        {
            if (_pageReady && page == _page) { return; }
            if (_fade != null) { _fade.FinishNow(); }        // 上一次没播完的先落地

            Panel oldPage = CurrentPage();
            int   oldIdx  = _page;
            bool  animate = _pageReady && oldPage != null && _fade != null;

            _page = page;
            _navService.Selected  = (page == 0);
            _navSettings.Selected = (page == 1);
            _navLog.Selected      = (page == 2);
            _navAbout.Selected    = (page == 3);
            _pageReady = true;
            SyncWheelTarget();

            Panel newPage = CurrentPage();
            if (!animate || newPage == null || oldPage == newPage)
            {
                ShowCurrentOnly();
                RefreshSnapAsync(_page, CurrentPage());
                return;
            }

            // 交叉溶解：旧页向下淡出、新页从下方升到原位淡入（两张图都走缓存）
            Bitmap from, to;
            lock (_snapLock)
            {
                from = SnapOf(oldIdx, oldPage);
                if (from == null)
                {
                    ShowCurrentOnly();
                    return;
                }
                _fade.Prepare(from);             // 舞台先铺上旧页画面，肉眼无跳变
                oldPage.Visible = false;
                newPage.Visible = true;          // 舞台压在最上层，这一帧看不到，只为能抓图
                newPage.Left    = 0;
                newPage.Top     = 0;
                newPage.Refresh();               // 先让它和子控件真画一遍，避免抓到半成品
                to = SnapOf(page, newPage);
                newPage.Visible = false;
                _fade.Begin(to, 260, 190, Theme.Pxf(14f), Theme.Pxf(10f));
            }
        }

        // ---------------------------------------------------------- 主题

        internal void ToggleTheme()
        {
            Theme.Dark = !Theme.Dark;
            _cfg.DarkTheme = Theme.Dark;
            _cfg.Save();
            RebuildForTheme();
        }

        private void RebuildForTheme()
        {
            string keepLog = (_log == null) ? "" : _log.Text;
            Anim.ClearAll();
            this.SuspendLayout();
            this.Controls.Clear();
            this.BackColor = Theme.Border;
            BuildUi();
            // 【关键】BuildUi 是按 100% 逻辑坐标搭的界面；切换主题时窗体已经放大到 S 倍，
            // 所以必须再走一遍 ScaleLayout + 把 ClientSize 也乘 S，否则整个界面会缩到左上角。
            if (Theme.S > 0f && Theme.S != 1f)
            {
                ScaleLayout(this, Theme.S);
                this.ClientSize = new Size(Theme.Px(W + 2), Theme.Px(H + 2));
            }
            if (_log != null) { _log.Text = keepLog; ScrollLogToEnd(); }
            RefreshPluginList();
            LayoutServiceControls();
            LayoutLogPage();
            UpdateUi(IsPortListening(Port));
            this.ResumeLayout();
        }

        // ---------------------------------------------------------- 插件

        private void RefreshPluginList()
        {
            if (_pluginHost == null) { return; }

            _pluginHost.ClearRows();
            _pluginRows.Clear();
            _plugins.Clear();

            // 新装用户最常见的一幕：还没指定 DSH 目录。这时别甩「读取失败」，直接给人话。
            if (!_cfg.DshConfigured)
            {
                _lblPluginSummary.Text      = "还没有指定 DSH 目录，读不到插件列表 —— 请到「设置 → DSH 目录」里选一下。";
                _lblPluginSummary.ForeColor = Theme.Danger;
                _btnPluginAll.Enabled       = false;
                _btnPluginReset.Enabled     = false;
                _pluginHost.SetContent(0);
                return;
            }

            _btnPluginAll.Enabled   = true;
            _btnPluginReset.Enabled = true;

            string err;
            if (!PluginStore.Load(PluginPath, _plugins, out err))
            {
                _lblPluginSummary.Text      = err;
                _lblPluginSummary.ForeColor = Theme.Danger;
                AppendLog("[面板] " + err + "\r\n");
                return;
            }

            _lblPluginSummary.ForeColor = Theme.SubText;
            UpdatePluginSummary("勾选后在下次启动 DSH 时生效");

            bool locked = IsPortListening(Port) || _starting;
            int y = 0;
            for (int i = 0; i < _plugins.Count; i++)
            {
                PluginRow row = new PluginRow(_plugins[i]);
                int rowW = _pluginHost.ClientSize.Width - Theme.Px(16);
                if (rowW < Theme.Px(120)) { rowW = Theme.Px(120); }
                row.Size     = new Size(rowW, Theme.Px(34));
                row.Locked   = locked || _plugins[i].Protected;
                row.Toggled += delegate { OnPluginToggled(); };
                _pluginHost.AddRow(row, y);
                _pluginRows.Add(row);
                y += Theme.Px(34);
            }
            _pluginHost.SetContent(y);
            _btnPluginAll.Text = (CountEnabled() == _plugins.Count) ? "全不选" : "全选";
        }

        private void UpdatePluginSummary(string tail)
        {
            _lblPluginSummary.Text = "共 " + _plugins.Count + " 个已注册插件，启用 " +
                                     CountEnabled() + " 个 · " + tail;
        }

        private void LockPluginRows(bool locked)
        {
            for (int i = 0; i < _pluginRows.Count; i++)
            {
                _pluginRows[i].Locked = locked || _pluginRows[i].Item.Protected;
            }
        }

        private void ToggleAllPlugins()
        {
            bool want = CountEnabled() != _plugins.Count;            for (int i = 0; i < _plugins.Count; i++)
            {
                if (!_plugins[i].Protected) { _plugins[i].Enabled = want; }
            }
            for (int i = 0; i < _pluginRows.Count; i++) { _pluginRows[i].Invalidate(); }

            string err;
            if (PluginStore.Save(PluginPath, _plugins, out err))
            {
                UpdatePluginSummary(want ? "已全部启用" : "已全部禁用");
                AppendLog("[面板] 插件开关已写入（" + (want ? "全部启用" : "全部禁用") + "）。\r\n");
                LogEvent("插件开关批量设置：" + (want ? "全部启用" : "全部禁用"));
            }
            else
            {
                AppendLog("[面板] 保存失败：" + err + "\r\n");
            }
            _btnPluginAll.Text = (CountEnabled() == _plugins.Count) ? "全不选" : "全选";
            MarkPageDirty();
        }

        private void OnPluginToggled()
        {
            string err;
            if (!PluginStore.Save(PluginPath, _plugins, out err))
            {
                AppendLog("[面板] 保存插件开关失败：" + err + "\r\n");
                LogEvent("插件开关保存失败：" + err);
                MessageBox.Show(err, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshPluginList();
                return;
            }

            UpdatePluginSummary("改动已写入，将在下次启动 DSH 时生效");
            AppendLog("[面板] 插件开关已写入 package.json（启用 " + CountEnabled() + " 个）。\r\n");
            LogEvent("插件开关已保存：启用 " + CountEnabled() + " 个");
            _btnPluginAll.Text = (CountEnabled() == _plugins.Count) ? "全不选" : "全选";
            MarkPageDirty();
        }

        private void ResetPlugins()
        {
            DialogResult r = MessageBox.Show(
                "把所有插件恢复成「全部启用」吗？\r\n\r\n" +
                "（只改 bundle 启用列表，不动 dependencies；写前会自动备份 package.json）",
                "恢复默认插件列表", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { return; }

            for (int i = 0; i < _plugins.Count; i++) { _plugins[i].Enabled = true; }

            string err;
            if (!PluginStore.Save(PluginPath, _plugins, out err))
            {
                AppendLog("[面板] 恢复默认失败：" + err + "\r\n");
                LogEvent("恢复默认插件列表失败：" + err);
                MessageBox.Show(err, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                LogEvent("插件列表已恢复为全部启用");
                AppendLog("[面板] 已恢复全部插件为启用。\r\n");
            }
            RefreshPluginList();
            MarkPageDirty();
        }

        private void ScrollPluginsTop()
        {
            if (_pluginHost != null) { _pluginHost.ScrollBy(-100000, false); }
            FlashCard(_cardPlugins);
        }

        // ---------------------------------------------------------- 服务状态

        internal void SetServiceState(bool running, bool starting)
        {
            SetServiceState(running, starting, false);
        }

        internal void SetServiceState(bool running, bool starting, bool restarting)
        {
            bool changed = (running != _svcRunning) || (starting != _svcStarting);
            _svcRunning  = running;
            _svcStarting = starting;
            if (changed) { MarkPageDirty(); }

            if (_lblState != null)
            {
                if (running)        { _lblState.Text = CleanMode ? "运行中（纯净）" : "运行中"; _lblState.ForeColor = Theme.Ok; }
                else if (starting)  { _lblState.Text = "启动中…";  _lblState.ForeColor = Theme.Warn;   }
                else                { _lblState.Text = "已停止";   _lblState.ForeColor = Theme.SubText; }

                if (_dot != null) { _dot.SetState(running, starting); }
            }

            if (_btnOpen != null)
            {
                if (running)
                {
                    _btnOpen.Text = "停止 DSH";
                    _btnOpen.Kind = 3;                       // 实心红
                    _btnOpen.Glyph = Glyphs.Stop;
                    _btnOpen.Enabled2 = true;
                }
                else if (starting)
                {
                    _btnOpen.Text = "正在启动…";
                    _btnOpen.Kind = 0;
                    _btnOpen.Glyph = Glyphs.Play;
                    _btnOpen.Enabled2 = false;
                }
                else
                {
                    _btnOpen.Text = "启动并打开 DSH";
                    _btnOpen.Kind = 0;
                    _btnOpen.Glyph = Glyphs.Play;
                    _btnOpen.Enabled2 = true;
                }
                _btnOpen.Invalidate();
            }

            if (_lblCardHint != null)
            {
                _lblCardHint.Text = running
                    ? "服务运行中 · 插件开关已锁定，改动需重启服务"
                    : (starting ? "正在拉起服务，请稍候…" : "启动后插件开关会自动锁定");
            }

            // 重启图标：**只有重启流程**才转圈；普通启动不转。连上（running）后缓停并回正。
            if (_btnRestart != null)
            {
                bool spin = restarting && starting && !running;
                if (_btnRestart.Spinning != spin)
                {
                    _btnRestart.Spinning = spin;
                    Anim.Add(_btnRestart);
                }
            }

            Anim.Add(this);
            LayoutServiceControls();
        }

        private void OnMainButton()
        {
            if (_svcRunning || IsPortListening(Port))
            {
                LogEvent("按钮：停止 DSH");
                ConfirmStop();
            }
            else
            {
                LogEvent("按钮：启动并打开 DSH");
                OpenDsh();
            }
        }

        // ---------------------------------------------------------- 网络信息

        private void RefreshNetInfo()
        {
            if (_rowAddr == null) { return; }

            bool running = IsPortListening(Port);
            int  pid     = running ? FindPidOnPort(Port) : 0;

            _rowAddr.Value    = running ? UrlBase : "（未监听）";
            _rowPort.Value    = Port.ToString();
            _rowPid.Value     = pid > 0 ? pid.ToString() : "—";
            _rowMode.Value    = (_cfg.BrowserMode == "app") ? "无地址栏独立窗口" : "普通窗口（标签页）";
            _rowBrowser.Value = BrowserDisplayName();
            _rowPlugins.Value = CountEnabled() + " / " + _plugins.Count;
            _rowSvcCmd.Value  = FileLabel(SvcCmd);
            _rowWorkDir.Value = FileLabel(WorkDir);
            _rowSvcLog.Value  = FileLabel(LogFile);
            _rowStateLog.Value = FileLabel(StateLog);
            _rowCfg.Value     = "panel.config.ini";

            _rowAddr.Invalidate();
            _rowPort.Invalidate();
            _rowPid.Invalidate();
            _rowMode.Invalidate();
            _rowBrowser.Invalidate();
            _rowPlugins.Invalidate();
            _rowSvcCmd.Invalidate();
            _rowWorkDir.Invalidate();
            _rowSvcLog.Invalidate();
            _rowStateLog.Invalidate();
            _rowCfg.Invalidate();

            if (_lblPortChip != null) { _lblPortChip.Text = "端口 " + Port; }
            if (_dot != null) { _dot.SetState(running, _starting); }
            RefreshBrowserButton();
        }

        private string FileLabel(string path)
        {
            if (path == null || path.Length == 0) { return "—"; }
            try
            {
                string name = Path.GetFileName(path.TrimEnd('\\'));
                if (name == null || name.Length == 0) { return path; }
                return name;
            }
            catch { return path; }
        }

        private int CountEnabled()
        {
            int n = 0;
            for (int i = 0; i < _plugins.Count; i++) { if (_plugins[i].Enabled) { n++; } }
            return n;
        }

        // ---------------------------------------------------------- 跳转与打开

        private void JumpToPort()
        {
            SwitchPage(1);
            FlashCard(_cardAdv);
            try { _txtPort.Focus(); _txtPort.SelectAll(); } catch { }
        }

        private void JumpToOpenMode()
        {
            SwitchPage(1);
            FlashCard(_cardOpen);
        }

        private void FlashCard(Card c)
        {
            if (c == null) { return; }
            c.Flash();
        }

        // 用资源管理器定位到文件（文件不存在就退化成打开所在目录）
        private void RevealFile(string path)
        {
            if (path == null || path.Length == 0) { return; }
            try
            {
                if (Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                    return;
                }
                if (File.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    return;
                }
                string dir = SafeDir(path);
                if (dir.Length > 0 && Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                    return;
                }
                AppendLog("[面板] 找不到：" + path + "\r\n");
            }
            catch (Exception ex)
            {
                AppendLog("[面板] 打开失败：" + ex.Message + "\r\n");
            }
        }

        // ---------------------------------------------------------- 设置回调

        private void OnSimpleSettingChanged()
        {
            _cfg.AutoOpen        = _swAutoOpen.Checked;
            _cfg.AutoTray        = _swAutoTray.Checked;
            _cfg.StopWithPanel   = _swStopWithPanel.Checked;
            _cfg.BalloonOnStop   = _swBalloon.Checked;
            _cfg.DevMode         = _swDevMode.Checked;
            _cfg.Save();
            LogEvent("设置已更新：自动打开=" + _cfg.AutoOpen + " 自动托盘=" + _cfg.AutoTray +
                     " 关面板停服务=" + _cfg.StopWithPanel + " 气泡=" + _cfg.BalloonOnStop +
                     " 开发者模式=" + _cfg.DevMode);
        }

        private void OnAutoStartChanged()
        {
            bool want = _swAutoStart.Checked;
            string err;
            if (!AutoStart.Set(want, out err))
            {
                _swAutoStart.SetCheckedSilent(!want);      // 回滚界面，别让开关撒谎
                MessageBox.Show(err, "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("[面板] 设置开机自启失败：" + err + "\r\n");
                LogEvent("设置开机自启失败：" + err);
                return;
            }
            _cfg.AutoStart = want;
            _cfg.Save();
            AppendLog("[面板] 开机自启已" + (want ? "开启" : "关闭") + "。\r\n");
            LogEvent("开机自启已" + (want ? "开启" : "关闭"));
        }

        private void OnOpenModeChanged()
        {
            _cfg.BrowserMode = (_segOpen.Index == 1) ? "app" : "tab";
            _cfg.Save();
            RefreshBrowserButton();
            RefreshNetInfo();
            AppendLog("[面板] 网页打开方式：" + (_cfg.BrowserMode == "app" ? "无地址栏独立窗口" : "普通窗口") + "\r\n");
            LogEvent("打开方式改为 " + _cfg.BrowserMode);
            MarkPageDirty();
        }

        private void ShowBrowserMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Theme.Ui(9f);

            m.Items.Add("系统默认浏览器", null, delegate { SetBrowser(""); });

            List<string[]> list = FindBrowsers();
            if (list.Count > 0) { m.Items.Add(new ToolStripSeparator()); }
            for (int i = 0; i < list.Count; i++)
            {
                string path = list[i][1];
                string name = list[i][0];
                m.Items.Add(name, null, delegate { SetBrowser(path); });
            }

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("选择其他程序…", null, delegate { PickBrowser(); });

            m.Show(_btnBrowser, new Point(0, _btnBrowser.Height + 2));
        }

        private void SetBrowser(string path)
        {
            _cfg.BrowserPath = path == null ? "" : path;
            _cfg.Save();
            RefreshBrowserButton();
            RefreshNetInfo();
            AppendLog("[面板] 默认浏览器：" + BrowserDisplayName() + "\r\n");
            LogEvent("默认浏览器改为 " + BrowserDisplayName());
            MarkPageDirty();
        }

        private void PickBrowser()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title  = "选择浏览器可执行文件";
                d.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) { SetBrowser(d.FileName); }
            }
        }

        private void RefreshBrowserButton()
        {
            if (_btnBrowser == null) { return; }
            _btnBrowser.Text = BrowserDisplayName();
            _btnBrowser.Invalidate();
        }

        private string BrowserDisplayName()
        {
            string p = _cfg.BrowserPath;
            if (p != null && p.Length > 0)
            {
                try { return Path.GetFileNameWithoutExtension(p); }
                catch { return p; }
            }
            if (_cfg.BrowserMode == "app")
            {
                string auto = FindBrowser();
                if (auto == null || auto.Length == 0) { return "自动（未找到 Chromium）"; }
                try { return "自动 · " + Path.GetFileNameWithoutExtension(auto); }
                catch { return "自动探测"; }
            }
            return "系统默认浏览器";
        }

        private void OnSavePort()
        {
            int p;
            if (!int.TryParse(_txtPort.Text.Trim(), out p) || p < 1 || p > 65535)
            {
                MessageBox.Show("端口必须是 1 ~ 65535 之间的整数。", "端口无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtPort.Text = _cfg.Port.ToString();
                return;
            }
            _cfg.Port = p;
            _cfg.Save();
            AppendLog("[面板] 端口已改为 " + p + "，重启面板后生效。\r\n");
            LogEvent("端口已改为 " + p);
            RefreshNetInfo();
            MarkPageDirty();
            MessageBox.Show("端口已保存为 " + p + "。\r\n重启面板后生效。", "端口",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal static string SafeDir(string file)
        {
            try
            {
                string d = Path.GetDirectoryName(file);
                if (d == null) { return ""; }
                return d + "\\";
            }
            catch { return ""; }
        }

        private static string CfgPath
        {
            get { return SafeDir(Application.ExecutablePath) + "panel.config.ini"; }
        }
    }

    // ==========================================================================
    //  插件列表里的一行：名称 + 版本 + 右侧开关（受保护的显示「核心」标签）
    // ==========================================================================
    internal class PluginRow : Control, IAnim
    {
        public PluginItem Item;
        public bool       Locked;
        public event EventHandler Toggled;

        private bool  _hover;
        private float _fade;
        private float _knob;

        public PluginRow(PluginItem item)
        {
            this.Item      = item;
            this.BackColor = Theme.Card;
            this.Cursor    = Cursors.Hand;
            this.TabStop   = false;
            _knob          = item.Enabled ? 1f : 0f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Anim.Add(this); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Anim.Add(this); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!Locked && !Item.Protected)
            {
                Item.Enabled = !Item.Enabled;
                Anim.Add(this);
                Invalidate();
                if (Toggled != null) { Toggled(this, EventArgs.Empty); }
            }
            base.OnMouseDown(e);
        }

        public bool AnimStep()
        {
            _fade = Anim.Approach(_fade, (_hover && !Locked) ? 1f : 0f, 0.20f);
            _knob = Anim.Approach(_knob, Item.Enabled ? 1f : 0f, 0.18f);
            return _fade != ((_hover && !Locked) ? 1f : 0f) || _knob != (Item.Enabled ? 1f : 0f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool dim = Locked && !Item.Protected;

            if (_fade > 0.02f)
            {
                Draw.Fill(g, new Rectangle(0, Theme.Px(1), this.Width, this.Height - Theme.Px(2)),
                          Draw.Alpha(Theme.Hover, _fade), Theme.Px(8));
            }

            Color fg  = dim ? Theme.SubText : Theme.Text;
            Color sub = Theme.SubText;

            int tail = Theme.Px(130);
            Rectangle nameRect = new Rectangle(Theme.Px(10), 0, this.Width - tail, this.Height);
            Draw.TextClip(g, Item.Display, Theme.Ui(9.3f), fg, nameRect, ContentAlignment.MiddleLeft);

            int nameW = TextRenderer.MeasureText(Item.Display, Theme.Ui(9.3f)).Width;
            int nameMax = this.Width - Theme.Px(150);
            if (nameW > nameMax) { nameW = nameMax; }
            Rectangle verRect = new Rectangle(Theme.Px(14) + nameW, 0,
                                              this.Width - nameW - Theme.Px(150), this.Height);
            Draw.TextClip(g, Item.Version, Theme.Ui(7.6f), sub, verRect, ContentAlignment.MiddleLeft);

            if (Item.Protected)
            {
                int tw = Theme.Px(36), thh = Theme.Px(18);
                Rectangle tag = new Rectangle(this.Width - Theme.Px(104), (this.Height - thh) / 2, tw, thh);
                Draw.Fill(g, tag, Theme.AccentSoft, Theme.Px(5));
                Draw.Text(g, "核心", Theme.Ui(7.2f), Theme.AccentText, tag, ContentAlignment.MiddleCenter);
            }

            int sw = Theme.Px(40), sh = Theme.Px(20);
            int sx = this.Width - sw - Theme.Px(12);
            int sy = (this.Height - sh) / 2;
            Rectangle track = new Rectangle(sx, sy, sw, sh);

            Color on  = Theme.Accent;
            Color off = dim ? Theme.BorderSoft : Theme.Border;
            Color tc  = Item.Protected ? Theme.Accent : Draw.Mix(off, on, Anim.Ease(_knob));
            Draw.Fill(g, track, tc, sh / 2);

            int pad  = Theme.Px(2);
            int knob = sh - pad * 2;
            int kx   = (int)Math.Round(track.X + pad + (track.Width - pad * 2 - knob) * Anim.Ease(_knob));
            using (SolidBrush b = new SolidBrush(Theme.Card))
            {
                g.FillEllipse(b, kx, track.Y + pad, knob, knob);
            }
        }
    }
}
