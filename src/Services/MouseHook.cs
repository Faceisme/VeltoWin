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

    // dwExtraInfo 在 MSLLHOOKSTRUCT 里的字节偏移(x64 上是 24)。用 OffsetOf 算,避免硬编码出错。
    private static readonly int ExtraInfoOffset =
        (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.dwExtraInfo));

    // 回调异常日志限流:出问题时不要把日志刷爆(钩子在全系统鼠标移动上跑)。
    private long _lastErrorLogTicks;

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
        // ┌─ R1:整个回调包在 try/catch 里。低层钩子回调里抛出未捕获异常,会被系统
        // │   静默卸掉钩子 —— 手势全废且无任何提示。任何情况下都必须把事件放行,
        // └─  绝不让异常逃逸到内核。
        try
        {
            if (nCode < 0)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // 先用 wParam(消息号)判类型 —— 无关事件(左键/中键/滚轮等)直接放行,
            // 连结构体都不用碰。P2:绝大多数全系统鼠标事件在这里就走掉了。
            var kind = wParam.ToInt32() switch
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

            // P2:只读真正要用的 3 个字段,不再 Marshal 整个 MSLLHOOKSTRUCT。
            // 跳过自己合成的事件(右键回放),避免递归。
            if (Marshal.ReadIntPtr(lParam, ExtraInfoOffset) == NativeMethods.SyntheticEventMarker)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
            var x = Marshal.ReadInt32(lParam, 0); // POINT.pt.x @ offset 0
            var y = Marshal.ReadInt32(lParam, 4); // POINT.pt.y @ offset 4

            var consumed = OnEvent?.Invoke(new MouseEvent(kind, x, y)) ?? false;
            return consumed ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            LogCallbackErrorThrottled(ex);
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }
    }

    private void LogCallbackErrorThrottled(Exception ex)
    {
        var now = Environment.TickCount64;
        // 最多每 5 秒记一次,避免高频事件把日志刷爆
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
