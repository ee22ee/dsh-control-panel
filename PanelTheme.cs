using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshPanel
{
    // ==========================================================================
    //  配色板：浅色「蓝白」与深色「夜间」。参考 Steamcommunity_302 的低对比、
    //  大圆角、柔和填充：页面底色比卡片深一档，卡片再比内嵌面浅一档。
    // ==========================================================================
    internal sealed class Pal
    {
        public Color Bg, Shell, Card, Panel2;
        public Color Text, SubText;
        public Color Border, BorderSoft;
        public Color Accent, AccentDeep, AccentSoft, AccentText;
        public Color Ok, OkSoft, Warn, Danger, DangerSoft;
        public Color Hover;
        public Color TitleBg, TitleText, TitleSub;
    }

    internal static class Theme
    {
        public static bool Dark;

        // 高 DPI 缩放系数：只在 OnLoad 里由 DpiScale() 写一次。
        // 【关键】WinForms 只帮我们缩放控件的 bounds，自绘代码里的字面量（内边距、
        // 图案尺寸、圆角、行距）它一概不管 —— 这些必须自己过一遍 Px()/Pxf()，
        // 否则 200% 下图案和留白只有字号的一半大。字号是 pt（物理单位），
        // 系统已经放大过了，绝不能再乘这个系数。
        public static float S = 1f;
        public static int   Px(float v)  { return (int)Math.Round(v * S); }
        public static float Pxf(float v) { return v * S; }

        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        private static readonly Pal Light = new Pal
        {
            Bg        = C(237, 240, 246),
            Shell     = C(231, 235, 242),
            Card      = C(255, 255, 255),
            Panel2    = C(245, 248, 252),
            Text      = C( 33,  41,  54),
            SubText   = C(120, 133, 150),
            Border    = C(223, 229, 238),
            BorderSoft= C(236, 241, 248),
            Accent    = C( 47, 111, 224),
            AccentDeep= C( 33,  90, 190),
            AccentSoft= C(232, 241, 254),
            AccentText= C( 37,  96, 200),
            Ok        = C( 22, 158, 108),
            OkSoft    = C(226, 246, 237),
            Warn      = C(226, 138,  45),
            Danger    = C(214,  70,  70),
            DangerSoft= C(253, 236, 236),
            Hover     = C(243, 246, 251),
            TitleBg   = C(255, 255, 255),
            TitleText = C( 28,  36,  48),
            TitleSub  = C(138, 149, 164)
        };

        private static readonly Pal Night = new Pal
        {
            Bg        = C( 26,  29,  34),
            Shell     = C( 32,  36,  42),
            Card      = C( 38,  42,  49),
            Panel2    = C( 46,  51,  59),
            Text      = C(232, 236, 242),
            SubText   = C(140, 150, 165),
            Border    = C( 55,  61,  70),
            BorderSoft= C( 47,  52,  60),
            Accent    = C( 84, 148, 240),
            AccentDeep= C( 58, 118, 214),
            AccentSoft= C( 40,  55,  77),
            AccentText= C(133, 182, 255),
            Ok        = C( 62, 196, 140),
            OkSoft    = C( 32,  58,  48),
            Warn      = C(240, 168,  80),
            Danger    = C(236, 100, 100),
            DangerSoft= C( 62,  40,  44),
            Hover     = C( 44,  49,  57),
            TitleBg   = C( 32,  36,  42),
            TitleText = C(226, 231, 238),
            TitleSub  = C(130, 141, 156)
        };

        private static Pal P { get { return Dark ? Night : Light; } }

        public static Color Bg         { get { return P.Bg;         } }
        public static Color Shell      { get { return P.Shell;      } }
        public static Color Card       { get { return P.Card;       } }
        public static Color Panel2     { get { return P.Panel2;     } }
        public static Color Text       { get { return P.Text;       } }
        public static Color SubText    { get { return P.SubText;    } }
        public static Color Border     { get { return P.Border;     } }
        public static Color BorderSoft { get { return P.BorderSoft; } }
        public static Color Accent     { get { return P.Accent;     } }
        public static Color AccentDeep { get { return P.AccentDeep; } }
        public static Color AccentSoft { get { return P.AccentSoft; } }
        public static Color AccentText { get { return P.AccentText; } }
        public static Color Ok         { get { return P.Ok;         } }
        public static Color OkSoft     { get { return P.OkSoft;     } }
        public static Color Warn       { get { return P.Warn;       } }
        public static Color Danger     { get { return P.Danger;     } }
        public static Color DangerSoft { get { return P.DangerSoft; } }
        public static Color Hover      { get { return P.Hover;      } }
        public static Color TitleBg    { get { return P.TitleBg;    } }
        public static Color TitleText  { get { return P.TitleText;  } }
        public static Color TitleSub   { get { return P.TitleSub;   } }

        // 日志区两种主题下都保持深底绿字，终端感一致
        public static Color LogBg  { get { return Dark ? C(15, 17, 20) : C(26, 29, 34); } }
        public static Color LogFg  { get { return C(176, 224, 178); } }
        public static Color LogDim { get { return C(120, 136, 154); } }

        // 把当前主题的全部颜色按固定顺序拍一张快照。切换主题时先拍「切换前」、切完再拍
        // 「切换后」，两张表逐一配对就得到「旧色 → 新色」映射，用来就地重涂控件树
        // （见 PanelUi.Retint）。顺序必须与下面这个数组一致，不要再随手调整。
        public static Color[] Snapshot()
        {
            return new Color[]
            {
                Bg, Shell, Card, Panel2,
                Text, SubText, Border, BorderSoft,
                Accent, AccentDeep, AccentSoft, AccentText,
                Ok, OkSoft, Warn, Danger, DangerSoft, Hover,
                TitleBg, TitleText, TitleSub,
                LogBg, LogFg, LogDim
            };
        }

        // ---- 字体缓存 ----
        // 【必须缓存】WinForms 的 Font 是 GDI+ 句柄：每次 OnPaint 里 new 一个，
        // 动画一跑（60fps）几秒钟就会把进程句柄打满然后整窗变黑。这里按
        // 字号+字形做缓存，整个进程生命周期内只创建十几种字体。
        // 注意：pt 是物理单位，高 DPI 下系统自己会放大 —— 除了屏幕放不下时的
        // 整体降级（FontScale），绝不能再乘 S。
        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        // 【屏幕适配】当屏幕小到放不下设计尺寸时，会把 S 压到 DPI 比例以下。
        // 这时光缩放控件 bounds 不够 —— 字号是 pt（物理单位），不跟着缩就会溢出。
        // FontScale = S / 系统DPI比例，用来把 pt 按比例缩小；正常情况恒为 1。
        public static float FontScale = 1f;

        private static Font F(string family, float size, FontStyle style)
        {
            size = size * FontScale;
            string key = family + "|" + size.ToString("0.##") + "|" + (int)style;
            Font f;
            if (_fonts.TryGetValue(key, out f)) { return f; }
            try { f = new Font(family, size, style); }
            catch { f = new Font(FontFamily.GenericSansSerif, size, style); }
            _fonts[key] = f;
            return f;
        }

        public static Font Ui(float size)                   { return F("Microsoft YaHei UI", size, FontStyle.Regular); }
        public static Font Ui(float size, FontStyle style)  { return F("Microsoft YaHei UI", size, style); }
        public static Font Mono(float size)                 { return F("Consolas", size, FontStyle.Regular); }
        public static Font Num(float size, FontStyle style) { return F("Segoe UI", size, style); }
    }

    // ==========================================================================
    //  动画引擎
    //  只有一个 16ms 的全局定时器，而且只在真有动画在跑时才启动 —— 空闲时零开销。
    //  控件把自己的「本帧该不该继续动画」写在 AnimStep() 里，返回 false 就自动下架。
    // ==========================================================================
    internal interface IAnim
    {
        bool AnimStep();
    }

    internal static class Anim
    {
        private static readonly System.Windows.Forms.Timer T;
        private static readonly List<Control> Items = new List<Control>();

        static Anim()
        {
            T = new System.Windows.Forms.Timer();
            T.Interval = 16;
            T.Tick += delegate { Step(); };
        }

        public static void Add(Control c)
        {
            if (c == null || c.IsDisposed) { return; }
            if (Items.Contains(c)) { return; }
            Items.Add(c);
            if (!T.Enabled) { T.Start(); }
        }

        public static void Drop(Control c)
        {
            Items.Remove(c);
            if (Items.Count == 0) { T.Stop(); }
        }

        public static void ClearAll()
        {
            Items.Clear();
            T.Stop();
        }

        private static void Step()
        {
            Control[] arr = Items.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                Control c = arr[i];
                if (c.IsDisposed) { Items.Remove(c); continue; }
                IAnim a = c as IAnim;
                bool more = (a == null) ? false : a.AnimStep();
                // 窗体自己那几路动画（分体按钮、切页位移）动的都是**带句柄的子控件**，
                // 子控件会自己重画；再给整个窗体 Invalidate 等于每帧把 2000×1560 的
                // 背景和所有卡片重画一遍，200% 缩放下正好卡在 60fps 边缘 → 看着顿。
                if (!(c is PanelForm) && !c.IsDisposed) { c.Invalidate(); }
                if (!more) { Items.Remove(c); }
            }
            if (Items.Count == 0) { T.Stop(); }
        }

        // 缓动：quadratic ease-out，起步快收尾慢，看起来软一点
        public static float Ease(float t)
        {
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            return 1f - (1f - t) * (1f - t);
        }

        // 更软的三次缓出：收尾更慢、落地更轻，切页用这个
        public static float EaseSoft(float t)
        {
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            float u = 1f - t;
            return 1f - u * u * u;
        }

        // 两头都平缓（smoothstep），淡入淡出用它最不容易看出「起步一顿」
        public static float EaseBoth(float t)
        {
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            return t * t * (3f - 2f * t);
        }

        // 强缓出（≈ cubic-bezier(.22, 1, .36, 1)）：起步就快、收尾拖长，新页升上来用它
        public static float EaseOutQuart(float t)
        {
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            float u = 1f - t;
            return 1f - u * u * u * u;
        }

        // 缓入（≈ cubic-bezier(.55, .06, .68, .19)）：慢慢起、后段快，旧页退场用它
        public static float EaseInCubic(float t)
        {
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            return t * t * t;
        }

        public static float Approach(float cur, float target, float step)
        {
            if (cur < target) { cur += step; if (cur > target) { cur = target; } }
            else if (cur > target) { cur -= step; if (cur < target) { cur = target; } }
            return cur;
        }
    }

    // ==========================================================================
    //  绘图工具
    // ==========================================================================
    internal static class Draw
    {
        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) { return a; }
            if (t >= 1f) { return b; }
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, float a)
        {
            int v = (int)Math.Round(255f * a);
            if (v < 0) { v = 0; }
            if (v > 255) { v = 255; }
            return Color.FromArgb(v, c.R, c.G, c.B);
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 1 || r.Width <= 2 || r.Height <= 2)
            {
                p.AddRectangle(r);
                return p;
            }
            int d = radius * 2;
            if (d > r.Width)  { d = r.Width;  }
            if (d > r.Height) { d = r.Height; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, Rectangle r, Color c, int radius)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, p);
            }
        }

        public static void Stroke(Graphics g, Rectangle r, Color c, int radius, float w)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, w))
            {
                g.DrawPath(pen, p);
            }
        }

        // 顶部一道极淡的白色高光，卡片的「柔和」感基本靠它
        public static void Gloss(Graphics g, Rectangle r, int radius, int alpha)
        {
            if (r.Height < 8) { return; }
            Rectangle rr = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, Math.Max(2, r.Height / 3));
            using (GraphicsPath p = Round(rr, radius))
            using (LinearGradientBrush b = new LinearGradientBrush(
                       rr, Color.FromArgb(alpha, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                       LinearGradientMode.Vertical))
            {
                g.FillPath(b, p);
            }
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, ContentAlignment align)
        {
            TextFormatFlags flags = TextFormatFlags.NoPadding;
            if (align == ContentAlignment.MiddleCenter)   { flags |= TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter; }
            else if (align == ContentAlignment.MiddleLeft){ flags |= TextFormatFlags.Left | TextFormatFlags.VerticalCenter; }
            else if (align == ContentAlignment.MiddleRight){ flags |= TextFormatFlags.Right | TextFormatFlags.VerticalCenter; }
            else                                          { flags |= TextFormatFlags.Left | TextFormatFlags.Top; }
            TextRenderer.DrawText(g, s, f, r, c, flags);
        }

        // 单行裁剪：超宽就截断加省略号。TextRenderer 不带 EndEllipsis 的话会硬溢出到卡片外。
        public static void TextClip(Graphics g, string s, Font f, Color c, Rectangle r, ContentAlignment align)
        {
            if (s == null) { s = ""; }
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                                  | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            if (align == ContentAlignment.MiddleCenter)    { flags |= TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter; }
            else if (align == ContentAlignment.MiddleLeft) { flags |= TextFormatFlags.Left | TextFormatFlags.VerticalCenter; }
            else if (align == ContentAlignment.MiddleRight){ flags |= TextFormatFlags.Right | TextFormatFlags.VerticalCenter; }
            else                                           { flags |= TextFormatFlags.Left | TextFormatFlags.VerticalCenter; }
            TextRenderer.DrawText(g, s, f, r, c, flags);
        }
    }

    // ==========================================================================
    //  矢量图案。全部用笔画出来，项目里不带任何图片资源。
    // ==========================================================================
    internal static class Glyphs
    {
        public const int None = 0;
        public const int Grid = 1;      // 服务（四宫格）
        public const int Gear = 2;      // 设置
        public const int Info = 3;      // 关于
        public const int Exit = 4;      // 退出
        public const int Play = 5;
        public const int Stop = 6;
        public const int Restart = 7;   // 循环箭头
        public const int Globe = 8;     // 打开网页
        public const int Dash = 9;      // 最小化
        public const int Cross = 10;    // 关闭
        public const int Moon = 11;
        public const int Sun = 12;
        public const int ChevronDown = 13;
        public const int ChevronUp = 14;
        public const int Folder = 15;
        public const int Link = 16;
        public const int List = 17;
        public const int Plug = 18;
        public const int ChevronRight = 19;   // 网络信息行右侧的「›」跳转指示

        public static void Render(Graphics g, int kind, Rectangle r, Color c, float weight)
        {
            if (kind == None) { return; }
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            float u  = Math.Min(r.Width, r.Height) * 0.5f;
            float w  = Math.Max(1.4f, weight);

            using (Pen p = new Pen(c, w))
            {
                p.StartCap = LineCap.Round;
                p.EndCap   = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                switch (kind)
                {
                    case Grid:
                        {
                            float s = u * 0.62f;
                            float gx = u * 0.10f;
                            g.DrawRectangle(p, cx - s - gx, cy - s - gx, s * 2f, s * 2f);
                            g.DrawRectangle(p, cx + gx, cy - s - gx, s * 2f, s * 2f);
                            g.DrawRectangle(p, cx - s - gx, cy + gx, s * 2f, s * 2f);
                            g.DrawRectangle(p, cx + gx, cy + gx, s * 2f, s * 2f);
                            break;
                        }
                    case Gear:
                        {
                            float rad = u * 0.46f;
                            g.DrawEllipse(p, cx - rad, cy - rad, rad * 2f, rad * 2f);
                            float ir = rad * 0.42f;
                            g.DrawEllipse(p, cx - ir, cy - ir, ir * 2f, ir * 2f);
                            for (int i = 0; i < 8; i++)
                            {
                                double ang = i * Math.PI / 4.0;
                                float x1 = cx + (float)Math.Cos(ang) * rad;
                                float y1 = cy + (float)Math.Sin(ang) * rad;
                                float x2 = cx + (float)Math.Cos(ang) * (rad + u * 0.20f);
                                float y2 = cy + (float)Math.Sin(ang) * (rad + u * 0.20f);
                                g.DrawLine(p, x1, y1, x2, y2);
                            }
                            break;
                        }
                    case Info:
                        {
                            float rad = u * 0.62f;
                            g.DrawEllipse(p, cx - rad, cy - rad, rad * 2f, rad * 2f);
                            using (SolidBrush b = new SolidBrush(c))
                            {
                                float dr = Math.Max(1.3f, w * 0.85f);
                                g.FillEllipse(b, cx - dr / 2f, cy - rad * 0.52f - dr / 2f, dr, dr);
                            }
                            g.DrawLine(p, cx, cy - rad * 0.10f, cx, cy + rad * 0.46f);
                            break;
                        }
                    case Exit:
                        {
                            float rad = u * 0.60f;
                            float bx = cx + u * 0.10f;
                            g.DrawLines(p, new PointF[] {
                                new PointF(cx - rad, cy - rad),
                                new PointF(bx,      cy - rad),
                                new PointF(bx,      cy + rad),
                                new PointF(cx - rad, cy + rad),
                                new PointF(cx - rad, cy - rad)
                            });
                            g.DrawLine(p, cx - rad * 0.30f, cy, bx + rad * 0.30f, cy);
                            g.DrawLines(p, new PointF[] {
                                new PointF(bx + rad * 0.02f, cy - rad * 0.34f),
                                new PointF(bx + rad * 0.34f, cy),
                                new PointF(bx + rad * 0.02f, cy + rad * 0.34f)
                            });
                            break;
                        }
                    case Play:
                        {
                            float h = u * 0.66f;
                            float wd = h * 0.86f;
                            using (SolidBrush b = new SolidBrush(c))
                            {
                                PointF[] pts = new PointF[] {
                                    new PointF(cx - wd * 0.42f, cy - h),
                                    new PointF(cx + wd * 0.62f, cy),
                                    new PointF(cx - wd * 0.42f, cy + h)
                                };
                                g.FillPolygon(b, pts);
                            }
                            break;
                        }
                    case Stop:
                        {
                            float s = u * 0.52f;
                            using (SolidBrush b = new SolidBrush(c))
                            {
                                RectangleF rr = new RectangleF(cx - s, cy - s, s * 2f, s * 2f);
                                g.FillRectangle(b, rr);
                            }
                            break;
                        }
                    case Restart:
                        {
                            // 圆润的「↻」：几乎整圈的粗弧 + 右上方一个粗箭头，
                            // 箭头落在弧的**终点**、朝顺时针切线方向（旋转方向和箭头一致）
                            float rad   = u * 0.58f;
                            float thick = Math.Max(1.8f, w * 1.15f);
                            using (Pen p2 = new Pen(c, thick))
                            {
                                p2.StartCap = LineCap.Round;
                                p2.EndCap   = LineCap.Round;
                                p2.LineJoin = LineJoin.Round;

                                // GDI+ 角度：0° 在 +x（右），顺时针增大。从 350° 扫 310° → 终点 300°
                                g.DrawArc(p2, cx - rad, cy - rad, rad * 2f, rad * 2f, 350f, 310f);

                                double a  = 300.0 * Math.PI / 180.0;
                                float  tx = cx + (float)Math.Cos(a) * rad;
                                float  ty = cy + (float)Math.Sin(a) * rad;
                                float  dx = (float)(-Math.Sin(a));      // 顺时针切线方向
                                float  dy = (float)( Math.Cos(a));
                                float  len = u * 0.30f;
                                float  sp  = 0.62f;
                                float  cs  = (float)Math.Cos(sp), sn = (float)Math.Sin(sp);
                                float  bxx = -dx, byy = -dy;            // 往回
                                g.DrawLines(p2, new PointF[]
                                {
                                    new PointF(tx + (bxx * cs - byy * sn) * len, ty + (bxx * sn + byy * cs) * len),
                                    new PointF(tx, ty),
                                    new PointF(tx + (bxx * cs + byy * sn) * len, ty + (-bxx * sn + byy * cs) * len)
                                });
                            }
                            break;
                        }
                    case Globe:
                        {
                            // 圆润的地球：外圈 + 一条经线 + 赤道，都是圆头粗线
                            float rad   = u * 0.58f;
                            float thick = Math.Max(1.8f, w * 1.15f);
                            using (Pen p2 = new Pen(c, thick))
                            {
                                p2.StartCap = LineCap.Round;
                                p2.EndCap   = LineCap.Round;
                                p2.LineJoin = LineJoin.Round;
                                g.DrawEllipse(p2, cx - rad, cy - rad, rad * 2f, rad * 2f);
                                g.DrawEllipse(p2, cx - rad * 0.46f, cy - rad, rad * 0.92f, rad * 2f);
                                g.DrawLine(p2, cx - rad, cy, cx + rad, cy);
                            }
                            break;
                        }
                    case Dash:
                        {
                            g.DrawLine(p, cx - u * 0.42f, cy, cx + u * 0.42f, cy);
                            break;
                        }
                    case Cross:
                        {
                            float s = u * 0.40f;
                            g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                            g.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
                            break;
                        }
                    case Moon:
                        {
                            float rad = u * 0.62f;
                            g.DrawArc(p, cx - rad, cy - rad, rad * 2f, rad * 2f, 60, 250);
                            g.DrawArc(p, cx - rad * 0.28f, cy - rad * 1.02f, rad * 1.86f, rad * 1.86f, 210, 130);
                            break;
                        }
                    case Sun:
                        {
                            float rad = u * 0.34f;
                            g.DrawEllipse(p, cx - rad, cy - rad, rad * 2f, rad * 2f);
                            for (int i = 0; i < 8; i++)
                            {
                                double a = i * Math.PI / 4.0;
                                float x1 = cx + (float)Math.Cos(a) * (rad + u * 0.14f);
                                float y1 = cy + (float)Math.Sin(a) * (rad + u * 0.14f);
                                float x2 = cx + (float)Math.Cos(a) * (rad + u * 0.32f);
                                float y2 = cy + (float)Math.Sin(a) * (rad + u * 0.32f);
                                g.DrawLine(p, x1, y1, x2, y2);
                            }
                            break;
                        }
                    case ChevronDown:
                        {
                            float s = u * 0.34f;
                            g.DrawLines(p, new PointF[] {
                                new PointF(cx - s, cy - s * 0.6f),
                                new PointF(cx,     cy + s * 0.7f),
                                new PointF(cx + s, cy - s * 0.6f)
                            });
                            break;
                        }
                    case ChevronUp:
                        {
                            float s = u * 0.34f;
                            g.DrawLines(p, new PointF[] {
                                new PointF(cx - s, cy + s * 0.6f),
                                new PointF(cx,     cy - s * 0.7f),
                                new PointF(cx + s, cy + s * 0.6f)
                            });
                            break;
                        }
                    case ChevronRight:
                        {
                            float s = u * 0.34f;
                            g.DrawLines(p, new PointF[] {
                                new PointF(cx - s * 0.55f, cy - s),
                                new PointF(cx + s * 0.65f, cy),
                                new PointF(cx - s * 0.55f, cy + s)
                            });
                            break;
                        }
                    case Folder:
                        {
                            float wd = u * 0.66f;
                            float ht = u * 0.52f;
                            g.DrawLines(p, new PointF[] {
                                new PointF(cx - wd, cy - ht),
                                new PointF(cx - wd * 0.20f, cy - ht),
                                new PointF(cx + wd * 0.02f, cy - ht * 0.66f),
                                new PointF(cx + wd, cy - ht * 0.66f),
                                new PointF(cx + wd, cy + ht),
                                new PointF(cx - wd, cy + ht),
                                new PointF(cx - wd, cy - ht)
                            });
                            break;
                        }
                    case Link:
                        {
                            g.DrawLines(p, new PointF[] {
                                new PointF(cx - u * 0.10f, cy - u * 0.50f),
                                new PointF(cx + u * 0.55f, cy - u * 0.50f),
                                new PointF(cx + u * 0.55f, cy + u * 0.15f)
                            });
                            g.DrawLine(p, cx - u * 0.10f, cy - u * 0.50f, cx + u * 0.55f, cy + u * 0.15f);
                            g.DrawArc(p, cx - u * 0.62f, cy - u * 0.20f, u * 1.0f, u * 1.0f, 150, 240);
                            break;
                        }
                    case List:
                        {
                            float x1 = cx - u * 0.55f;
                            float x2 = cx + u * 0.55f;
                            for (int i = 0; i < 3; i++)
                            {
                                float y = cy + (i - 1) * u * 0.42f;
                                g.DrawLine(p, x1, y, x1 + u * 0.16f, y);
                                g.DrawLine(p, x1 + u * 0.30f, y, x2, y);
                            }
                            break;
                        }
                    case Plug:
                        {
                            float rad = u * 0.40f;
                            g.DrawArc(p, cx - rad, cy - rad * 0.20f, rad * 2f, rad * 2f, 180, 180);
                            g.DrawLine(p, cx - rad * 0.55f, cy + rad * 0.80f, cx - rad * 0.55f, cy + rad * 1.30f);
                            g.DrawLine(p, cx + rad * 0.55f, cy + rad * 0.80f, cx + rad * 0.55f, cy + rad * 1.30f);
                            g.DrawLine(p, cx - rad * 0.55f, cy - rad * 0.20f, cx - rad * 0.55f, cy - rad * 0.90f);
                            g.DrawLine(p, cx + rad * 0.55f, cy - rad * 0.20f, cx + rad * 0.55f, cy - rad * 0.90f);
                            break;
                        }
                }
            }
            g.SmoothingMode = old;
        }
    }

    // ==========================================================================
    //  左侧导航项：圆角柔和填充 + 左侧强调色小竖条，鼠标经过有渐显底色
    // ==========================================================================
    internal class NavButton : Control, IAnim
    {
        private bool  _hover;
        private bool  _selected;
        private float _fade;       // 0..1 悬停渐显
        private float _bar;        // 0..1 选中竖条

        public int Glyph;

        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) { return; }
                _selected = value;
                Anim.Add(this);
                Invalidate();
            }
        }

        public NavButton(string text, int glyph)
        {
            this.Text      = text;
            this.Glyph     = glyph;
            this.Font      = Theme.Ui(10f);
            this.Cursor    = Cursors.Hand;
            this.TabStop   = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Anim.Add(this); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Anim.Add(this); Invalidate(); base.OnMouseLeave(e); }

        public bool AnimStep()
        {
            _fade = Anim.Approach(_fade, _hover ? 1f : 0f, 0.14f);
            _bar  = Anim.Approach(_bar,  _selected ? 1f : 0f, 0.18f);
            return !(_fade == (_hover ? 1f : 0f) && _bar == (_selected ? 1f : 0f));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new Rectangle(0, 0, this.Width, this.Height);
            int radius = Theme.Px(9);

            if (_selected)
            {
                Draw.Fill(g, r, Theme.AccentSoft, radius);
            }
            else if (_fade > 0.01f)
            {
                Draw.Fill(g, r, Draw.Alpha(Theme.Hover, Math.Min(1f, _fade)), radius);
            }

            // 选中竖条：高度随动画长出来
            if (this.Width > Theme.Px(60) && _bar > 0.02f)
            {
                int h  = (int)Math.Round((this.Height - Theme.Pxf(18)) * _bar);
                int y  = (this.Height - h) / 2;
                Rectangle bar = new Rectangle(0, y, Theme.Px(3), h);
                Draw.Fill(g, bar, Theme.Accent, Theme.Px(2));
            }

            Color fg = _selected ? Theme.AccentText : Draw.Mix(Theme.Text, Theme.AccentText, _fade * 0.5f);
            int gsz = Theme.Px(17);
            Rectangle gr = new Rectangle(Theme.Px(14), (this.Height - gsz) / 2, gsz, gsz);
            if (this.Glyph != Glyphs.None)
            {
                Glyphs.Render(g, this.Glyph, gr, fg, 1.6f);
            }

            int tx = this.Glyph != Glyphs.None ? Theme.Px(14) + gsz + Theme.Px(11) : Theme.Px(18);
            Rectangle tr = new Rectangle(tx, 0, this.Width - tx - Theme.Px(8), this.Height);
            Draw.TextClip(g, this.Text, this.Font, fg, tr, ContentAlignment.MiddleLeft);
        }
    }

    // ==========================================================================
    //  扁平按钮
    //  kind: 0=Primary（实心）1=Ghost（描边）2=Danger（红描边）3=DangerSolid（实心红）
    // ==========================================================================
    internal class FlatButton : Control, IAnim
    {
        private bool  _hover;
        private bool  _down;
        private float _fade;

        public int  Kind = 0;
        public bool Enabled2 = true;
        public int  Glyph;
        public int  GlyphGap = 8;

        private int _radius = 9;
        public int Radius { get { return Theme.Px(_radius); } set { _radius = value; } }

        public FlatButton(string text, int kind)
        {
            this.Text      = text;
            this.Kind      = kind;
            this.Font      = Theme.Ui(9.5f);
            this.Cursor    = Cursors.Hand;
            this.TabStop   = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Anim.Add(this); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Anim.Add(this); Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true;  Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)   { _down = false; Invalidate(); base.OnMouseUp(e); }

        public bool AnimStep()
        {
            _fade = Anim.Approach(_fade, (_hover && this.Enabled && Enabled2) ? 1f : 0f, 0.16f);
            return _fade != ((_hover && this.Enabled && Enabled2) ? 1f : 0f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool on = this.Enabled && Enabled2;
            int dy  = (_down && on) ? Theme.Px(1) : 0;
            Rectangle r = new Rectangle(0, dy, this.Width - 1, this.Height - 1);

            Color fg;
            if (!on)
            {
                Draw.Fill(g, r, Theme.Panel2, Radius);
                Draw.Stroke(g, r, Theme.Border, Radius, 1f);
                fg = Theme.SubText;
            }
            else if (Kind == 0)
            {
                Color back = Draw.Mix(Theme.Accent, Theme.AccentDeep, _fade);
                if (_down) { back = Theme.AccentDeep; }
                Draw.Fill(g, r, back, Radius);
                Draw.Gloss(g, r, Radius, 26);
                fg = Color.White;
            }
            else if (Kind == 3)
            {
                Color back = _down ? ControlPaint.Dark(Theme.Danger, 0.10f)
                                   : Draw.Mix(Theme.Danger, ControlPaint.Dark(Theme.Danger, 0.14f), _fade);
                Draw.Fill(g, r, back, Radius);
                Draw.Gloss(g, r, Radius, 26);
                fg = Color.White;
            }
            else
            {
                Color line = (Kind == 2) ? Theme.Danger : Theme.Accent;
                Color back = Draw.Mix(Theme.Card, (Kind == 2) ? Theme.DangerSoft : Theme.AccentSoft, _fade);
                Draw.Fill(g, r, back, Radius);
                Draw.Stroke(g, r, line, Radius, 1f);
                fg = line;
            }

            // 文字（+可选图案）整体居中。TextRenderer.MeasureText 默认带内边距，
            // 直接拿来算居中会左偏几像素（高 DPI 下更明显），所以统一走 NoPadding。
            string txt = this.Text == null ? "" : this.Text;
            Font f     = this.Font;
            int gsz    = Theme.Px(15);
            int gap    = Theme.Px(GlyphGap);
            if (this.Glyph == Glyphs.None)
            {
                Draw.TextClip(g, txt, f, fg, new Rectangle(0, dy, this.Width, this.Height), ContentAlignment.MiddleCenter);
            }
            else
            {
                Size ts = TextRenderer.MeasureText(txt, f, new Size(int.MaxValue, int.MaxValue),
                                                   TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int total = gsz + gap + ts.Width;
                int x0    = (this.Width - total) / 2;
                Glyphs.Render(g, this.Glyph, new Rectangle(x0, (this.Height - gsz) / 2 + dy, gsz, gsz), fg, 1.7f);
                int tx = x0 + gsz + gap;
                Draw.TextClip(g, txt, f, fg, new Rectangle(tx, dy, this.Width - tx, this.Height), ContentAlignment.MiddleLeft);
            }
        }
    }

    // ==========================================================================
    //  图标按钮（方形圆角，纯图案）
    // ==========================================================================
    internal class IconButton : Control, IAnim
    {
        private bool  _hover;
        private float _fade;

        // ---- 旋转（只对「重启」图案有意义）----
        // 外层把 Spinning 置 true 就匀速转；置 false 时不立刻停，而是先算出
        // 「转到下一个整圈」还差多少度，再用 260ms 缓出把这点角度走完 —— 这样
        // 停下来时图案跟原来一模一样（永远落在 360° 的整数倍上）。
        private float _ang;            // 当前角度，约定落在 [0, 360)
        private float _stopFrom;
        private float _stopRemain;
        private float _stopT = 1f;
        private const float SpinDegPerSec = 700f;
        private const float StopSec       = 0.26f;

        public bool Spinning;
        public int   Kind = 0;
        public float Alpha = 1f;       // 由外部控制的整体透明度（0 时自动隐藏）
        public bool  Filled = false;           // true = 常驻浅色底，false = 描边

        private int _radius = 9;
        public int Radius { get { return Theme.Px(_radius); } set { _radius = value; } }

        public IconButton(int kind)
        {
            this.Kind     = kind;
            this.Cursor   = Cursors.Hand;
            this.TabStop  = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Anim.Add(this); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Anim.Add(this); Invalidate(); base.OnMouseLeave(e); }

        public bool AnimStep()
        {
            bool more = false;

            float target = (_hover && this.Enabled) ? 1f : 0f;
            if (_fade != target) { _fade = Anim.Approach(_fade, target, 0.16f); more = true; }

            if (this.Kind == Glyphs.Restart)
            {
                if (Spinning)
                {
                    _stopT = 1f;                                   // 取消正在进行的收尾
                    _ang  += SpinDegPerSec * 0.016f;
                    if (_ang >= 360f) { _ang -= 360f; }
                    more = true;
                }
                else if (_ang > 0.01f)
                {
                    if (_stopT >= 1f)
                    {
                        _stopFrom   = _ang;
                        _stopRemain = 360f - _ang;                 // 补到整圈，落点视觉上=0°
                        _stopT      = 0f;
                    }
                    _stopT += 0.016f / StopSec;
                    if (_stopT >= 1f) { _stopT = 1f; _ang = 0f; }
                    else
                    {
                        _ang = _stopFrom + _stopRemain * Anim.EaseOutQuart(_stopT);
                        if (_ang >= 360f) { _ang -= 360f; }
                    }
                    more = true;
                }
            }

            return more;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float a = Math.Max(0f, Math.Min(1f, Alpha));
            if (a <= 0.02f) { return; }

            Rectangle r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);

            Color line = Theme.AccentText;
            if (!this.Enabled) { line = Theme.SubText; }
            else if (Filled)   { line = Theme.AccentText; }

            Color bg = Draw.Alpha(Theme.Card, a);
            if (!this.Enabled)      { bg = Draw.Alpha(Theme.Panel2, a); }
            else if (Filled)        { bg = Draw.Alpha(Draw.Mix(Theme.AccentSoft, Theme.Hover, _fade), a); }
            else if (_fade > 0.01f) { bg = Draw.Alpha(Draw.Mix(Theme.Card, Theme.AccentSoft, _fade), a); }

            Draw.Fill(g, r, bg, Radius);
            Color bd = Draw.Alpha(this.Enabled ? Draw.Mix(Theme.Border, Theme.Accent, _fade) : Theme.Border, a);
            Draw.Stroke(g, r, bd, Radius, 1f);

            Color ink = Draw.Alpha(this.Enabled ? line : Theme.SubText, a);
            int gsz = (int)Math.Round(Math.Min(this.Width, this.Height) * 0.58f);
            Rectangle gr = new Rectangle((this.Width - gsz) / 2, (this.Height - gsz) / 2, gsz, gsz);

            if (this.Kind == Glyphs.Restart && _ang != 0f)
            {
                GraphicsState st = g.Save();
                g.TranslateTransform(this.Width / 2f, this.Height / 2f);
                g.RotateTransform(_ang);
                g.TranslateTransform(-this.Width / 2f, -this.Height / 2f);
                Glyphs.Render(g, this.Kind, gr, ink, 1.8f);
                g.Restore(st);
            }
            else
            {
                Glyphs.Render(g, this.Kind, gr, ink, 1.8f);
            }
        }
    }

    // ==========================================================================
    //  标题栏按钮（自绘无边框窗体专用）
    //  kind: 0=最小化 1=关闭 2=主题（月亮/太阳）
    // ==========================================================================
    internal class TitleButton : Control, IAnim
    {
        private bool  _hover;
        private float _fade;

        public int  Kind = 0;
        public bool IsDark;      // 主题按钮用：true 时画太阳（表示切回浅色）

        public TitleButton(int kind)
        {
            this.Kind      = kind;
            this.Cursor    = Cursors.Hand;
            this.TabStop   = false;
            this.Size      = new Size(38, 30);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Anim.Add(this); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Anim.Add(this); Invalidate(); base.OnMouseLeave(e); }

        public bool AnimStep()
        {
            _fade = Anim.Approach(_fade, _hover ? 1f : 0f, 0.20f);
            return _fade != (_hover ? 1f : 0f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new Rectangle(0, 0, this.Width, this.Height);
            bool closing = (Kind == 1);
            Color back = closing ? Color.FromArgb(232, 84, 84) : Theme.Hover;
            Color ink  = Theme.TitleText;

            if (_fade > 0.02f)
            {
                Draw.Fill(g, new Rectangle(Theme.Px(2), Theme.Px(2),
                                           this.Width - Theme.Px(4), this.Height - Theme.Px(4)),
                          Draw.Alpha(back, _fade), Theme.Px(7));
                if (closing) { ink = Color.White; }
            }

            int gsz = Theme.Px(15);
            Rectangle gr = new Rectangle((this.Width - gsz) / 2, (this.Height - gsz) / 2, gsz, gsz);
            int kind = (Kind == 1) ? Glyphs.Cross : ((Kind == 2) ? (IsDark ? Glyphs.Sun : Glyphs.Moon) : Glyphs.Dash);
            Glyphs.Render(g, kind, gr, ink, 1.7f);
        }
    }

    // ==========================================================================
    //  开关（胶囊 + 滑动圆点）
    // ==========================================================================
    internal class ToggleSwitch : Control, IAnim
    {
        private bool  _checked;
        private bool  _hover;
        private float _pos;        // 0 = 关，1 = 开
        private float _fade;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) { return; }
                _checked = value;
                Anim.Add(this);
                Invalidate();
                if (CheckedChanged != null) { CheckedChanged(this, EventArgs.Empty); }
            }
        }

        // 静默设置（不触发事件）：切主题重建界面时用，避免回调里读半初始化的字段
        public void SetCheckedSilent(bool v)
        {
            _checked = v;
            _pos     = v ? 1f : 0f;
            Invalidate();
        }

        public ToggleSwitch()
        {
            this.Size      = new Size(46, 24);
            this.Cursor    = Cursors.Hand;
            this.TabStop   = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Anim.Add(this); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Anim.Add(this); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (this.Enabled) { Checked = !Checked; }
            base.OnMouseDown(e);
        }

        public bool AnimStep()
        {
            _pos  = Anim.Approach(_pos,  _checked ? 1f : 0f, 0.16f);
            _fade = Anim.Approach(_fade, _hover ? 1f : 0f, 0.18f);
            return _pos != (_checked ? 1f : 0f) || _fade != (_hover ? 1f : 0f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int h = this.Height - Theme.Px(2);
            int w = this.Width  - Theme.Px(2);
            if (w < Theme.Px(8) || h < Theme.Px(4)) { return; }
            if (w < h) { w = h * 2; }

            Rectangle track = new Rectangle(Theme.Px(1), Theme.Px(1), w, h);
            int radius = h / 2;

            Color off = Draw.Mix(Theme.Border, Theme.Hover, _fade);
            Color on  = Draw.Mix(Theme.Accent, Theme.AccentDeep, _fade);
            Draw.Fill(g, track, this.Enabled ? Draw.Mix(off, on, Anim.Ease(_pos)) : Theme.Panel2, radius);

            int pad  = Theme.Px(2);
            int knob = h - pad * 2;
            float x  = track.X + pad + (track.Width - pad * 2 - knob) * Anim.Ease(_pos);
            using (SolidBrush b = new SolidBrush(this.Enabled ? Color.White : Theme.SubText))
            {
                g.FillEllipse(b, x, track.Y + pad, knob, knob);
            }
        }
    }

    // ==========================================================================
    //  分段选择器（两个及以上选项，选中块会滑动）
    // ==========================================================================
    internal class Segmented : Control, IAnim
    {
        private string[] _items = new string[0];
        private int      _index;
        private int      _hoverIdx = -1;
        private float    _pill;        // 选中块的位置（以「项」为单位）

        public event EventHandler Changed;

        public int Index
        {
            get { return _index; }
            set
            {
                if (value < 0 || value >= _items.Length || value == _index) { return; }
                _index = value;
                Anim.Add(this);
                Invalidate();
                if (Changed != null) { Changed(this, EventArgs.Empty); }
            }
        }

        public void SetIndexSilent(int v)
        {
            if (v < 0 || v >= _items.Length) { return; }
            _index = v;
            _pill  = v;
            Invalidate();
        }

        public Segmented(string[] items)
        {
            _items = items;
            this.Height    = 34;
            this.Font      = Theme.Ui(9f);
            this.Cursor    = Cursors.Hand;
            this.TabStop   = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = Hit(e.X);
            if (idx != _hoverIdx) { _hoverIdx = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hoverIdx = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int idx = Hit(e.X);
            if (idx >= 0) { Index = idx; }
            base.OnMouseDown(e);
        }

        private int Hit(int x)
        {
            if (_items.Length == 0) { return -1; }
            int w = this.Width / _items.Length;
            if (w <= 0) { return -1; }
            int i = x / w;
            if (i < 0) { i = 0; }
            if (i >= _items.Length) { i = _items.Length - 1; }
            return i;
        }

        public bool AnimStep()
        {
            _pill = Anim.Approach(_pill, _index, 0.20f);
            return _pill != _index;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_items.Length == 0) { return; }

            Rectangle r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            Draw.Fill(g, r, Theme.Panel2, Theme.Px(8));

            int w = this.Width / _items.Length;
            float px = _pill * w;
            int inset = Theme.Px(3);
            Rectangle pill = new Rectangle((int)Math.Round(px) + inset, inset,
                                           w - inset * 2, this.Height - Theme.Px(7));
            Draw.Fill(g, pill, Theme.Card, Theme.Px(6));
            Draw.Stroke(g, pill, Theme.AccentSoft, Theme.Px(6), 1f);

            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle tr = new Rectangle(i * w, 0, w, this.Height);
                bool sel = (i == _index);
                Color fg = sel ? Theme.AccentText : Theme.SubText;
                if (!sel && i == _hoverIdx) { fg = Theme.Text; }
                Draw.TextClip(g, _items[i], this.Font, fg, tr, ContentAlignment.MiddleCenter);
            }
        }
    }

    // ==========================================================================
    //  状态圆点：运行中缓慢呼吸（外圈光晕），启动中快速闪
    // ==========================================================================
    internal class StatusDot : Control, IAnim
    {
        private float _t;
        public bool Running;
        public bool Starting;

        public StatusDot()
        {
            this.Size      = new Size(16, 16);
            this.TabStop   = false;
            this.Enabled   = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        public void SetState(bool running, bool starting)
        {
            if (Running == running && Starting == starting) { return; }
            Running  = running;
            Starting = starting;
            if (running || starting) { Anim.Add(this); }
            Invalidate();
        }

        public bool AnimStep()
        {
            _t = (_t + 0.045f) % 1f;
            return Running || Starting;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cx = this.Width / 2f;
            float cy = this.Height / 2f;
            Color c  = Starting ? Theme.Warn : (Running ? Theme.Ok : Theme.SubText);

            float pulse = 0.5f + 0.5f * (float)Math.Sin(_t * Math.PI * 2.0);
            if (Running || Starting)
            {
                float hr = this.Width * (0.34f + 0.20f * pulse);
                using (SolidBrush b = new SolidBrush(Draw.Alpha(c, 0.22f - 0.12f * pulse)))
                {
                    g.FillEllipse(b, cx - hr, cy - hr, hr * 2f, hr * 2f);
                }
            }

            float rad = this.Width * 0.23f;
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillEllipse(b, cx - rad, cy - rad, rad * 2f, rad * 2f);
            }
        }
    }

    // ==========================================================================
    //  可点击的信息行：「标签 —— 值 ›」；hover 变底色，值是强调色带右下箭头
    // ==========================================================================
    internal class NetRow : Control, IAnim
    {
        private bool  _hover;
        private float _fade;

        public string Label   = "";
        public string Value   = "";
        public int    Glyph;
        public bool   Clickable;
        public string Tip     = "";

        public NetRow(string label, int glyph, bool clickable)
        {
            this.Label     = label;
            this.Glyph     = glyph;
            this.Clickable = clickable;
            this.Cursor    = clickable ? Cursors.Hand : Cursors.Default;
            this.Font      = Theme.Ui(8.8f);
            this.TabStop   = false;
            this.Height    = 26;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  if (Clickable) { Anim.Add(this); } Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Anim.Add(this); Invalidate(); base.OnMouseLeave(e); }

        public bool AnimStep()
        {
            _fade = Anim.Approach(_fade, (_hover && Clickable) ? 1f : 0f, 0.22f);
            return _fade != ((_hover && Clickable) ? 1f : 0f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_fade > 0.02f)
            {
                Draw.Fill(g, new Rectangle(0, Theme.Px(1), this.Width, this.Height - Theme.Px(2)),
                          Draw.Alpha(Theme.Hover, _fade), Theme.Px(6));
            }

            int x = Theme.Px(6);
            if (this.Glyph != Glyphs.None)
            {
                int gsz = Theme.Px(14);
                Glyphs.Render(g, this.Glyph, new Rectangle(x, (this.Height - gsz) / 2, gsz, gsz),
                              Clickable ? Draw.Mix(Theme.SubText, Theme.AccentText, _fade) : Theme.SubText, 1.5f);
                x += gsz + Theme.Px(8);
            }

            int labelW = Theme.Px(84);
            Rectangle lr = new Rectangle(x, 0, labelW, this.Height);
            Draw.TextClip(g, this.Label, this.Font, Theme.SubText, lr, ContentAlignment.MiddleLeft);

            int arrowW = Clickable ? Theme.Px(14) : 0;
            Rectangle vr = new Rectangle(x + labelW, 0,
                                         this.Width - x - labelW - arrowW - Theme.Px(6), this.Height);
            Color vc = Clickable ? Draw.Mix(Theme.Text, Theme.AccentText, 0.35f + 0.65f * _fade) : Theme.Text;
            Draw.TextClip(g, this.Value, this.Font, vc, vr, ContentAlignment.MiddleLeft);

            if (Clickable)
            {
                int gsz = Theme.Px(12);
                Rectangle ar = new Rectangle(this.Width - gsz - Theme.Px(4), (this.Height - gsz) / 2, gsz, gsz);
                Glyphs.Render(g, Glyphs.ChevronRight, ar, Draw.Alpha(Theme.AccentText, 0.35f + 0.65f * _fade), 1.5f);
            }
        }
    }

    // ==========================================================================
    //  圆角卡片：柔和填充 + 极淡描边 + 标题行（可带图案）
    // ==========================================================================
    internal class Card : Panel, IAnim
    {
        public string Title = "";
        public int    TitleHeight = 36;
        public int    Glyph;
        public bool   Flat;          // true = 无描边（用于内嵌小卡）

        private int _radius = 12;
        public int Radius { get { return Theme.Px(_radius); } set { _radius = value; } }

        private float _flash;        // 0..1：被点击跳转命中时闪一圈强调色描边

        public Card(string title)
        {
            this.Title     = title;
            this.Padding   = new Padding(0);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }

        public void Flash()
        {
            _flash = 1f;
            Anim.Add(this);
            Invalidate();
        }

        public bool AnimStep()
        {
            _flash = Anim.Approach(_flash, 0f, 0.045f);
            return _flash > 0f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            Draw.Fill(g, r, Theme.Card, Radius);
            if (!Flat) { Draw.Stroke(g, r, Theme.BorderSoft, Radius, 1f); }

            // 高亮：外圈强调色由粗到细地退掉，用作「跳转到这里了」的提示
            if (_flash > 0.02f)
            {
                Rectangle fr = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
                Draw.Stroke(g, fr, Draw.Alpha(Theme.Accent, 0.75f * Anim.Ease(_flash)), Radius,
                            1f + 2f * Anim.Ease(_flash));
            }

            if (this.Title != null && this.Title.Length > 0)
            {
                int x = Theme.Px(14);
                int th = Theme.Px(TitleHeight);
                if (this.Glyph != Glyphs.None)
                {
                    int gsz = Theme.Px(15);
                    Glyphs.Render(g, this.Glyph,
                                  new Rectangle(x, (th - gsz) / 2, gsz, gsz),
                                  Theme.AccentText, 1.6f);
                    x += gsz + Theme.Px(9);
                }
                Rectangle tr = new Rectangle(x, Theme.Px(2), this.Width - x - Theme.Px(60), th - Theme.Px(4));
                Draw.TextClip(g, this.Title, Theme.Ui(9.5f, FontStyle.Bold), Theme.Text, tr,
                              ContentAlignment.MiddleLeft);
            }
        }
    }

    // ------------------------------------------------------------------ 细滚动条容器
    // 只做竖向滚动：右侧画一条 4px 宽的细圆角滑块（悬停加深、可拖动），
    // 子控件直接放进来、靠改 Top 实现滚动（WinForms 会按客户区自动裁剪子窗口）。
    internal class ScrollArea : Panel, IAnim
    {
        private readonly Dictionary<Control, int> _tops = new Dictionary<Control, int>();
        private int _off;
        private int _max;
        private int _content;
        private bool _hover;
        private bool _dragging;
        private int _dragY;
        private int _dragOff;
        private float _fade;

        private const int WM_MOUSEWHEEL = 0x020A;

        public ScrollArea()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.BackColor = Theme.Card;
        }

        private int BarW { get { return Theme.Px(6); } }

        public bool HasBar { get { return _max > 0; } }

        public void ClearRows()
        {
            this.Controls.Clear();
            _tops.Clear();
            _off   = 0;
            _max   = 0;
            _fade  = 0f;
            this.Invalidate();
        }

        public void AddRow(Control c, int top)
        {
            _tops[c] = top;
            c.Top    = top - _off;
            this.Controls.Add(c);
        }

        // 卡片这类「一张就是一大块」的子控件是先 Controls.Add 再登记位置的：
        // 不登记的话 Apply() 不会移动它，滚动时内容一动不动（踩过）。
        public void Register(Control c, int top)
        {
            _tops[c] = top;
            c.Top    = top - _off;
        }

        // 内容总高（不受滚动影响）
        public void SetContent(int contentHeight)
        {
            int old = _max;
            _content = contentHeight;
            _max = Math.Max(0, contentHeight - this.Height);
            if (_off > _max) { _off = _max; }
            if (_off < 0) { _off = 0; }
            if (_max != old) { Anim.Add(this); }
            Apply();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int old = _max;
            _max = Math.Max(0, _content - this.Height);
            if (_off > _max) { _off = _max; }
            if (_max != old) { Anim.Add(this); }
        }

        // 【必须】ScaleLayout 把整窗 bounds 放大后，这份「内容坐标系」也要同步放大：
        // 行控件的位置已经被放大成物理坐标，而 _tops 还是逻辑坐标，
        // 一旦滚动就会用逻辑值去摆物理行 → 行全叠在一起（用户反馈的「滑动后文字重叠」）。
        public void ScaleContent(float s)
        {
            if (s <= 0f || Math.Abs(s - 1f) < 0.0001f) { return; }
            List<Control> keys = new List<Control>(_tops.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                _tops[keys[i]] = (int)Math.Round(_tops[keys[i]] * s);
            }
            _content = (int)Math.Round(_content * s);
            _off     = (int)Math.Round(_off * s);
            _max     = Math.Max(0, _content - this.Height);
            if (_off > _max) { _off = _max; }
            if (_off < 0)    { _off = 0; }
            Apply();
        }

        public void ScrollBy(int dy, bool animate)
        {
            int v = _off + dy;
            if (v < 0) { v = 0; }
            if (v > _max) { v = _max; }
            if (v == _off) { return; }
            _off = v;
            Apply();
            if (animate) { Anim.Add(this); }
        }

        private void Apply()
        {
            foreach (Control c in this.Controls)
            {
                int t;
                if (_tops.TryGetValue(c, out t)) { c.Top = t - _off; }
            }
            this.Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((long)m.WParam >> 16);
                this.ScrollBy(-Math.Sign(delta) * Theme.Px(76), true);
                return;
            }
            base.WndProc(ref m);
        }

        private Rectangle Track()
        {
            return new Rectangle(this.Width - BarW - Theme.Px(2), Theme.Px(4),
                                 BarW, this.Height - Theme.Px(8));
        }

        private Rectangle Thumb()
        {
            Rectangle tr = Track();
            if (_max <= 0) { return Rectangle.Empty; }
            int h = Math.Max(Theme.Px(28), (int)((float)tr.Height * this.Height / (this.Height + _max)));
            int y = tr.Y;
            if (_max > 0)
            {
                int span = tr.Height - h;
                y += (int)Math.Round((double)span * _off / _max);
            }
            return new Rectangle(tr.X, y, tr.Width, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!HasBar) { return; }
            if (e.X >= this.Width - BarW - Theme.Px(6))
            {
                _dragging = true;
                _dragY    = e.Y;
                _dragOff  = _off;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool h = HasBar && e.X >= this.Width - BarW - Theme.Px(6);
            if (h != _hover) { _hover = h; Anim.Add(this); }

            this.Cursor = h ? Cursors.Hand : Cursors.Default;

            if (_dragging)
            {
                Rectangle tr = Track();
                int hh = Thumb().Height;
                int span = Math.Max(1, tr.Height - hh);
                int dy = e.Y - _dragY;
                int v = _dragOff + (int)Math.Round((double)dy * _max / span);
                if (v < 0) { v = 0; }
                if (v > _max) { v = _max; }
                _off = v;
                Apply();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover) { _hover = false; Anim.Add(this); }
        }

        public bool AnimStep()
        {
            float target = (_hover || _dragging) ? 1f : 0f;
            if (_max <= 0) { target = 0f; }
            _fade = Anim.Approach(_fade, target, 0.12f);
            this.Invalidate(new Rectangle(this.Width - Theme.Px(12), 0, Theme.Px(12), this.Height));
            return Math.Abs(_fade - target) > 0.02f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(this.BackColor)) { g.FillRectangle(b, 0, 0, this.Width, this.Height); }

            if (_max <= 0) { return; }

            Rectangle th = Thumb();
            float a = 0.35f + 0.5f * Anim.Ease(_fade);
            Draw.Fill(g, th, Draw.Alpha(Theme.SubText, a), th.Width / 2);
        }
    }

    // ==========================================================================
    //  托盘右键菜单：WinForms 默认菜单是系统浅灰样式，跟自绘界面完全不搭，
    //  深色主题下尤其突兀。这两段把菜单的底色/描边/悬停块/文字色全部接管，
    //  颜色全部从 Theme 现取，所以切主题后菜单也跟着变。
    // ==========================================================================
    internal sealed class TrayMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground   { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin      { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle     { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd        { get { return Theme.Card; } }
        public override Color MenuBorder                    { get { return Theme.Border; } }
        public override Color MenuItemBorder                { get { return Color.Transparent; } }
        public override Color MenuItemSelected              { get { return Theme.AccentSoft; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.AccentSoft; } }
        public override Color MenuItemSelectedGradientEnd   { get { return Theme.AccentSoft; } }
        public override Color MenuItemPressedGradientBegin  { get { return Theme.AccentSoft; } }
        public override Color MenuItemPressedGradientEnd    { get { return Theme.AccentSoft; } }
        public override Color SeparatorDark                 { get { return Theme.Border; } }
        public override Color SeparatorLight                { get { return Theme.Border; } }
    }

    internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayMenuColors())
        {
            this.RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Card)) { e.Graphics.FillRectangle(b, e.AffectedBounds); }
        }

        // 系统那圈是纯黑硬边，换成主题描边色
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (Pen p = new Pen(Theme.Border, 1f)) { e.Graphics.DrawRectangle(p, r); }
        }

        // 不要左侧那条图标留白带（菜单项都是纯文字）
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) { return; }
            Rectangle r = new Rectangle(Theme.Px(4), 1, e.Item.Width - Theme.Px(8), e.Item.Height - 2);
            Draw.Fill(e.Graphics, r, Theme.AccentSoft, Theme.Px(6));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // 条目自己指定了 ForeColor（比如「退出面板」的危险色）就用它，别被统一配色盖掉
            if (e.Item.ForeColor != Theme.Text && e.Item.ForeColor != Color.Empty)
            {
                e.TextColor = e.Item.Enabled ? e.Item.ForeColor : Theme.SubText;
                base.OnRenderItemText(e);
                return;
            }
            e.TextColor = !e.Item.Enabled ? Theme.SubText
                        : (e.Item.Selected ? Theme.AccentText : Theme.Text);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Border, 1f))
            {
                e.Graphics.DrawLine(p, Theme.Px(10), y, e.Item.Width - Theme.Px(10), y);
            }
        }
    }

    // ==========================================================================
    //  滚轮路由：WM_MOUSEWHEEL 只会投递给「有焦点」的窗口，而 ScrollArea 是 Panel，
    //  永远拿不到焦点 —— 所以滚轮压根到不了它手里（面板上滚不动的原因）。
    //  这里在消息循环层截一道，光标停在滚动区上就转给它。
    // ==========================================================================
    internal sealed class WheelRouter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        public ScrollArea Target;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) { return false; }

            ScrollArea sa = this.Target;
            if (sa == null || !sa.IsHandleCreated || sa.IsDisposed || !sa.Visible) { return false; }

            Point p = sa.PointToClient(Control.MousePosition);
            if (p.X < 0 || p.Y < 0 || p.X >= sa.Width || p.Y >= sa.Height) { return false; }

            int delta = (short)((long)m.WParam >> 16);
            if (delta == 0) { return false; }

            sa.ScrollBy(-Math.Sign(delta) * Theme.Px(76), true);
            return true;
        }
    }

    // ==========================================================================
    //  抓窗口位图。注意：Control.DrawToBitmap 对「自绘 UserPaint 控件 + 子控件」的
    //  组合抓不全（试过：只抓到卡片描边，文字和填充全丢），所以老老实实用 Win32
    //  的 PrintWindow(PW_RENDERFULLCONTENT) —— 跟截图验证走的是同一条路，抓到什么
    //  看到的就是什么。
    // ==========================================================================
    internal static class Shot
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        // 注意：PW_RENDERFULLCONTENT(2) 走的是 DWM 的合成缓存 —— 一个「刚显示、还没被
        // 合成过」的子窗口抓出来是父窗口上一帧的画面（实测抓到的是上一个页面）。
        // 所以这里用 flags=0 的老路：给窗口发 WM_PRINT，让 GDI 老老实实现画一遍，
        // 子控件当场重绘，抓到的必然是当前内容。
        public static Bitmap Window(Control c)
        {
            if (c == null || c.Width <= 0 || c.Height <= 0 || !c.IsHandleCreated) { return null; }
            Bitmap bmp = new Bitmap(c.Width, c.Height);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(c.Handle, hdc, 0); }
                    finally { g.ReleaseHdc(hdc); }
                }
                return bmp;
            }
            catch { bmp.Dispose(); return null; }
        }
    }

    // ==========================================================================
    //  切页交叉溶解舞台。
    //  WinForms 的子控件没有 Opacity，过去靠「逐帧挪动真实页面」既慢（每帧要搬几十个
    //  子窗口）又只能位移、根本没有渐变。现在改成：切页瞬间把旧页/新页各抓一张位图
    //  （Control.DrawToBitmap），把真实页面藏起来，用一个铺满的舞台控件按 elapsed
    //  时间做交叉溶解 —— 旧页向下淡出，新页从下方升回原位并淡入。
    //  混合用 GDI 的 AlphaBlend（比 GDI+ ColorMatrix 快一个量级），200% 缩放也不掉帧。
    // ==========================================================================
    internal sealed class FadeStage : Control, IAnim
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        private const byte AC_SRC_OVER = 0x00;

        [DllImport("msimg32.dll", SetLastError = true)]
        private static extern bool AlphaBlend(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                              IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc,
                                              BLENDFUNCTION bf);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);

        public event EventHandler Done;

        private IntPtr _hFrom, _hTo, _dcFrom, _dcTo, _oldFrom, _oldTo;
        private float  _t;          // 新页进度（0..1）
        private float  _tl;         // 旧页进度（0..1）
        private float  _distIn, _distOut;
        private bool   _running;
        private int    _durMs  = 260;
        private int    _durOut = 190;
        private readonly Stopwatch _sw = new Stopwatch();

        public FadeStage()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Theme.Bg;
            this.Visible   = false;
        }

        // 先把「旧页」端上来铺满（此时画面跟切页前一模一样，肉眼无跳变）
        // 传进来的 Bitmap 只是「临时素材」：这里 GetHbitmap 拷一份 GDI 位图，
        // 托管位图归调用方所有（PanelUi 的页面快照缓存要复用），这里绝不 Dispose。
        public void Prepare(Bitmap from)
        {
            Release();
            this.Visible = true;
            this.BringToFront();
            if (from != null)
            {
                _hFrom   = from.GetHbitmap(Color.FromArgb(0));
                _dcFrom  = CreateCompatibleDC(IntPtr.Zero);
                _oldFrom = SelectObject(_dcFrom, _hFrom);
            }
            _t = 0f;
            Invalidate();
        }

        // 再挂上「新页」并开始溶解（同上，不 Dispose 调用方的位图）
        //
        // 参数照抄 Steamcommunity 302 的 page-slide 规范（从它的 Wails 前端 bundle 里扒出来的）：
        //   .page-slide-enter-active  animation: page-enter .26s cubic-bezier(.22,1,.36,1) both
        //   .page-slide-leave-active  animation: page-leave .19s cubic-bezier(.55,.06,.68,.19) both
        //   @keyframes page-enter  from { opacity:0; transform: translateY(14px) } to { opacity:1; transform:none }
        //   @keyframes page-leave  from { opacity:1; transform:none }          to { opacity:0; transform: translateY(-10px) }
        // 要点①：新页从下方 14px 升上来（260ms、强缓出），旧页往上飘 10px 并淡出（190ms、缓入）。
        // 要点②：旧页是**压在新页上面**那层，而且它自己盖着底色 —— 所以全程不会出现两张页面
        //        各 50% 叠在一起那种「字压字」的糊状，肉眼看到的是「旧页退场、新页在底下已经成型」。
        public void Begin(Bitmap to, int durIn, int durOut, float distIn, float distOut)
        {
            if (to != null)
            {
                _hTo   = to.GetHbitmap(Color.FromArgb(0));
                _dcTo  = CreateCompatibleDC(IntPtr.Zero);
                _oldTo = SelectObject(_dcTo, _hTo);
            }
            _durMs   = durIn > 0 ? durIn : 260;
            _durOut  = durOut > 0 ? durOut : 190;
            _distIn  = distIn;
            _distOut = distOut;
            _t       = 0f;
            _tl      = 0f;
            _running = true;
            _sw.Reset();
            _sw.Start();
            Anim.Add(this);
            Invalidate();
        }

        public bool AnimStep()
        {
            if (!_running) { return false; }
            long ms = _sw.ElapsedMilliseconds;
            float ti = _durMs  <= 0 ? 1f : (float)ms / _durMs;
            float tl = _durOut <= 0 ? 1f : (float)ms / _durOut;
            if (ti > 1f) { ti = 1f; }
            if (tl > 1f) { tl = 1f; }
            _t  = ti;
            _tl = tl;
            if (ti >= 1f) { EndFade(); return false; }
            return true;    // 由 Anim.Step 负责 Invalidate
        }

        // 立刻收尾（用户连点导航时用）
        public void FinishNow()
        {
            if (_running) { EndFade(); }
        }

        private void EndFade()
        {
            _running = false;
            _sw.Stop();
            // 顺序很重要：先让外面把新页显示出来并 Refresh 画好（此时舞台还盖在上面，
            // 最后一帧画的就是新页本身），再撤舞台 —— 反过来的话会闪一帧「内容区全空」。
            EventHandler h = Done;
            if (h != null) { h(this, EventArgs.Empty); }
            Release();
            this.Visible = false;
        }

        private void Release()
        {
            if (_dcFrom != IntPtr.Zero)
            {
                if (_oldFrom != IntPtr.Zero) { SelectObject(_dcFrom, _oldFrom); _oldFrom = IntPtr.Zero; }
                DeleteDC(_dcFrom);
                _dcFrom = IntPtr.Zero;
            }
            if (_dcTo != IntPtr.Zero)
            {
                if (_oldTo != IntPtr.Zero) { SelectObject(_dcTo, _oldTo); _oldTo = IntPtr.Zero; }
                DeleteDC(_dcTo);
                _dcTo = IntPtr.Zero;
            }
            if (_hFrom != IntPtr.Zero) { DeleteObject(_hFrom); _hFrom = IntPtr.Zero; }
            if (_hTo   != IntPtr.Zero) { DeleteObject(_hTo);   _hTo   = IntPtr.Zero; }
        }

        protected override void Dispose(bool disposing)
        {
            _running = false;
            Release();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Bg)) { g.FillRectangle(b, this.ClientRectangle); }
            if (_dcFrom == IntPtr.Zero && _dcTo == IntPtr.Zero) { return; }

            float te = Anim.EaseOutQuart(_t);      // 新页：cubic-bezier(.22,1,.36,1)
            float tl = Anim.EaseInCubic(_tl);      // 旧页：cubic-bezier(.55,.06,.68,.19)
            int   w  = this.Width;
            int   h  = this.Height;
            IntPtr hdc = g.GetHdc();
            try
            {
                // ① 先铺「新页」：从下方 distIn 处升到原位，同时淡入
                if (_dcTo != IntPtr.Zero)
                {
                    int a = (int)Math.Round(255f * te);
                    if (a > 0)
                    {
                        BLENDFUNCTION bf = new BLENDFUNCTION();
                        bf.BlendOp             = AC_SRC_OVER;
                        bf.SourceConstantAlpha = (byte)(a > 255 ? 255 : a);
                        int dy = (int)Math.Round(_distIn * (1f - te));
                        AlphaBlend(hdc, 0, dy, w, h, _dcTo, 0, 0, w, h, bf);
                    }
                }
                // ② 再把「旧页」压在上面：向上 distOut 漂走并淡出（它 190ms 就先退完了）
                if (_dcFrom != IntPtr.Zero)
                {
                    int a = (int)Math.Round(255f * (1f - tl));
                    if (a > 0)
                    {
                        BLENDFUNCTION bf = new BLENDFUNCTION();
                        bf.BlendOp             = AC_SRC_OVER;
                        bf.SourceConstantAlpha = (byte)(a > 255 ? 255 : a);
                        int dy = -(int)Math.Round(_distOut * tl);
                        AlphaBlend(hdc, 0, dy, w, h, _dcFrom, 0, 0, w, h, bf);
                    }
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
    }
}
