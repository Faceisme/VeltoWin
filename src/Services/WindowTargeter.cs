using Velto.Models;
using Velto.Win32;
using System.Text;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Velto.Services;

/// <summary>
/// 把"光标下的窗口"提到前台。Windows 上 SendInput 默认会发到前台窗口,
/// 所以两个 policy 的差别就在于"动作开始前要不要切前台"。
///
/// SetForegroundWindow 在跨线程时会被 SPI_SETFOREGROUNDLOCKTIMEOUT 拦截,
/// 标准绕法是 AttachThreadInput 到目标线程,把自己伪装成属于同一输入队列,
/// 调完立刻 detach,以免后续按键被错误投递到对方线程。
/// </summary>
public static class WindowTargeter
{
    public sealed record Target(IntPtr Hwnd, bool ShouldActivate);

    public static Target Resolve(GestureTargetPolicy policy, NativeMethods.POINT mouseDownPoint)
    {
        if (policy == GestureTargetPolicy.ActiveWindow)
        {
            return new Target(NativeMethods.GetForegroundWindow(), ShouldActivate: false);
        }

        var hwnd = NativeMethods.WindowFromPoint(mouseDownPoint);
        if (hwnd == IntPtr.Zero)
        {
            return new Target(NativeMethods.GetForegroundWindow(), ShouldActivate: false);
        }
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = hwnd;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (IsActivationUnsafe(root))
        {
            return new Target(foreground, ShouldActivate: false);
        }

        return new Target(root, ShouldActivate: root != foreground);
    }

    public static void PrepareForExecution(Target target)
    {
        if (!target.ShouldActivate || target.Hwnd == IntPtr.Zero)
        {
            return;
        }
        if (IsActivationUnsafe(target.Hwnd))
        {
            return;
        }

        var foregroundHwnd = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out _);
        var ourThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(target.Hwnd, out _);

        bool attachedToForeground = false;
        bool attachedToTarget = false;
        try
        {
            if (foregroundThread != ourThread && foregroundThread != 0)
            {
                attachedToForeground = NativeMethods.AttachThreadInput(ourThread, foregroundThread, true);
                if (!attachedToForeground)
                {
                    GestureDiagnosticLogger.Info(
                        $"attach_foreground_failed target={FormatHwnd(target.Hwnd)} foreground={FormatHwnd(foregroundHwnd)} error={Marshal.GetLastWin32Error()}");
                }
            }
            if (targetThread != ourThread && targetThread != foregroundThread && targetThread != 0)
            {
                attachedToTarget = NativeMethods.AttachThreadInput(ourThread, targetThread, true);
                if (!attachedToTarget)
                {
                    GestureDiagnosticLogger.Info(
                        $"attach_target_failed target={FormatHwnd(target.Hwnd)} error={Marshal.GetLastWin32Error()}");
                }
            }

            if (!NativeMethods.SetForegroundWindow(target.Hwnd))
            {
                GestureDiagnosticLogger.Info(
                    $"set_foreground_failed target={FormatHwnd(target.Hwnd)} error={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            if (attachedToForeground)
            {
                NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
            }
            if (attachedToTarget)
            {
                NativeMethods.AttachThreadInput(ourThread, targetThread, false);
            }
        }
    }

    public static string Describe(Target target)
        => $"{FormatHwnd(target.Hwnd)} process='{GetProcessName(target.Hwnd)}' class='{GetClassName(target.Hwnd)}' iconic={IsIconic(target.Hwnd)} activate={target.ShouldActivate}";

    public static string GetTargetClassName(Target target)
        => GetClassName(target.Hwnd);

    public static string GetTargetProcessName(Target target)
        => GetProcessName(target.Hwnd);

    private static bool IsActivationUnsafe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return true;
        }

        if (IsIconic(hwnd))
        {
            return true;
        }

        return IsShellClass(GetClassName(hwnd));
    }

    private static bool IsShellClass(string className)
        => className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW"
        || className.Contains("Tray", StringComparison.OrdinalIgnoreCase)
        || className.Contains("TaskList", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Start", StringComparison.OrdinalIgnoreCase);

    private static bool IsIconic(IntPtr hwnd)
        => hwnd != IntPtr.Zero && NativeMethods.IsIconic(hwnd);

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString();
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatHwnd(IntPtr hwnd)
        => "0x" + unchecked((ulong)hwnd.ToInt64()).ToString("X");
}
