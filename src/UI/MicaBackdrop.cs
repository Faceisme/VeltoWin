using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Velto.Services;
using Velto.Win32;

namespace Velto.UI;

/// <summary>
/// 给 WPF 窗口套上 Win11 Mica 背景 —— 对齐 macOS 版的 Liquid Glass 视觉。
///
/// WPF 不原生支持 Mica,需要走 DWM API:
///   1. <c>DwmExtendFrameIntoClientArea(-1,-1,-1,-1)</c>:把"非客户区"扩到整个窗口,
///      让 DWM 在整个区域应用 backdrop 材质。
///   2. <c>DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW)</c>:开 Mica。
///   3. WPF 的 <c>Window.Background</c> 必须是 <c>Transparent</c>,否则 WPF 自己填颜色就盖住 Mica。
///   4. <c>AllowsTransparency</c> 不能开 —— 开了 WPF 走软件渲染路径,DWM 合成被绕过,Mica 失效。
///
/// 调用方:在窗口的 <c>SourceInitialized</c> 事件里调一次。失败时静默(老 Win 不支持),
/// 窗口退化为正常实色背景。
/// </summary>
public static class MicaBackdrop
{
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Logger.Warn("MicaBackdrop.Apply: hwnd 还没生成,跳过");
            return;
        }

        // 让 WPF 不要填背景,空出来给 DWM 画 Mica
        window.Background = Brushes.Transparent;

        var margins = new NativeMethods.MARGINS
        {
            cxLeftWidth = -1, cxRightWidth = -1,
            cyTopHeight = -1, cyBottomHeight = -1,
        };
        var hr1 = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (hr1 != 0)
        {
            Logger.Warn($"DwmExtendFrameIntoClientArea HRESULT=0x{hr1:X8}");
        }

        var backdropType = NativeMethods.DWMSBT_MAINWINDOW;
        var hr2 = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
            ref backdropType, sizeof(int));
        if (hr2 != 0)
        {
            Logger.Warn($"DwmSetWindowAttribute(SYSTEMBACKDROP_TYPE) HRESULT=0x{hr2:X8} —— 系统可能不支持 Mica");
        }

        // 跟随系统主题:深色模式时让标题栏也变深
        var darkMode = IsSystemUsingDarkTheme() ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
            ref darkMode, sizeof(int));
    }

    private static bool IsSystemUsingDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = light, 0 = dark
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* 拿不到当 light */ }
        return false;
    }
}
