using System.Runtime.InteropServices;
using Velto.Win32;

namespace Velto.Services;

/// <summary>
/// 全局低层鼠标钩子 (WH_MOUSE_LL)。
///
/// 钩子回调跑在安装钩子的线程上,且必须有消息循环 —— 我们在 WPF App 启动后安装,
/// 由主 UI 线程消息泵驱动。回调里不要做重活,只把事件转给 <see cref="GestureEngine"/>。
///
/// 钩子返回值的语义:
///   0 → 让事件继续传给系统(默认 CallNextHookEx 的结果)
///   ≠0 → 吞掉事件
///
/// 我们在手势进行中要吞掉右键按下/拖动/松开,以阻止系统右键菜单弹出。
/// "纯右键单击没拖动"的场景,我们也先吞,然后用 SendInput 合成一次完整的右键单击回放。
/// </summary>
public sealed class MouseHook : IDisposable
{
    private NativeMethods.LowLevelMouseProc? _proc;
    private IntPtr _hookHandle;

    /// <summary>
    /// 收到鼠标事件时调,返回 <c>true</c> 表示吞掉该事件。
    /// </summary>
    public Func<MouseEvent, bool>? OnEvent { get; set; }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _proc = HookCallback; // 必须留引用,否则会被 GC 当成可回收
        var hMod = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Logger.Error($"SetWindowsHookEx(WH_MOUSE_LL) 失败,Win32 Error = {err}");
            throw new InvalidOperationException($"安装鼠标钩子失败,Win32 Error = {err}");
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
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

        // 跳过自己合成的事件,避免递归/无限循环
        if (data.dwExtraInfo == NativeMethods.SyntheticEventMarker)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var msg = wParam.ToInt32();
        var kind = msg switch
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

        var consumed = OnEvent?.Invoke(new MouseEvent(kind, data.pt.X, data.pt.Y)) ?? false;
        if (consumed)
        {
            return (IntPtr)1;
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
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
