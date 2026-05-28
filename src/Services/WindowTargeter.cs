using Velto.Models;
using Velto.Win32;

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
        return new Target(root, ShouldActivate: root != foreground);
    }

    public static void PrepareForExecution(Target target)
    {
        if (!target.ShouldActivate || target.Hwnd == IntPtr.Zero)
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
            }
            if (targetThread != ourThread && targetThread != foregroundThread && targetThread != 0)
            {
                attachedToTarget = NativeMethods.AttachThreadInput(ourThread, targetThread, true);
            }

            NativeMethods.SetForegroundWindow(target.Hwnd);
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
}
