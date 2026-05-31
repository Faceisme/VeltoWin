using System.Runtime.InteropServices;
using Velto.Win32;

namespace Velto.Services;

public sealed class MouseHook : IDisposable
{
    private NativeMethods.LowLevelMouseProc? _proc;
    private IntPtr _hookHandle;

    private static readonly int ExtraInfoOffset =
        (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.dwExtraInfo));

    private long _lastErrorLogTicks;
    private bool _rightButtonTracking;

    public Func<MouseEvent, bool>? OnEvent { get; set; }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _proc = HookCallback;
        var hMod = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Logger.Error($"SetWindowsHookEx(WH_MOUSE_LL) failed, Win32 Error = {err}");
            throw new InvalidOperationException($"Failed to install mouse hook, Win32 Error = {err}");
        }
    }

    public void Uninstall()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _proc = null;
        _rightButtonTracking = false;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var kind = MouseEventKind.Other;
        try
        {
            if (nCode < 0)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            var message = wParam.ToInt32();
            if (message == NativeMethods.WM_MOUSEMOVE && !_rightButtonTracking)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            kind = message switch
            {
                NativeMethods.WM_RBUTTONDOWN => MouseEventKind.RightButtonDown,
                NativeMethods.WM_RBUTTONUP   => MouseEventKind.RightButtonUp,
                NativeMethods.WM_MOUSEMOVE   => MouseEventKind.MouseMove,
                _ => MouseEventKind.Other,
            };
            if (kind == MouseEventKind.Other)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            if (Marshal.ReadIntPtr(lParam, ExtraInfoOffset) == NativeMethods.SyntheticEventMarker)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            var x = Marshal.ReadInt32(lParam, 0);
            var y = Marshal.ReadInt32(lParam, 4);

            if (kind == MouseEventKind.RightButtonDown)
            {
                _rightButtonTracking = true;
            }

            bool consumed;
            try
            {
                consumed = OnEvent?.Invoke(new MouseEvent(kind, x, y)) ?? false;
            }
            finally
            {
                if (kind == MouseEventKind.RightButtonUp)
                {
                    _rightButtonTracking = false;
                }
            }

            return consumed ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            if (kind is MouseEventKind.RightButtonDown or MouseEventKind.RightButtonUp)
            {
                _rightButtonTracking = false;
            }
            LogCallbackErrorThrottled(ex);
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }
    }

    private void LogCallbackErrorThrottled(Exception ex)
    {
        var now = Environment.TickCount64;
        if (now - _lastErrorLogTicks < 5000) return;
        _lastErrorLogTicks = now;
        Logger.Error(ex, "MouseHook.HookCallback");
    }
}

public enum MouseEventKind
{
    RightButtonDown,
    RightButtonUp,
    MouseMove,
    Other,
}

public readonly record struct MouseEvent(MouseEventKind Kind, int X, int Y);
