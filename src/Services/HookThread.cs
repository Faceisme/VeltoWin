using Velto.Win32;

namespace Velto.Services;

/// <summary>
/// 独占的"输入线程"。
///
/// 在 WPF UI 线程上装 <c>WH_MOUSE_LL</c> 的最大问题:
///   1. 钩子回调跟 UI dispatch 抢同一根线程。设置窗口做一次布局,你的鼠标就卡一帧。
///   2. WH_MOUSE_LL 有系统级超时(注册表 LowLevelHooksTimeout),回调慢系统会**静默卸掉**钩子。
///   3. SendInput 合成的回放事件要重入钩子,UI 线程忙时回放被推迟,右键单击→上下文菜单的延迟肉眼可见。
///
/// 解决方案:专门起一根优先级稍高的后台线程,在那儿装钩子 + 跑自己的 Win32 消息泵。
/// 鼠标事件全程在这根线程上处理,完全不跟 UI 抢资源。
/// 跟 macOS 版用独立 EventTap 线程的思路一致。
///
/// 跨线程访问 UI(轨迹覆盖层)统一走 <c>Dispatcher.BeginInvoke</c>,在 <see cref="GestureEngine"/> 里完成。
/// </summary>
public sealed class HookThread : IDisposable
{
    // 看门狗:WH_MOUSE_LL 回调超时(LowLevelHooksTimeout)会被系统**静默**卸载,没有任何通知,
    // 之后 Velto 看起来还在跑、手势却全失效。低频自检 + 自动重装,把"重启程序才能恢复"变成自愈。
    private const uint HookHealthCheckMessage = NativeMethods.WM_APP + 0x100;
    private const int WatchdogIntervalMs = 30_000;

    private Thread? _thread;
    private uint _threadId;
    private MouseHook? _hook;
    private Func<MouseEvent, bool>? _handler;
    private readonly ManualResetEventSlim _started = new(false);
    private Exception? _startupException;
    private System.Threading.Timer? _watchdog;
    private NativeMethods.POINT _watchdogCursor;
    private bool _watchdogHasCursor;

    public void Start(Func<MouseEvent, bool> handler)
    {
        if (_thread is not null) throw new InvalidOperationException("HookThread 已启动");
        _handler = handler;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Velto.InputHook",
        };
        _thread.Start();
        _started.Wait();

        if (_startupException is not null)
        {
            throw new InvalidOperationException("HookThread 启动失败", _startupException);
        }

        // 体检逻辑必须跑在钩子线程上(SetWindowsHookEx 与安装线程关联,重装只能在那儿做),
        // Timer 只负责按周期投递消息唤醒消息泵。
        _watchdog = new System.Threading.Timer(
            _ => NativeMethods.PostThreadMessageW(_threadId, HookHealthCheckMessage, IntPtr.Zero, IntPtr.Zero),
            null, WatchdogIntervalMs, WatchdogIntervalMs);
    }

    public void Stop()
    {
        if (_thread is null) return;
        _watchdog?.Dispose();
        _watchdog = null;
        // PostThreadMessage(WM_QUIT) 让消息泵自然退出 —— 比 Thread.Interrupt/Abort 干净。
        NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Pump()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = new MouseHook { OnEvent = _handler };
            _hook.Install();
        }
        catch (Exception ex)
        {
            _startupException = ex;
            _started.Set();
            return;
        }

        _started.Set();

        // GetMessage 阻塞等下一条消息。返回 0 = WM_QUIT。
        // WH_MOUSE_LL 的回调由 GetMessage/DispatchMessage 派发 —— 没有消息泵的话钩子根本不会回调。
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == HookHealthCheckMessage)
            {
                CheckHookHealth();
                continue;
            }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        _hook?.Uninstall();
        _hook = null;
    }

    /// <summary>
    /// 在钩子线程上执行。判定标准:两次体检之间光标动过,但钩子回调在这整段时间里
    /// 一次都没被调过(任何鼠标移动都会进 LL 钩子回调)→ 钩子已被系统卸载,重装。
    /// </summary>
    private void CheckHookHealth()
    {
        if (_hook is null) return;

        var hasCursor = NativeMethods.GetCursorPos(out var cursor);
        var moved = _watchdogHasCursor && hasCursor &&
                    (cursor.X != _watchdogCursor.X || cursor.Y != _watchdogCursor.Y);
        if (hasCursor)
        {
            _watchdogCursor = cursor;
            _watchdogHasCursor = true;
        }
        if (!moved) return;

        var silentMs = Environment.TickCount64 - _hook.LastCallbackTick;
        if (silentMs < WatchdogIntervalMs) return;

        Logger.Warn($"鼠标钩子疑似被系统静默卸载(光标已移动但 {silentMs}ms 无回调),重新安装");
        try
        {
            _hook.Reinstall();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "HookThread 钩子重装");
        }
    }
}
