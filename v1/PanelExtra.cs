using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshPanel
{
    // ==========================================================================
    //  开机自启：写 HKCU 的 Run 键。
    //  用 HKCU 而不是 HKLM，是为了不触发 UAC —— 面板自己就能开关，不需要管理员。
    // ==========================================================================
    internal static class AutoStart
    {
        private const string RunKey    = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DshPanel";

        public static bool IsOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) { return false; }
                    object v = k.GetValue(ValueName);
                    return (v != null) && (v.ToString().Length > 0);
                }
            }
            catch { return false; }
        }

        public static bool Set(bool on, out string error)
        {
            error = "";
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) { error = "打不开注册表启动项。"; return false; }

                    if (on)
                    {
                        string exe = Application.ExecutablePath;
                        k.SetValue(ValueName, "\"" + exe + "\"");
                    }
                    else
                    {
                        if (k.GetValue(ValueName) != null) { k.DeleteValue(ValueName, false); }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "写入注册表启动项失败：" + ex.Message;
                return false;
            }
        }
    }
}
