using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Velto.Services;

/// <summary>
/// 在"当前用户的开始菜单 / 程序"目录里放一个快捷方式,
/// 这样即便托盘图标被隐藏,也能在开始菜单搜索 "Velto" 把程序(设置)找回来。
/// 用 Windows Script Host(WScript.Shell)COM 写 .lnk,无需额外引用、无需管理员权限。
/// </summary>
public static class StartMenuShortcut
{
    // 文件名即开始菜单里显示/可搜索的名字。
    private const string ShortcutFileName = "Velto 手势.lnk";

    public static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            ShortcutFileName);

    /// <summary>
    /// 确保快捷方式存在且指向当前 exe。每次启动调用,幂等;失败只记日志不影响运行。
    /// </summary>
    public static void Ensure()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            Create(ShortcutPath, exe);
            Logger.Info($"开始菜单快捷方式已就绪: {ShortcutPath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "StartMenuShortcut.Ensure");
        }
    }

    /// <summary>从开始菜单移除快捷方式(预留:将来做"取消固定"开关时用)。</summary>
    public static void Remove()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "StartMenuShortcut.Remove");
        }
    }

    private static void Create(string lnkPath, string targetExe)
    {
        var dir = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM 组件不可用");
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) throw new InvalidOperationException("无法创建 WScript.Shell 实例");
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? "";
            shortcut.IconLocation = targetExe + ",0";
            shortcut.Description = "Velto 鼠标手势";
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
