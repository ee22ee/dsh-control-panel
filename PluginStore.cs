using System.Text;
using System.Text.RegularExpressions;

namespace DshPanel2;

/// <summary>一个插件（= 一个 bundle）在面板上的样子。</summary>
internal sealed class PluginItem
{
    public string Name = "";
    public string Version = "";

    /// <summary>是否出现在 dsh.profile.bundles 里（= 启用）。</summary>
    public bool Enabled;

    /// <summary>DSH 命脉，禁止禁用。</summary>
    public bool Protected;

    /// <summary>不在 dependencies 里的内置 bundle（随 DSH 一起装）。</summary>
    public bool Builtin;

    /// <summary>显示名：去掉 @scope/ 前缀和 dsh- 前缀，看着清爽；完整包名留给悬浮提示。</summary>
    public string Display
    {
        get
        {
            var n = Name;
            if (n.StartsWith('@'))
            {
                var slash = n.IndexOf('/');
                if (slash > 0) { n = n[(slash + 1)..]; }
            }
            if (n.StartsWith("dsh-")) { n = n[4..]; }
            return n;
        }
    }
}

/// <summary>
/// package.json 的 dsh.profile.bundles 就是这个 profile 的「启用列表」：
/// 从数组里拿掉 = 不加载 = 下次启动 DSH 前生效；dependencies 留着，随时能勾回来。
///
/// 刻意不做整体 JSON 序列化 —— 那会把整个文件的缩进、键序、空行全部重排。
/// 只定位 bundles 数组并原地替换那一段文本，文件其余部分一个字节都不动。
///
/// 相对旧版的两处加强：
///   1. <see cref="Save"/> 会拒绝把受保护的插件移出数组（旧版只靠界面锁，接口本身没有防线）
///   2. 写入前的自校验保留，并且失败时一个字节都不写
/// </summary>
internal static class PluginStore
{
    /// <summary>少了任何一个 DSH 都起不来，必须在界面上锁死。</summary>
    private static readonly string[] ProtectedNames =
    [
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
        "@deepseek-ai/dsh-web-app-client",
    ];

    internal static bool IsProtected(string name) =>
        ProtectedNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    // ==================== 读 ====================

    internal static bool Load(string path, List<PluginItem> into, out string error)
    {
        error = "";
        into.Clear();

        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex)
        {
            error = "读取 package.json 失败：" + ex.Message;
            return false;
        }

        return Parse(text, into, out error);
    }

    /// <summary>纯文本解析：既给 Load 用，也给 Save 写完之后的校验用。可直接单测。</summary>
    internal static bool Parse(string text, List<PluginItem> into, out string error)
    {
        error = "";
        into.Clear();

        var depStart = FindKeyBlock(text, "dependencies", '{');
        var depEnd = depStart < 0 ? -1 : FindBlockEnd(text, depStart, '{', '}');
        if (depStart < 0 || depEnd <= depStart)
        {
            error = "package.json 里找不到 dependencies 块。";
            return false;
        }

        var depBody = text[(depStart + 1)..depEnd];
        foreach (Match m in Regex.Matches(depBody, @"""([^""]+)""\s*:\s*""([^""]*)"""))
        {
            into.Add(new PluginItem
            {
                Name = m.Groups[1].Value,
                Version = m.Groups[2].Value,
            });
        }

        var bStart = FindKeyBlock(text, "bundles", '[');
        var bEnd = bStart < 0 ? -1 : FindBlockEnd(text, bStart, '[', ']');
        if (bStart < 0 || bEnd <= bStart)
        {
            error = "package.json 里找不到 dsh.profile.bundles 数组。";
            return false;
        }

        var bBody = text[(bStart + 1)..bEnd];
        var enabled = new List<string>();
        foreach (Match m in Regex.Matches(bBody, @"""([^""]+)"""))
        {
            enabled.Add(m.Groups[1].Value);
        }

        foreach (var it in into)
        {
            it.Enabled = Contains(enabled, it.Name);
            it.Protected = IsProtected(it.Name);
        }

        // bundles 里有、dependencies 里没有的，是随 DSH 一起装的内置 bundle
        foreach (var name in enabled)
        {
            if (into.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) { continue; }

            into.Add(new PluginItem
            {
                Name = name,
                Version = "内置",
                Enabled = true,
                Builtin = true,
                Protected = IsProtected(name),
            });
        }

        return true;
    }

    // ==================== 写 ====================

    internal static bool Save(string path, List<PluginItem> items, out string error)
    {
        error = "";

        // 防线 1：受保护的插件不允许被移出数组
        var removedProtected = items
            .Where(i => i.Protected && !i.Enabled)
            .Select(i => i.Name)
            .ToList();
        if (removedProtected.Count > 0)
        {
            error = "以下插件是 DSH 命脉，不能禁用：" + string.Join("、", removedProtected);
            return false;
        }

        var on = items.Where(i => i.Enabled).Select(i => i.Name).ToList();
        if (on.Count == 0)
        {
            error = "至少要保留一个插件，否则 DSH 无法启动。";
            return false;
        }

        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex)
        {
            error = "读取 package.json 失败：" + ex.Message;
            return false;
        }

        var bStart = FindKeyBlock(text, "bundles", '[');
        var bEnd = bStart < 0 ? -1 : FindBlockEnd(text, bStart, '[', ']');
        if (bStart < 0 || bEnd <= bStart)
        {
            error = "package.json 里找不到 dsh.profile.bundles 数组，未做任何修改。";
            return false;
        }

        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var itemIndent = IndentOfFirstItem(text, bStart, bEnd, nl);
        var closeIndent = IndentOfClosingBracket(text, bStart, nl);

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < on.Count; i++)
        {
            sb.Append(nl).Append(itemIndent).Append('"').Append(on[i]).Append('"');
            if (i < on.Count - 1) { sb.Append(','); }
        }
        sb.Append(nl).Append(closeIndent).Append(']');

        var newText = text[..bStart] + sb + text[(bEnd + 1)..];

        // 防线 2：写完必须先自证 —— 新文本仍能被解析，且开关状态与预期完全一致
        var verify = new List<PluginItem>();
        if (!Parse(newText, verify, out var verr))
        {
            error = "新内容校验失败（未写入）：" + verr;
            return false;
        }

        foreach (var item in items)
        {
            var found = verify.FirstOrDefault(v =>
                string.Equals(v.Name, item.Name, StringComparison.OrdinalIgnoreCase));

            if (found is null)
            {
                error = $"新内容校验失败：插件 {item.Name} 在写回后消失了（未写入）。";
                return false;
            }
            if (found.Enabled != item.Enabled)
            {
                error = $"新内容校验失败：插件 {item.Name} 的开关状态没写对（未写入）。";
                return false;
            }
        }

        // 先备份再落盘：万一 DSH 起不来，把 .bak 覆盖回去即可
        try { File.Copy(path, path + ".bak", true); }
        catch { /* 备份失败不阻塞写入 */ }

        try
        {
            File.WriteAllText(path, newText, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            error = "写入 package.json 失败：" + ex.Message;
            return false;
        }
    }

    // ==================== 内部工具 ====================

    private static bool Contains(List<string> list, string name) =>
        list.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>找到 "key" 之后的第一个 open 字符。</summary>
    private static int FindKeyBlock(string s, string key, char open)
    {
        var k = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (k < 0) { return -1; }
        return s.IndexOf(open, k + key.Length + 2);
    }

    /// <summary>从 start（open 所在位置）配对到对应的 close，跳过字符串字面量。</summary>
    private static int FindBlockEnd(string s, int start, char open, char close)
    {
        var depth = 0;
        var inStr = false;

        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];

            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') { inStr = false; }
                continue;
            }

            if (c == '"') { inStr = true; continue; }

            if (c == open) { depth++; }
            else if (c == close)
            {
                depth--;
                if (depth == 0) { return i; }
            }
        }

        return -1;
    }

    /// <summary>取数组里第一个元素所在行的缩进。</summary>
    private static string IndentOfFirstItem(string text, int bStart, int bEnd, string nl)
    {
        var i = text.IndexOf(nl, bStart, StringComparison.Ordinal);
        if (i < 0 || i > bEnd) { return "        "; }

        var j = i + nl.Length;
        var sb = new StringBuilder();
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) { sb.Append(text[j]); j++; }

        var s = sb.ToString();
        return s.Length == 0 ? "        " : s;
    }

    /// <summary>取数组闭合 ] 所在行的缩进。</summary>
    private static string IndentOfClosingBracket(string text, int bStart, string nl)
    {
        var lineStart = text.LastIndexOf(nl, Math.Max(0, bStart), StringComparison.Ordinal);
        if (lineStart < 0) { return "      "; }

        var j = lineStart + nl.Length;
        var sb = new StringBuilder();
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) { sb.Append(text[j]); j++; }

        var s = sb.ToString();
        return s.Length == 0 ? "      " : s;
    }
}
