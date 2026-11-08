using Microsoft.Win32;

namespace DshPanel2;

/// <summary>
/// 开机自启：写 HKCU 的 Run 键。
///
/// 用 HKCU 而不是 HKLM，是为了不触发 UAC —— 面板自己就能开关，不需要管理员权限。
///
/// 相对旧版的改动（缺陷 i）：旧版定义了 <c>IsOn()</c> 但从未调用，
/// 于是注册表里的实际状态和配置文件里的 <c>autostart</c> 可能长期不一致
/// （比如用别的工具删掉了注册表项、或 exe 挪了位置）。
/// 本版在启动时做一次校正，见 <see cref="SyncFromRegistry"/>。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DshPanel2";

    internal static bool IsOn()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (k is null) { return false; }
            var v = k.GetValue(ValueName);
            return v is not null && v.ToString()!.Length > 0;
        }
        catch { return false; }
    }

    internal static bool Set(bool on, out string error)
    {
        error = "";
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                error = "拿不到当前 exe 路径（Environment.ProcessPath 为空）";
                Program.LogEvent("AutoStart.Set 失败：" + error);
                return false;
            }

            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (k is null)
            {
                error = "打不开注册表启动项。";
                Program.LogEvent("AutoStart.Set 失败：" + error);
                return false;
            }

            if (on)
            {
                // 路径带引号，避免 Program Files 这类含空格的路径被截断
                k.SetValue(ValueName, "\"" + exePath + "\"");
            }
            else
            {
                if (k.GetValue(ValueName) is not null) { k.DeleteValue(ValueName, false); }
            }

            // 回读校验：写成功不代表真的落盘（权限/重定向都可能让 SetValue 静默失败）
            var check = k.GetValue(ValueName)?.ToString() ?? "";
            if (on && check.Length == 0)
            {
                error = "注册表写入后被回读为空，可能被安全策略拦截。";
                Program.LogEvent("AutoStart.Set 失败：" + error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "写入注册表启动项失败：" + ex.Message;
            Program.LogEvent("AutoStart.Set 异常：" + ex);
            return false;
        }
    }

    /// <summary>
    /// 启动时校正：把注册表里的真实状态同步回配置。
    ///
    /// 三种情况：
    ///   配置说开、注册表没有 → 补写注册表（用户可能在别处删掉了）
    ///   配置说开、注册表指向别的 exe → 重写成本 exe（面板被挪过位置）
    ///   配置说关、注册表有 → 删掉（避免用户关了却还在自启）
    /// </summary>
    internal static void SyncFromRegistry(PanelConfig cfg)
    {
        var regOn = IsOn();

        if (cfg.AutoStart && !regOn)
        {
            if (Set(true, out var err)) { Program.LogEvent("启动校正：补写开机自启注册表项"); }
            else { Program.LogEvent("启动校正：补写自启失败 " + err); }
            return;
        }

        if (!cfg.AutoStart && regOn)
        {
            if (Set(false, out var err)) { Program.LogEvent("启动校正：删掉多余的启动项"); }
            else { Program.LogEvent("启动校正：删除启动项失败 " + err); }
            return;
        }

        // 两边都说开：检查注册表指向的是不是当前 exe
        if (cfg.AutoStart && regOn)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var cur = k?.GetValue(ValueName)?.ToString() ?? "";
                var want = "\"" + Environment.ProcessPath + "\"";
                if (!string.Equals(cur, want, StringComparison.OrdinalIgnoreCase))
                {
                    if (Set(true, out _)) { Program.LogEvent("启动校正：自启路径已更新指向当前 exe"); }
                }
            }
            catch (Exception ex)
            {
                Program.LogEvent("启动校正：读取自启路径失败 " + ex.Message);
            }
        }
    }
}
