using System.Diagnostics;
using System.Text;
using Velto.Win32;

namespace Velto.Services;

public sealed class ShellTransitionDiagnostic : IDisposable
{
    private const int SnapshotDelayMs = 80;

    private readonly NativeMethods.WinEventProc _callback;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _minimizeHook;
    private readonly IntPtr _objectHook;

    private long _lastObjectEventTicks;

    public ShellTransitionDiagnostic()
    {
        _callback = OnWinEvent;
        _foregroundHook = SetHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        _minimizeHook = SetHook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
        _objectHook = SetHook(NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_HIDE);

        GestureDiagnosticLogger.Info(
            $"shell_diag_ready foregroundHook={FormatHwnd(_foregroundHook)} " +
            $"minimizeHook={FormatHwnd(_minimizeHook)} objectHook={FormatHwnd(_objectHook)}");
        QueueSnapshot("startup", IntPtr.Zero);
    }

    public void Dispose()
    {
        Unhook(_foregroundHook);
        Unhook(_minimizeHook);
        Unhook(_objectHook);
    }

    private IntPtr SetHook(uint eventMin, uint eventMax)
    {
        var hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (hook == IntPtr.Zero)
        {
            GestureDiagnosticLogger.Info($"shell_diag_hook_failed eventMin=0x{eventMin:X} eventMax=0x{eventMax:X}");
        }

        return hook;
    }

    private static void Unhook(IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(hook);
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        try
        {
            if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0)
            {
                return;
            }

            var eventName = EventName(eventType);
            if (IsObjectEvent(eventType) && !ShouldLogObjectEvent(hwnd))
            {
                return;
            }

            if (IsObjectEvent(eventType) && ShouldThrottleObjectEvent())
            {
                return;
            }

            GestureDiagnosticLogger.Info(
                $"window_event event={eventName} hook={FormatHwnd(hWinEventHook)} hwnd={DescribeWindow(hwnd)} " +
                $"thread={idEventThread} eventTime={dwmsEventTime}");

            if (eventType is NativeMethods.EVENT_SYSTEM_FOREGROUND
                or NativeMethods.EVENT_SYSTEM_MINIMIZESTART
                or NativeMethods.EVENT_SYSTEM_MINIMIZEEND)
            {
                QueueSnapshot(eventName, hwnd);
            }
        }
        catch (Exception ex)
        {
            GestureDiagnosticLogger.Error(ex, "ShellTransitionDiagnostic.OnWinEvent");
        }
    }

    private static bool IsObjectEvent(uint eventType)
        => eventType is >= NativeMethods.EVENT_OBJECT_CREATE and <= NativeMethods.EVENT_OBJECT_HIDE;

    private bool ShouldThrottleObjectEvent()
    {
        var now = Environment.TickCount64;
        if (now - _lastObjectEventTicks < 20)
        {
            return true;
        }

        _lastObjectEventTicks = now;
        return false;
    }

    private static bool ShouldLogObjectEvent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root != IntPtr.Zero && root != hwnd)
        {
            return false;
        }

        var className = GetClassName(hwnd);
        if (IsInterestingClass(className))
        {
            return true;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return Math.Abs(rect.Right - rect.Left) >= 80 && Math.Abs(rect.Bottom - rect.Top) >= 80;
    }

    private static bool IsInterestingClass(string className)
        => className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "MSTaskSwWClass"
        || className.Contains("Tray", StringComparison.OrdinalIgnoreCase)
        || className.Contains("TaskList", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Start", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
        || className.Contains("HwndWrapper[Velto", StringComparison.OrdinalIgnoreCase);

    private static void QueueSnapshot(string reason, IntPtr hwnd)
    {
        LogSnapshot(reason, hwnd, 0);
        Task.Delay(SnapshotDelayMs).ContinueWith(
            _ => LogSnapshot(reason, hwnd, SnapshotDelayMs),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void LogSnapshot(string reason, IntPtr hwnd, int delayMs)
    {
        try
        {
            var cursor = NativeMethods.GetCursorPos(out var pt)
                ? $"({pt.X},{pt.Y})"
                : "(unknown)";
            var underCursor = NativeMethods.GetCursorPos(out pt)
                ? NativeMethods.WindowFromPoint(pt)
                : IntPtr.Zero;

            GestureDiagnosticLogger.Info(
                $"window_snapshot reason={reason} delayMs={delayMs} eventHwnd={DescribeWindow(hwnd)} " +
                $"foreground={DescribeWindow(NativeMethods.GetForegroundWindow())} " +
                $"cursor={cursor} underCursor={DescribeWindow(underCursor)} shell={DescribeShellWindows()}");
        }
        catch (Exception ex)
        {
            GestureDiagnosticLogger.Error(ex, "ShellTransitionDiagnostic.LogSnapshot");
        }
    }

    private static string DescribeShellWindows()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        var workers = new List<string>();
        var worker = IntPtr.Zero;

        while (true)
        {
            worker = NativeMethods.FindWindowExW(IntPtr.Zero, worker, "WorkerW", null);
            if (worker == IntPtr.Zero)
            {
                break;
            }

            workers.Add(DescribeWindow(worker));
            if (workers.Count >= 4)
            {
                break;
            }
        }

        return $"progman={DescribeWindow(progman)} taskbar={DescribeWindow(taskbar)} workerW=[{string.Join("; ", workers)}]";
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "0x0";
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

        return $"{FormatHwnd(hwnd)} class='{GetClassName(hwnd)}' title='{Trim(GetWindowText(hwnd), 80)}' " +
               $"pid={pid} proc='{GetProcessName(pid)}' visible={NativeMethods.IsWindowVisible(hwnd)} " +
               $"iconic={NativeMethods.IsIconic(hwnd)} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) " +
               $"size={rect.Right - rect.Left}x{rect.Bottom - rect.Top} ex=0x{exStyle:X}";
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString();
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString();
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            return pid == 0 ? string.Empty : Process.GetProcessById(unchecked((int)pid)).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Trim(string value, int maxLength)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string EventName(uint eventType)
        => eventType switch
        {
            NativeMethods.EVENT_SYSTEM_FOREGROUND => "foreground",
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART => "minimize_start",
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND => "minimize_end",
            NativeMethods.EVENT_OBJECT_CREATE => "object_create",
            NativeMethods.EVENT_OBJECT_DESTROY => "object_destroy",
            NativeMethods.EVENT_OBJECT_SHOW => "object_show",
            NativeMethods.EVENT_OBJECT_HIDE => "object_hide",
            _ => "0x" + eventType.ToString("X"),
        };

    private static string FormatHwnd(IntPtr hwnd)
        => "0x" + unchecked((ulong)hwnd.ToInt64()).ToString("X");
}
