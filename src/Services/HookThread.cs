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
    private Thread? _thread;
    private uint _threadId;
    private MouseHook? _hook;
    private Func<MouseEvent, bool>? _handler;
    private readonly ManualResetEventSlim _started = new(false);
    private Exception? _startupException;

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
    }

    public void Stop()
    {
        if (_thread is null) return;
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
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        _hook?.Uninstall();
        _hook = null;
    }
}
