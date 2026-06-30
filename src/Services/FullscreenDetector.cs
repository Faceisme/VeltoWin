using System.Runtime.InteropServices;
using System.Text;
using Velto.Win32;

namespace Velto.Services;

/// <summary>
/// 判定"前台窗口是不是一个全屏应用(游戏 / 全屏视频 / 演示)"。
///
/// 判定法:前台窗口的矩形是否盖满它所在那块显示器的<b>整块</b>矩形(<c>rcMonitor</c>,含任务栏区)。
///   - 最大化的普通窗口只铺<b>工作区</b>(<c>rcWork</c>),会露出任务栏 → 不算全屏。
///   - 独占全屏 / 无边框全屏 / 全屏视频会盖住整块显示器 → 算全屏。
/// 这条"对比整块显示器矩形"的启发式能同时覆盖独占与无边框两种主流全屏方式,
/// 且天然把"最大化"和"全屏"区分开。多显示器下用 <see cref="NativeMethods.MonitorFromWindow"/>
/// 取前台窗口实际所在的那块屏。
///
/// 桌面本身(Progman / WorkerW)和任务栏(Shell_TrayWnd…)也铺满屏幕,但不是"应用全屏",显式排除。
/// </summary>
public static class FullscreenDetector
{
    /// <summary>读前台窗口的实时状态判断是否全屏。任何异常 / 拿不到信息都按"非全屏"处理(宁可不暂停)。</summary>
    public static bool IsForegroundFullscreen()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (NativeMethods.IsIconic(hwnd)) return false;

        if (IsShellWindow(GetClassName(hwnd))) return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var window)) return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;

        return CoversMonitor(window, info.rcMonitor);
    }

    /// <summary>
    /// 纯几何判定:窗口矩形是否盖满显示器整块矩形。用 ≤ / ≥(而非相等)是因为某些全屏游戏
    /// 会把窗口设得比显示器略大几像素。抽成无副作用的静态方法,便于单测。
    /// </summary>
    public static bool CoversMonitor(NativeMethods.RECT window, NativeMethods.RECT monitor)
        => window.Left <= monitor.Left
        && window.Top <= monitor.Top
        && window.Right >= monitor.Right
        && window.Bottom >= monitor.Bottom
        && monitor.Right > monitor.Left
        && monitor.Bottom > monitor.Top;

    private static bool IsShellWindow(string className)
        => className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString();
    }
}
