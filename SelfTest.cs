using System.Text;

namespace DshPanel2;

/// <summary>
/// 内置自检：验证纯逻辑（INI 解析、插件文本手术、端口探活）。
///
/// 为什么不用 xunit：本机 NuGet 只能走本地源（schannel 问题），拉不到测试框架。
/// 与其依赖外部包，不如把纯函数本身写成可断言的形式，用 <c>DshPanel2.exe --selftest</c> 跑。
/// 这也是重写的主要收益之一 —— 旧版的这些逻辑与 WinForms 控件纠缠，根本无法单独测。
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _failed;

    internal static int Run()
    {
        Log.Clear();
        _failed = 0;

        TestIniParse();
        TestIniSetKey();
        TestIniTruthy();
        TestPluginParse();
        TestPluginRejectsAllOff();
        TestPluginSaveKeepsFormatting();
        TestProbePort();

        Log.AppendLine();
        Log.AppendLine(_failed == 0 ? "==== 全部通过 ====" : $"==== 失败 {_failed} 项 ====");

        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "selftest.log"),
                Log.ToString(), new UTF8Encoding(false));
        }
        catch { }

        return _failed == 0 ? 0 : 1;
    }

    // ==================== 断言 ====================

    private static void Check(string name, bool ok, string? detail = null)
    {
        Log.AppendLine(ok ? $"  PASS  {name}" : $"  FAIL  {name}{(detail is null ? "" : "  → " + detail)}");
        if (!ok) { _failed++; }
    }

    private static void Eq(string name, string expected, string actual) =>
        Check(name, expected == actual, $"期望 [{expected}] 实际 [{actual}]");

    // ==================== INI ====================

    private static void TestIniParse()
    {
        Log.AppendLine("[IniText.Parse]");

        var lines = new[]
        {
            "# 注释行",
            "; 另一种注释",
            "",
            "port = 3080",
            "  dshhome   =   C:\\Users\\me\\.dsh  ",
            "title = \"带 空格 的值\"",
            "nokey",
            "= 没有键",
            "autoopen = 1",
        };

        var dict = IniText.Parse(lines).ToDictionary(x => x.Key, x => x.Value);

        Eq("普通键", "3080", dict.GetValueOrDefault("port", ""));
        Eq("前后空白被裁", "C:\\Users\\me\\.dsh", dict.GetValueOrDefault("dshhome", ""));
        Eq("值两端引号被剥", "带 空格 的值", dict.GetValueOrDefault("title", ""));
        Check("跳过无等号行", !dict.ContainsKey("nokey"));
        Check("跳过空键", !dict.ContainsKey(""));
        Eq("布尔键", "1", dict.GetValueOrDefault("autoopen", ""));
    }

    private static void TestIniSetKey()
    {
        Log.AppendLine("[IniText.SetKey]");

        var lines = new List<string>
        {
            "# 保留的注释",
            "port = 3080",
            "title = 旧标题",
        };

        IniText.SetKey(lines, "port", "4090");
        Eq("就地替换已有键", "port = 4090", lines[1]);
        Eq("注释未被破坏", "# 保留的注释", lines[0]);

        IniText.SetKey(lines, "newkey", "hello");
        Eq("新键追加到末尾", "newkey = hello", lines[^1]);
        Check("原有行数 +1", lines.Count == 4, lines.Count.ToString());

        // 大小写不敏感
        IniText.SetKey(lines, "PORT", "5000");
        Eq("键名大小写不敏感", "port = 5000", lines[1]);
    }

    private static void TestIniTruthy()
    {
        Log.AppendLine("[IniText.Truthy]");
        Check("1 为真", IniText.Truthy("1"));
        Check("true 为真", IniText.Truthy("true"));
        Check("YES 为真", IniText.Truthy("YES"));
        Check("on 为真", IniText.Truthy("on"));
        Check("是 为真", IniText.Truthy("是"));
        Check("0 为假", !IniText.Truthy("0"));
        Check("空串为假", !IniText.Truthy(""));
        Check("乱写为假", !IniText.Truthy("maybe"));
    }

    // ==================== 插件文本手术 ====================

    private const string SampleJson = """
{
  "name": "web-profile",
  "dependencies": {
    "@deepseek-ai/dsh-base": "0.2.1",
    "@scope/plugin-a": "1.0.0",
    "@scope/plugin-b": "2.0.0"
  },
  "dsh": {
    "profile": {
      "bundles": [
        "@deepseek-ai/dsh-base",
        "@scope/plugin-a",
        "@scope/plugin-b"
      ]
    }
  }
}
""";

    private static void TestPluginParse()
    {
        Log.AppendLine("[PluginStore.Parse]");

        var items = new List<PluginItem>();
        var ok = PluginStore.Parse(SampleJson, items, out var err);

        Check("解析成功", ok, err);
        Check("依赖数 3", items.Count == 3, items.Count.ToString());
        Check("全部启用", items.All(i => i.Enabled));
        Check("base 受保护", items.First(i => i.Name == "@deepseek-ai/dsh-base").Protected);
        Eq("显示名去 scope 与 dsh- 前缀", "plugin-a",
            items.First(i => i.Name == "@scope/plugin-a").Display);
    }

    private static void TestPluginRejectsAllOff()
    {
        Log.AppendLine("[PluginStore.Save 防线]");

        // 用 exe 同级目录而不是 %TEMP%：沙箱内 %TEMP% 也可能不可写
        var dir = Path.Combine(AppContext.BaseDirectory, "selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "package.json");
        File.WriteAllText(file, SampleJson, new UTF8Encoding(false));

        try
        {
            var items = new List<PluginItem>();
            PluginStore.Parse(SampleJson, items, out _);

            // 1) 全部关掉 → 至少要留一个
            foreach (var i in items) { i.Enabled = false; }
            Check("全关被拒绝", !PluginStore.Save(file, items, out var e1), e1);

            // 2) 关掉命脉插件 → 拒绝
            PluginStore.Parse(SampleJson, items, out _);
            var baseItem = items.First(i => i.Name == "@deepseek-ai/dsh-base");
            baseItem.Enabled = false;
            Check("禁用命脉被拒绝", !PluginStore.Save(file, items, out var e2), e2);
            Check("被拒绝时文件未改动", File.ReadAllText(file) == SampleJson);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestPluginSaveKeepsFormatting()
    {
        Log.AppendLine("[PluginStore.Save 保格式]");

        // 用 exe 同级目录而不是 %TEMP%：沙箱内 %TEMP% 也可能不可写
        var dir = Path.Combine(AppContext.BaseDirectory, "selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "package.json");
        File.WriteAllText(file, SampleJson, new UTF8Encoding(false));

        try
        {
            var items = new List<PluginItem>();
            PluginStore.Parse(SampleJson, items, out _);

            // 关掉 plugin-a
            items.First(i => i.Name == "@scope/plugin-a").Enabled = false;

            var ok = PluginStore.Save(file, items, out var err);
            Check("保存成功", ok, err);

            var after = File.ReadAllText(file);

            // 只检查 bundles 数组段：dependencies 里的 plugin-a 本来就应该留着
            var bStart = after.IndexOf("\"bundles\"", StringComparison.Ordinal);
            var bEnd = bStart < 0 ? -1 : after.IndexOf(']', bStart);
            var bundlesText = (bStart >= 0 && bEnd > bStart) ? after[bStart..bEnd] : "";

            Check("bundles 数组里不再有 plugin-a", !bundlesText.Contains("@scope/plugin-a"), bundlesText);
            Check("bundles 数组里仍有 plugin-b", bundlesText.Contains("@scope/plugin-b"));
            Check("dependencies 里的 plugin-a 仍在", after.Contains("\"@scope/plugin-a\": \"1.0.0\""));
            Check("其它字段未被重排", after.Contains("\"name\": \"web-profile\""));

            // 再解析一次，确认开关状态正确
            var re = new List<PluginItem>();
            PluginStore.Parse(after, re, out _);
            Check("回读：plugin-a 已关",
                !re.First(i => i.Name == "@scope/plugin-a").Enabled);
            Check("回读：plugin-b 仍开",
                re.First(i => i.Name == "@scope/plugin-b").Enabled);

            Check("生成了 .bak", File.Exists(file + ".bak"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ==================== 端口探活 ====================

    private static void TestProbePort()
    {
        Log.AppendLine("[ServiceController.ProbePort]");

        // 三个状态里至少确认「查询不会抛」且返回值在合法域内
        var p = ServiceController.ProbePort(9);
        Check("未监听端口返回 0 或 -1", p is 0 or -1, p.ToString());

        // 用当前进程自己的监听端口做正例（如果拿不到就跳过）
        var listening = System.Net.NetworkInformation.IPGlobalProperties
            .GetIPGlobalProperties().GetActiveTcpListeners();

        if (listening.Length > 0)
        {
            var port = listening[0].Port;
            var pr = ServiceController.ProbePort(port);
            Check($"正在监听的端口 {port} 返回 1", pr == 1, pr.ToString());
        }
        else
        {
            Log.AppendLine("  SKIP  本机没有监听端口可做正例");
        }
    }
}
