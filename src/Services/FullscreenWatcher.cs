namespace Velto.Services;

/// <summary>
/// 全屏应用监视器:周期性检查前台是否为全屏应用,是则借 <see cref="GestureGate"/> 暂停手势,
/// 退出全屏后自动恢复。受偏好 <c>PauseInFullscreen</c> 控制 —— 关掉就停轮询并立即放行。
///
/// 复用 <see cref="GestureGate.Suspend"/>:它内部是引用计数,和"设置窗口激活时的暂停"叠加无冲突;
/// 监视器持有一份挂起句柄,进入全屏时拿、退出全屏时还。
///
/// 用低频(<see cref="PollIntervalMs"/>)轮询而非窗口事件钩子:轮询能一并覆盖
/// "无边框全屏游戏原地切换 / 视频进入全屏"这类不触发前台切换的情形,且每次只是几个
/// 廉价的 Win32 查询,CPU 可忽略。单发计时器在回调末尾重新武装,杜绝回调重入。
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private const int PollIntervalMs = 750;

    private readonly ConfigStore _store;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();

    private IDisposable? _suspension;
    private bool _enabled;
    private bool _disposed;

    public FullscreenWatcher(ConfigStore store)
    {
        _store = store;
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        _store.Changed += OnStoreChanged;
        ApplyEnabled(store.Preferences.PauseInFullscreen);
    }

    private void OnStoreChanged(ConfigStore.ChangeReason _)
        => ApplyEnabled(_store.Preferences.PauseInFullscreen);

    private void ApplyEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_disposed || enabled == _enabled) return;
            _enabled = enabled;

            if (enabled)
            {
                // 立即评估一次,避免开启后还要等一个周期才生效。
                _timer.Change(0, Timeout.Infinite);
            }
            else
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                ReleaseSuspensionLocked();
            }
        }
    }

    private void OnTick(object? _)
    {
        bool fullscreen;
        try
        {
            fullscreen = FullscreenDetector.IsForegroundFullscreen();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "FullscreenWatcher.OnTick");
            fullscreen = false; // 出错时宁可放行手势,也不要把用户的手势锁死。
        }

        lock (_lock)
        {
            if (_disposed || !_enabled) return;

            if (fullscreen) AcquireSuspensionLocked();
            else ReleaseSuspensionLocked();

            _timer.Change(PollIntervalMs, Timeout.Infinite);
        }
    }

    private void AcquireSuspensionLocked()
    {
        if (_suspension is not null) return;
        _suspension = GestureGate.Suspend();
        Logger.Info("检测到全屏应用,暂停手势识别");
    }

    private void ReleaseSuspensionLocked()
    {
        if (_suspension is null) return;
        _suspension.Dispose();
        _suspension = null;
        Logger.Info("已退出全屏应用,恢复手势识别");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _store.Changed -= OnStoreChanged;
            _timer.Dispose();
            ReleaseSuspensionLocked();
        }
    }
}
