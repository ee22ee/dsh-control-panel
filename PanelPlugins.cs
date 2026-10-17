using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DshPanel
{
    // ==========================================================================
    //  一个插件（= 一个 bundle）在面板上的样子
    // ==========================================================================
    internal sealed class PluginItem
    {
        public string Name    = "";
        public string Version = "";
        public bool   Enabled;      // 是否出现在 dsh.profile.bundles 里
        public bool   Protected;    // DSH 命脉，禁止禁用
        public bool   Builtin;      // 不在 dependencies 里的内置 bundle

        // 显示名：去掉 @scope/ 前缀，看着清爽；完整包名留给悬浮提示。
        public string Display
        {
            get
            {
                string n = Name;
                if (n.StartsWith("@"))
                {
                    int slash = n.IndexOf('/');
                    if (slash > 0) { n = n.Substring(slash + 1); }
                }
                if (n.StartsWith("dsh-")) { n = n.Substring(4); }
                return n;
            }
        }
    }

    // ==========================================================================
    //  package.json 的 dsh.profile.bundles 就是这个 profile 的「启用列表」：
    //  从数组里拿掉 = 不加载 = 下次启动 DSH 前生效；dependencies 留着，随时能勾回来。
    //
    //  这里刻意不做整体 JSON 序列化 —— 那会把整个文件的缩进、键序、空行全部重排。
    //  只定位 bundles 数组并原地替换那一段文本，文件其余部分一个字节都不动。
    // ==========================================================================
    internal static class PluginStore
    {
        // 少了任何一个，DSH 都起不来，必须在界面上锁死。
        private static readonly string[] ProtectedNames = new string[] {
            "@deepseek-ai/dsh-base",
            "@deepseek-ai/dsh-web-app",
            "@deepseek-ai/dsh-web-app-client"
        };

        public static bool IsProtected(string name)
        {
            for (int i = 0; i < ProtectedNames.Length; i++)
            {
                if (string.Equals(ProtectedNames[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // ---------- 读 ----------

        public static bool Load(string path, List<PluginItem> into, out string error)
        {
            error = "";
            into.Clear();
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) { error = "读取 package.json 失败：" + ex.Message; return false; }
            return Parse(text, into, out error);
        }

        // 纯文本解析：既给 Load 用，也给 Save 写完之后的校验用。
        public static bool Parse(string text, List<PluginItem> into, out string error)
        {
            error = "";
            into.Clear();

            int depStart = FindKeyBlock(text, "dependencies", '{');
            int depEnd   = (depStart < 0) ? -1 : FindBlockEnd(text, depStart, '{', '}');
            if (depStart < 0 || depEnd <= depStart)
            {
                error = "package.json 里找不到 dependencies 块。";
                return false;
            }

            string depBody = text.Substring(depStart + 1, depEnd - depStart - 1);
            MatchCollection depMs = Regex.Matches(depBody, "\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\"");
            for (int i = 0; i < depMs.Count; i++)
            {
                PluginItem it = new PluginItem();
                it.Name    = depMs[i].Groups[1].Value;
                it.Version = depMs[i].Groups[2].Value;
                into.Add(it);
            }

            int bStart = FindKeyBlock(text, "bundles", '[');
            int bEnd   = (bStart < 0) ? -1 : FindBlockEnd(text, bStart, '[', ']');
            if (bStart < 0 || bEnd <= bStart)
            {
                error = "package.json 里找不到 dsh.profile.bundles 数组。";
                return false;
            }

            List<string> enabled = new List<string>();
            string bBody = text.Substring(bStart + 1, bEnd - bStart - 1);
            MatchCollection bMs = Regex.Matches(bBody, "\"([^\"]+)\"");
            for (int i = 0; i < bMs.Count; i++) { enabled.Add(bMs[i].Groups[1].Value); }

            for (int i = 0; i < into.Count; i++)
            {
                into[i].Enabled   = Contains(enabled, into[i].Name);
                into[i].Protected = IsProtected(into[i].Name);
            }

            // bundles 里有、dependencies 里没有的，是随 DSH 一起装的内置 bundle。
            for (int i = 0; i < enabled.Count; i++)
            {
                bool known = false;
                for (int j = 0; j < into.Count; j++)
                {
                    if (string.Equals(into[j].Name, enabled[i], StringComparison.OrdinalIgnoreCase))
                    {
                        known = true;
                        break;
                    }
                }
                if (known) { continue; }

                PluginItem it = new PluginItem();
                it.Name      = enabled[i];
                it.Version   = "内置";
                it.Enabled   = true;
                it.Builtin   = true;
                it.Protected = IsProtected(enabled[i]);
                into.Add(it);
            }

            return true;
        }

        // ---------- 写 ----------

        public static bool Save(string path, List<PluginItem> items, out string error)
        {
            error = "";
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) { error = "读取 package.json 失败：" + ex.Message; return false; }

            int bStart = FindKeyBlock(text, "bundles", '[');
            int bEnd   = (bStart < 0) ? -1 : FindBlockEnd(text, bStart, '[', ']');
            if (bStart < 0 || bEnd <= bStart)
            {
                error = "package.json 里找不到 dsh.profile.bundles 数组，未做任何修改。";
                return false;
            }

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string itemIndent  = IndentOfFirstItem(text, bStart, bEnd, nl);
            string closeIndent = IndentOfClosingBracket(text, bStart, nl);

            List<string> on = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Enabled) { on.Add(items[i].Name); }
            }
            if (on.Count == 0)
            {
                error = "至少要保留一个插件，否则 DSH 无法启动。";
                return false;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < on.Count; i++)
            {
                sb.Append(nl).Append(itemIndent).Append('"').Append(on[i]).Append('"');
                if (i < on.Count - 1) { sb.Append(','); }
            }
            sb.Append(nl).Append(closeIndent).Append(']');

            string newText = text.Substring(0, bStart) + sb.ToString() + text.Substring(bEnd + 1);

            // 写完必须先自证：新文本仍能被解析，且开关状态与预期完全一致。
            List<PluginItem> verify = new List<PluginItem>();
            string verr;
            if (!Parse(newText, verify, out verr))
            {
                error = "新内容校验失败（未写入）：" + verr;
                return false;
            }
            for (int i = 0; i < items.Count; i++)
            {
                bool found = false;
                for (int j = 0; j < verify.Count; j++)
                {
                    if (string.Equals(verify[j].Name, items[i].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        if (verify[j].Enabled != items[i].Enabled)
                        {
                            error = "新内容校验失败：插件 " + items[i].Name + " 的开关状态没写对（未写入）。";
                            return false;
                        }
                        break;
                    }
                }
                if (!found)
                {
                    error = "新内容校验失败：插件 " + items[i].Name + " 在写回后消失了（未写入）。";
                    return false;
                }
            }

            // 先备份再落盘：万一 DSH 起不来，把 .bak 覆盖回去即可。
            try
            {
                File.Copy(path, path + ".bak", true);
            }
            catch { /* 备份失败不阻塞写入，但下面会如实记录 */ }

            try
            {
                File.WriteAllText(path, newText, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                error = "写入 package.json 失败：" + ex.Message;
                return false;
            }

            return true;
        }

        // ---------- 内部工具 ----------

        private static bool Contains(List<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        // 找到 "key" 之后的第一个 open 字符
        private static int FindKeyBlock(string s, string key, char open)
        {
            int k = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) { return -1; }
            int i = s.IndexOf(open, k + key.Length + 2);
            return i;
        }

        // 从 start（open 所在位置）配对到对应的 close，跳过字符串字面量
        private static int FindBlockEnd(string s, int start, char open, char close)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"')  { inStr = false; }
                    continue;
                }
                if (c == '"')        { inStr = true; continue; }
                if (c == open)       { depth++; }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) { return i; }
                }
            }
            return -1;
        }

        // 取数组里第一个元素所在行的缩进
        private static string IndentOfFirstItem(string text, int bStart, int bEnd, string nl)
        {
            int i = text.IndexOf(nl, bStart);
            if (i < 0 || i > bEnd) { return "        "; }
            int j = i + nl.Length;
            StringBuilder sb = new StringBuilder();
            while (j < text.Length && (text[j] == ' ' || text[j] == '\t'))
            {
                sb.Append(text[j]);
                j++;
            }
            string s = sb.ToString();
            return (s.Length == 0) ? "        " : s;
        }

        // 取数组闭合 ] 所在行的缩进
        private static string IndentOfClosingBracket(string text, int bStart, string nl)
        {
            int lineStart = text.LastIndexOf(nl, bStart);
            if (lineStart < 0) { return "      "; }
            StringBuilder sb = new StringBuilder();
            int j = lineStart + nl.Length;
            while (j < text.Length && (text[j] == ' ' || text[j] == '\t'))
            {
                sb.Append(text[j]);
                j++;
            }
            string s = sb.ToString();
            if (s.Length == 0) { return "      "; }
            return s;
        }
    }
}
