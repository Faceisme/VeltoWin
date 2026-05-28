using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Velto.Services;

/// <summary>
/// 开机自启 —— 用 Win11 推荐的 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 注册表项。
///
/// 不用任务计划程序(Task Scheduler):那需要管理员或第一次创建任务的提权,personal tool 用不上。
/// 不用启动文件夹快捷方式:.lnk 维护麻烦,且 Win11 已把启动文件夹边缘化。
/// HKCU\Run 是最干净的:登录时由 Explorer 启动,跟随用户,不需要任何特权。
///
/// 路径里塞的是当前 exe 全路径。如果你把 Velto 拷到别的目录,需要重新勾选一次开关
/// (registry 里旧路径会指向不存在的文件,Explorer 会静默忽略 —— 不至于报错)。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Velto";

    /// <summary>当前是否启用了自启 —— 注册表里有这个值,且指向的 exe 还真实存在。</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                if (key?.GetValue(ValueName) is string raw)
                {
                    var path = raw.Trim('"');
                    return File.Exists(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "AutoStart.IsEnabled");
            }
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                Logger.Warn("AutoStart: 打不开 HKCU\\Run 子键");
                return;
            }

            if (enabled)
            {
                var path = GetExecutablePath();
                if (path is null)
                {
                    Logger.Warn("AutoStart: 拿不到当前 exe 路径");
                    return;
                }
                // 带引号 —— 路径里有空格(Program Files 等)时必须
                key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
                Logger.Info($"AutoStart enabled → {path}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("AutoStart disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AutoStart.SetEnabled");
        }
    }

    private static string? GetExecutablePath()
    {
        // .NET 6+ 的 Environment.ProcessPath 是首选 —— 直接给 native exe 路径
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }
        // 兜底:旧 API
        try { return Process.GetCurrentProcess().MainModule?.FileName; }
        catch { return null; }
    }
}
