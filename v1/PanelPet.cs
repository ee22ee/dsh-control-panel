using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DshPanel
{
    // ------------------------------------------------------------------
    // 桌宠（dsh-pet）对接
    //
    // 桌宠是独立的 Electron 进程：启动后把 port / token / baseUrl 写进
    //   <桌宠目录>\.state\api.json
    // 面板读这个文件拿入口，再用本机的 HTTP 接口读写设置、发动作。
    //
    // 为什么全走 C#：面板的内页跑在 https://panel.local 这个虚拟域下，
    // 网页里直接 fetch http://127.0.0.1:390xx 会同时撞上混合内容拦截和 CORS，
    // 所以请求一律由这里代发，结果用 post 推给页面（{t:'pet'|'petSchema'|...}）。
    // ------------------------------------------------------------------
    public partial class PanelForm
    {
        private string _petBase  = "";      // http://127.0.0.1:39080/pet/v1
        private string _petToken = "";      // 写接口要带 x-pet-token
        private string _petVer   = "";
        private string _petDir   = "";
        private bool   _petUp    = false;   // 桌宠在跑
        private bool   _petBusy  = false;   // 防止轮询重入
        private System.Windows.Forms.Timer _petWatch;   // 用 WinForms 的 Timer，Tick 回到 UI 线程

        /// <summary>开始每 2 秒探测一次桌宠（api.json 在不在、ping 通不通）。</summary>
        internal void PetStartWatch()
        {
            if (_petWatch != null) { return; }
            _petWatch = new System.Windows.Forms.Timer();
            _petWatch.Interval = 2000;
            _petWatch.Tick += delegate { PetPoll(); };
            _petWatch.Start();
            PetPoll();
        }

        internal void PetStopWatch()
        {
            if (_petWatch == null) { return; }
            try { _petWatch.Stop(); _petWatch.Dispose(); } catch { }
            _petWatch = null;
        }

        /// <summary>桌宠当前是否在跑（网页据此决定显示/隐藏整张卡片）。</summary>
        internal bool PetRunning { get { return _petUp; } }

        // ---------------- 探测桌宠目录 ----------------

        /// <summary>
        /// 配置里填了 petdir 就用它（容忍填到 .state 或 api.json），
        /// 否则按 %LOCALAPPDATA%\dsh-pet、再从 exe 目录向上找 dsh-pet。
        /// 找不到返回空字符串 = 这台机器上没装桌宠。
        /// </summary>
        private string PetResolveDir()
        {
            string raw = _cfg == null ? "" : _cfg.PetDir;
            if (!string.IsNullOrEmpty(raw))
            {
                string p = raw.Trim().TrimEnd('\\', '/');
                if (Directory.Exists(p))
                {
                    // 允许直接指到 .state 目录
                    if (string.Equals(Path.GetFileName(p), ".state", StringComparison.OrdinalIgnoreCase))
                    {
                        DirectoryInfo up = Directory.GetParent(p);
                        if (up != null) { return up.FullName; }
                    }
                    return p;
                }
                if (File.Exists(p))          // 甚至允许直接指到 api.json
                {
                    DirectoryInfo st = Directory.GetParent(p);
                    if (st != null)
                    {
                        DirectoryInfo up = st.Parent;
                        if (up != null) { return up.FullName; }
                    }
                }
            }

            List<string> cand = new List<string>();
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(la)) { cand.Add(Path.Combine(la, "dsh-pet")); }
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
                {
                    cand.Add(Path.Combine(dir, "dsh-pet"));
                    DirectoryInfo p = Directory.GetParent(dir);
                    dir = p == null ? "" : p.FullName;
                }
            }
            catch { }

            foreach (string c in cand)
            {
                if (File.Exists(Path.Combine(Path.Combine(c, ".state"), "api.json"))) { return c; }
            }
            return "";
        }

        // ---------------- 轮询 ----------------

        private void PetPoll()
        {
            if (_petBusy) { return; }
            _petBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { PetPollCore(); }
                catch { }
                finally { _petBusy = false; }
            });
        }

        private void PetPollCore()
        {
            string dir = PetResolveDir();
            string api = string.IsNullOrEmpty(dir) ? "" : Path.Combine(Path.Combine(dir, ".state"), "api.json");
            string baseUrl = "";
            string token = "";
            string ver = "";
            bool up = false;

            if (File.Exists(api))
            {
                try
                {
                    string js = File.ReadAllText(api, Encoding.UTF8);
                    baseUrl = PetStr(js, "baseUrl");
                    token   = PetStr(js, "token");
                    int port = PetInt(js, "port");
                    if (string.IsNullOrEmpty(baseUrl) && port > 0)
                    {
                        baseUrl = "http://127.0.0.1:" + port + "/pet/v1";
                    }
                    if (!string.IsNullOrEmpty(baseUrl))
                    {
                        baseUrl = baseUrl.TrimEnd('/');
                        string ping = PetHttp("GET", baseUrl + "/ping", "", null, 2500);
                        if (ping != null && ping.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0)
                        {
                            up = true;
                            ver = PetStr(ping, "version");
                        }
                    }
                }
                catch { }
            }

            bool came = up && (!_petUp || baseUrl != _petBase);
            bool went = !up && _petUp;

            _petBase  = baseUrl;
            _petToken = token;
            _petUp    = up;
            _petVer   = ver;
            _petDir   = dir;

            if (went && Host != null)
            {
                Host.Post("{\"t\":\"pet\",\"running\":false,\"cfgdir\":\"" + Esc(_cfg.PetDir ?? "") + "\"}");
            }
            if (came && Host != null)
            {
                Host.Post("{\"t\":\"pet\",\"running\":true,\"version\":\"" + Esc(ver) +
                          "\",\"dir\":\"" + Esc(dir) +
                          "\",\"cfgdir\":\"" + Esc(_cfg.PetDir ?? "") + "\"}");
                PetPushSchemaAndConfig();
            }
        }

        /// <summary>把 schema / 当前配置 / 运行状态一起推给页面（原样转发桌宠的 JSON）。</summary>
        internal void PetPushSchemaAndConfig()
        {
            if (!_petUp || Host == null) { return; }
            string sch = PetHttp("GET", _petBase + "/schema", "", null, 4000);
            string cfg = PetHttp("GET", _petBase + "/config", "", null, 4000);
            string st  = PetHttp("GET", _petBase + "/state",  "", null, 4000);
            if (sch != null) { Host.Post("{\"t\":\"petSchema\",\"raw\":" + sch + "}"); }
            if (cfg != null) { Host.Post("{\"t\":\"petConfig\",\"raw\":" + cfg + "}"); }
            if (st  != null) { Host.Post("{\"t\":\"petState\",\"raw\":" + st  + "}"); }
        }

        // ---------------- 写操作（都放后台线程，避免卡住界面） ----------------

        /// <summary>改设置。patchJson 是页面传来的「只含改动键」的 JSON 对象。</summary>
        internal void PetPatch(string patchJson)
        {
            if (!_petUp || string.IsNullOrEmpty(patchJson)) { return; }
            string base0 = _petBase;
            string token = _petToken;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string r = PetHttp("PATCH", base0 + "/config", token, patchJson, 6000);
                    if (Host != null)
                    {
                        Host.Post("{\"t\":\"petPatched\",\"raw\":" + (r == null ? "null" : r) + "}");
                    }
                    PetPushSchemaAndConfig();      // 数值可能被钳制，回读真实值
                }
                catch { }                          // 线程池里漏异常 = 整个面板退出
            });
        }

        /// <summary>发一个动作：show / hide / toggle-visible / reset-position / reload / quit …</summary>
        internal void PetCommand(string action)
        {
            if (!_petUp || string.IsNullOrEmpty(action)) { return; }
            string base0 = _petBase;
            string token = _petToken;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string body = "{\"action\":\"" + Esc(action) + "\"}";
                    string r = PetHttp("POST", base0 + "/command", token, body, 6000);
                    if (Host != null)
                    {
                        Host.Post("{\"t\":\"petAction\",\"action\":\"" + Esc(action) +
                                  "\",\"raw\":" + (r == null ? "null" : r) + "}");
                    }
                }
                catch { }
                PetPoll();                     // 状态可能变了，立刻刷一次
            });
        }

        /// <summary>把桌宠设置恢复默认。</summary>
        internal void PetResetConfig()
        {
            if (!_petUp) { return; }
            string base0 = _petBase;
            string token = _petToken;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string r = PetHttp("POST", base0 + "/config/reset", token, "{}", 6000);
                    if (Host != null)
                    {
                        Host.Post("{\"t\":\"petPatched\",\"raw\":" + (r == null ? "null" : r) + "}");
                    }
                    PetPushSchemaAndConfig();
                }
                catch { }
            });
        }

        /// <summary>在设置页里改桌宠目录（换一台机器、或者桌宠装在别处时用）。</summary>
        internal void PetPickDir()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择 dsh-pet 的目录（里面应当有 .state 子目录）";
                if (Directory.Exists(_petDir)) { d.SelectedPath = _petDir; }
                if (d.ShowDialog(this) != DialogResult.OK) { return; }
                _cfg.PetDir = d.SelectedPath;
                _cfg.Save();
                _petUp = false;                // 强制重新探测并按新目录推一次
                LogEvent("桌宠目录已设为 " + _cfg.PetDir);
                PetPoll();
                PushPetHintToWeb();
            }
        }

        /// <summary>页面加载完时通知它「这台机器上有没有桌宠」。</summary>
        internal void PushPetHintToWeb()
        {
            if (Host == null) { return; }
            Host.Post("{\"t\":\"pet\",\"running\":" + (_petUp ? "true" : "false") +
                      (_petUp ? ",\"version\":\"" + Esc(_petVer) + "\",\"dir\":\"" + Esc(_petDir) + "\"" : "") +
                      ",\"cfgdir\":\"" + Esc(_cfg.PetDir ?? "") + "\"}");
            if (_petUp) { PetPushSchemaAndConfig(); }
        }

        // ---------------- HTTP + JSON 小工具 ----------------

        /// <summary>同步 HTTP。返回响应正文；连不上返回 null。</summary>
        private static string PetHttp(string method, string url, string token, string body, int timeoutMs)
        {
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = method;
                r.Timeout = timeoutMs;
                r.ReadWriteTimeout = timeoutMs;
                r.KeepAlive = false;
                r.AllowAutoRedirect = false;
                r.Proxy = null;                        // 本机回环，别走系统代理
                if (!string.IsNullOrEmpty(token)) { r.Headers["x-pet-token"] = token; }
                if (body != null)
                {
                    byte[] b = Encoding.UTF8.GetBytes(body);
                    r.ContentType = "application/json; charset=utf-8";
                    r.ContentLength = b.Length;
                    using (Stream s = r.GetRequestStream()) { s.Write(b, 0, b.Length); }
                }
                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                // 4xx/5xx 的正文里通常也带 {ok:false,error}，照样取回来
                try
                {
                    if (we.Response != null)
                    {
                        using (Stream s = we.Response.GetResponseStream())
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
                catch { }
                return null;
            }
            catch { return null; }
        }

        private static string PetStr(string js, string key)
        {
            if (string.IsNullOrEmpty(js)) { return ""; }
            Match m = Regex.Match(js, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }

        private static int PetInt(string js, string key)
        {
            if (string.IsNullOrEmpty(js)) { return 0; }
            Match m = Regex.Match(js, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+)");
            int v;
            return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : 0;
        }

        /// <summary>从 JSON 里取出某个键的原始文本（对象/数组/字符串/字面量都行）。</summary>
        internal static string PetRawValue(string js, string key)
        {
            if (string.IsNullOrEmpty(js)) { return ""; }
            int i = js.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) { return ""; }
            int c = js.IndexOf(':', i + key.Length + 2);
            if (c < 0) { return ""; }
            int s = c + 1;
            while (s < js.Length && char.IsWhiteSpace(js[s])) { s++; }
            if (s >= js.Length) { return ""; }

            char first = js[s];
            if (first == '{' || first == '[')
            {
                char open = first;
                char close = first == '{' ? '}' : ']';
                int depth = 0;
                bool inStr = false, esc = false;
                for (int k = s; k < js.Length; k++)
                {
                    char ch = js[k];
                    if (inStr)
                    {
                        if (esc) { esc = false; }
                        else if (ch == '\\') { esc = true; }
                        else if (ch == '"') { inStr = false; }
                        continue;
                    }
                    if (ch == '"') { inStr = true; continue; }
                    if (ch == open) { depth++; }
                    else if (ch == close)
                    {
                        depth--;
                        if (depth == 0) { return js.Substring(s, k - s + 1); }
                    }
                }
                return "";
            }
            if (first == '"')
            {
                bool esc = false;
                for (int k = s + 1; k < js.Length; k++)
                {
                    char ch = js[k];
                    if (esc) { esc = false; continue; }
                    if (ch == '\\') { esc = true; continue; }
                    if (ch == '"') { return js.Substring(s, k - s + 1); }
                }
                return "";
            }
            int e = s;
            while (e < js.Length && js[e] != ',' && js[e] != '}' && js[e] != ']') { e++; }
            return js.Substring(s, e - s).Trim();
        }
    }
}
