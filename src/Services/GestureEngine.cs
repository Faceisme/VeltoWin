using System.Windows.Threading;
using Velto.Models;
using Velto.UI;
using Velto.Win32;

namespace Velto.Services;

/// <summary>
/// 鼠标手势生命周期状态机:
///   <c>Idle → Pending → Gesturing → (Match | Cancel) → Idle</c>
///
/// 架构(v2,移到独立线程后):
///   - <see cref="HandleMouseEvent"/> 在 <see cref="HookThread"/> 上跑
///   - 状态字段用 <c>_stateLock</c> 保护 —— Timer 回调和 store 变更回调可能在其它线程改它
///   - 任何动 WPF UI 的调用(轨迹覆盖层)都通过 <c>_uiDispatcher.BeginInvoke</c> hop 回 UI 线程
///   - 重活(SetForegroundWindow / 等待前台切换 / SendInput)放到 ThreadPool,绝不阻塞 hook 线程
///
/// 与 macOS 版的差异:
///   - mac 用 CFRunLoopTimer 挂在 tap runloop;Win 用 <see cref="System.Threading.Timer"/>
///     (DispatcherTimer 不行 —— 我们不在 UI 线程上了)
///   - mac 吞 mouseDragged 不影响光标;Win 上吞 WM_MOUSEMOVE 会让光标卡死,
///     所以这边只吞 R-DOWN / R-UP,move 事件全部放行
/// </summary>
public sealed class GestureEngine : IDisposable
{
    private enum State
    {
        Idle,
        Pending,
        Gesturing,
        CleanupAwaitingUp,
    }

    private readonly object _stateLock = new();
    private readonly ConfigStore _store;
    private readonly Func<TrailOverlayWindow> _overlayFactory;
    private readonly Dispatcher _uiDispatcher;
    private readonly GestureRecognizer _recognizer = new();
    private TrailOverlayWindow? _overlay;

    // R3:偏好 / 手势快照用 volatile 引用,hook 线程「无锁读」。
    // ConfigStore 改配置时是整体替换出新的不可变对象,所以持有旧引用始终是个一致的快照,
    // 更新只是原子换引用 —— hook 线程不再为了读配置去抢 _stateLock(避免和 UI 线程竞争)。
    private volatile AppPreferences _prefsSnapshot;
    private volatile GestureSnapshot _gestureSnapshot;

    private sealed record GestureSnapshot(IReadOnlyList<GestureCommand> Gestures, ulong Version);

    private State _state = State.Idle;
    private readonly List<Point> _points = new(256);
    private Point _startPoint;
    private Point _lastPoint;
    private Point _lastArmedPoint;
    private NativeMethods.POINT _startPointWin;
    private WindowTargeter.Target _startTarget = new(IntPtr.Zero, ShouldActivate: false);
    private long _downTick;
    private long _gestureSerial;
    private long _currentGestureId;
    private long _activeOverlayGestureId;

    // 乱画检测:累计路径长 + 外接框。路径长/对角线比值过大 = 来回乱涂 → 作废当前手势。
    private double _pathLength;
    private double _minX, _maxX, _minY, _maxY;

    // System.Threading.Timer:回调跑在 ThreadPool 上;callback 内重新进 lock 同步状态。
    private readonly System.Threading.Timer _gestureTimeoutTimer;
    private readonly System.Threading.Timer _safetyTimer;

    private const double MovementThreshold        = 10;
    private const double MinimumRecordedDistance  = 2;
    private const double TimeoutRearmDistance     = 8;
    private const int    MaximumGesturePointCount = 512;
    private const double SafetyTimeoutSeconds     = 8;

    // 乱画(scribble)判定:路径总长 / 外接框对角线。合法手势实测比值最大约 1.94
    // (刷新 / 重开标签 这类来回一次的);3.5 留足余量,而真正来回乱涂(多次反向)会轻松冲到 4+。
    // 只在画得够长(≥ScribbleMinPath)且对角线不太小(避免起笔噪声 / 除零)时才判。
    private const double ScribblePathRatio   = 3.5;
    private const double ScribbleMinPath     = 80;
    private const double ScribbleMinDiagonal = 25;

    public GestureEngine(ConfigStore store, Func<TrailOverlayWindow> overlayFactory, Dispatcher uiDispatcher)
    {
        _store = store;
        _overlayFactory = overlayFactory;
        _uiDispatcher = uiDispatcher;
        _prefsSnapshot = store.Preferences;
        _gestureSnapshot = new GestureSnapshot(store.Gestures, store.GesturesVersion);

        _gestureTimeoutTimer = new System.Threading.Timer(OnGestureTimeoutFired, null, Timeout.Infinite, Timeout.Infinite);
        _safetyTimer = new System.Threading.Timer(OnSafetyTimerFired, null, Timeout.Infinite, Timeout.Infinite);
        GestureDiagnosticLogger.Info($"diagnostics_ready path='{GestureDiagnosticLogger.CurrentPath}'");

        store.Changed += _ =>
        {
            // 无锁更新:原子换引用即可。hook 线程读时不必加锁,也就不会和这里竞争。
            _prefsSnapshot = store.Preferences;
            _gestureSnapshot = new GestureSnapshot(store.Gestures, store.GesturesVersion);
        };
    }

    public void Dispose()
    {
        _gestureTimeoutTimer.Dispose();
        _safetyTimer.Dispose();
    }

    /// <summary>由 <see cref="MouseHook"/> 在 hook 线程上直接调。返回 <c>true</c> = 吞掉。</summary>
    public bool HandleMouseEvent(MouseEvent e)
    {
        // 无锁快速拒绝:手势关闭时所有事件直接放行,连锁都不抢。
        if (!_prefsSnapshot.GesturesEnabled) return false;

        // 设置窗口活动时整体让路 —— 右键交给录制画布/系统菜单,不吞不识别。
        if (GestureGate.Suspended) return false;

        lock (_stateLock)
        {
            return e.Kind switch
            {
                MouseEventKind.RightButtonDown => HandleRightDown(e),
                MouseEventKind.MouseMove       => HandleMove(e),
                MouseEventKind.RightButtonUp   => HandleRightUp(e),
                _ => false,
            };
        }
    }

    // 以下私有方法都在 _stateLock 持有的前提下调用。

    private bool HandleRightDown(MouseEvent e)
    {
        if (_state != State.Idle) ResetTrackingLocked();
        _state = State.Pending;
        _startPoint = new Point(e.X, e.Y);
        _lastPoint = _startPoint;
        _startPointWin = new NativeMethods.POINT { X = e.X, Y = e.Y };
        _startTarget = WindowTargeter.Resolve(_prefsSnapshot.GestureTargetPolicy, _startPointWin);
        _downTick = Environment.TickCount64;
        _currentGestureId = ++_gestureSerial;
        var showTrail = _prefsSnapshot.ShowTrail;
        System.Threading.Volatile.Write(ref _activeOverlayGestureId, showTrail ? _currentGestureId : 0);
        _points.Clear();
        _points.Add(_startPoint);
        InitMetricsLocked(_startPoint);
        ArmSafetyTimerLocked();
        var snapshot = _points.ToArray();
        GestureDiagnosticLogger.Info(
            $"down id={_currentGestureId} x={e.X} y={e.Y} target={FormatHwnd(_startTarget.Hwnd)} " +
            $"activate={_startTarget.ShouldActivate} policy={_prefsSnapshot.GestureTargetPolicy} " +
            $"targetInfo=\"{WindowTargeter.Describe(_startTarget)}\" " +
            $"threshold={_prefsSnapshot.RecognitionThreshold:0.00} timeout={_prefsSnapshot.GestureTimeoutSeconds:0.00} " +
            $"showTrail={showTrail}");
        BeginOverlaySynchronously(_currentGestureId, snapshot, showTrail);
        return true; // 吞掉:先看是不是手势
    }

    private bool HandleMove(MouseEvent e)
    {
        // !!! Win 上吞掉 WM_MOUSEMOVE 会冻结光标,本函数永远 return false。!!!
        switch (_state)
        {
            case State.Pending:
            {
                var p = new Point(e.X, e.Y);
                _lastPoint = p;
                if (Distance(_startPoint, p) >= MovementThreshold)
                {
                    _points.Clear();
                    _points.Add(_startPoint);
                    InitMetricsLocked(_startPoint);
                    AppendPointLocked(p);
                    _state = State.Gesturing;
                    ArmGestureTimeoutTimerLocked();
                    _lastArmedPoint = p;
                    GestureDiagnosticLogger.Info(
                        $"begin_gesture id={_currentGestureId} movement={Distance(_startPoint, p):0.0}px " +
                        $"points={_points.Count} start=({_startPoint.X:0},{_startPoint.Y:0}) now=({p.X:0},{p.Y:0})");
                    var showTrail = _prefsSnapshot.ShowTrail;
                    var snapshot = _points.ToArray();
                    QueueOverlayUpdate(_currentGestureId, snapshot, showTrail);
                }
                return false;
            }
            case State.Gesturing:
            {
                var p = new Point(e.X, e.Y);
                var added = AppendPointLocked(p);
                if (added)
                {
                    // 乱画作废:还按着右键、但已经画成来回乱涂 → 取消当前手势,等松开(松开不触发任何快捷键)。
                    if (IsScribbleLocked())
                    {
                        Logger.Info($"gesture cancelled: 乱画作废 (path={_pathLength:0}px)");
                        GestureDiagnosticLogger.Info($"cancel_scribble id={_currentGestureId} path={_pathLength:0.0}");
                        CancelAndAwaitRightUpLocked();
                        return false;
                    }
                    if (Distance(_lastArmedPoint, p) >= TimeoutRearmDistance)
                    {
                        ArmGestureTimeoutTimerLocked();
                        _lastArmedPoint = p;
                    }
                    var showTrail = _prefsSnapshot.ShowTrail;
                    var snapshot = _points.ToArray();
                    QueueOverlayUpdate(_currentGestureId, snapshot, showTrail);
                }
                return false;
            }
            case State.CleanupAwaitingUp:
                _lastPoint = new Point(e.X, e.Y);
                return false;
            default:
                return false;
        }
    }

    private bool HandleRightUp(MouseEvent e)
    {
        switch (_state)
        {
            case State.Pending:
            {
                // 纯单击 → 回放右键让系统菜单弹出。
                // ★关键★:绝不能在这里(钩子回调内 + 持锁)直接 SendInput。
                // 从 LL 钩子回调内部注入输入,系统会把注入事件排到当前钩子处理之后并串行化,
                // 实测右键菜单要等 1.5s 以上。改成 ThreadPool 在回调返回后立刻回放,菜单瞬时弹出。
                var capturedGestureId = _currentGestureId;
                var elapsedMs = Environment.TickCount64 - _downTick;
                GestureDiagnosticLogger.Info($"right_click_replay id={capturedGestureId} elapsedMs={elapsedMs}");
                ResetTrackingLocked(hideOverlay: false);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    HideOverlaySynchronously(capturedGestureId);
                    KeyboardSender.ReplayRightClick();
                });
                return true;
            }
            case State.Gesturing:
            {
                AppendPointLocked(new Point(e.X, e.Y));
                var capturedGestureId = _currentGestureId;
                var capturedDownTick = _downTick;
                var capturedPoints = _points.ToArray();
                var capturedTarget = _startTarget;
                var capturedPrefs = _prefsSnapshot;
                var snapshot = _gestureSnapshot;

                ResetTrackingLocked(hideOverlay: false);

                // SetForegroundWindow + 等前台切换 + SendInput 可能慢,放到 ThreadPool,
                // 让 hook 线程立刻返回继续接事件。
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    HideOverlaySynchronously(capturedGestureId);
                    RunGesture(capturedGestureId, capturedDownTick, capturedPoints, capturedTarget, capturedPrefs, snapshot.Gestures, snapshot.Version);
                });
                return true;
            }
            case State.CleanupAwaitingUp:
                ResetTrackingLocked();
                return true;
            default:
                return false;
        }
    }

    private void RunGesture(
        long gestureId,
        long downTick,
        Point[] points,
        WindowTargeter.Target target,
        AppPreferences prefs,
        IReadOnlyList<GestureCommand> gestures,
        ulong version)
    {
        try
        {
            // 候选方向序列写进日志 —— 误识别时一眼能看出"我画的"被识成了什么形状
            var drawn = _recognizer.DescribeSequence(points);
            var simpleDirection = _recognizer.DescribeSimpleDirection(points);
            var candidates = _recognizer.DescribeCandidates(points, gestures, version);
            var trace = DescribeTrace(points, downTick);
            var match = _recognizer.BestMatch(points, gestures, version, prefs.RecognitionThreshold);
            if (match is null)
            {
                Logger.Info($"gesture no match: drawn=[{drawn}] (points={points.Length}, threshold={prefs.RecognitionThreshold:0.00})");
                GestureDiagnosticLogger.Info(
                    $"result id={gestureId} status=no_match {trace} drawn=[{drawn}] simple={simpleDirection} " +
                    $"threshold={prefs.RecognitionThreshold:0.00} candidates=[{candidates}]");
                return;
            }

            var shortcut = match.Command.Shortcut;
            if (shortcut is null)
            {
                Logger.Info($"gesture matched '{match.Command.Name}' (drawn=[{drawn}]) but no shortcut bound");
                GestureDiagnosticLogger.Info(
                    $"result id={gestureId} status=no_shortcut command='{match.Command.Name}' strategy={match.Strategy} " +
                    $"{trace} drawn=[{drawn}] simple={simpleDirection} distance={match.Distance:0.000} " +
                    $"runnerUp={FormatNullable(match.RunnerUpDistance)} candidates=[{candidates}]");
                return;
            }

            Logger.Info($"gesture matched '{match.Command.Name}' drawn=[{drawn}] distance={match.Distance:0.000} runnerUp={match.RunnerUpDistance:0.000} → {shortcut.DisplayName}");

            GestureDiagnosticLogger.Info(
                $"result id={gestureId} status=matched command='{match.Command.Name}' strategy={match.Strategy} " +
                $"{trace} drawn=[{drawn}] simple={simpleDirection} distance={match.Distance:0.000} " +
                $"runnerUp={FormatNullable(match.RunnerUpDistance)} shortcut='{shortcut.DisplayName}' " +
                $"target={FormatHwnd(target.Hwnd)} activate={target.ShouldActivate} " +
                $"targetInfo=\"{WindowTargeter.Describe(target)}\" candidates=[{candidates}]");

            if (KeyboardSender.IsBrowserNavigationShortcut(shortcut))
            {
                WindowTargeter.PrepareForExecution(target);
                var sent = KeyboardSender.TrySendBrowserNavigationInput(shortcut, target.Hwnd, out var method);
                GestureDiagnosticLogger.Info(
                    $"execute id={gestureId} type=browser_navigation sent={sent} method={method} " +
                    $"target={FormatHwnd(target.Hwnd)} targetInfo=\"{WindowTargeter.Describe(target)}\"");
                return;
            }

            WindowTargeter.PrepareForExecution(target);

            // A1:不再固定 Sleep(60)。SetForegroundWindow 后轮询确认目标真的拿到前台,
            // 拿到就立刻发键(通常 <16ms),最多等 ~150ms 兜底。比死等 60ms 既快又稳:
            // 快(常见情况几乎不等)、稳(慢的机器也不会因为 60ms 不够而发错窗口)。
            if (target.ShouldActivate)
            {
                WaitForForeground(target.Hwnd, timeoutMs: 150);
            }
            var sentInputs = KeyboardSender.Send(shortcut);
            GestureDiagnosticLogger.Info(
                $"execute id={gestureId} type=keyboard shortcut='{shortcut.DisplayName}' sentInputs={sentInputs} " +
                $"target={FormatHwnd(target.Hwnd)} targetInfo=\"{WindowTargeter.Describe(target)}\"");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "RunGesture");
            GestureDiagnosticLogger.Error(ex, $"RunGesture id={gestureId}");
        }
    }

    /// <summary>轮询等待目标窗口拿到前台,拿到立即返回;超时也返回(已尽力)。</summary>
    private static string DescribeTrace(IReadOnlyList<Point> points, long downTick)
    {
        if (points.Count == 0)
        {
            return "points=0";
        }

        var first = points[0];
        var last = points[^1];
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        var displacement = Math.Sqrt(dx * dx + dy * dy);
        var path = 0.0;
        var minX = first.X;
        var maxX = first.X;
        var minY = first.Y;
        var maxY = first.Y;
        for (int i = 1; i < points.Count; i++)
        {
            path += Distance(points[i - 1], points[i]);
            minX = Math.Min(minX, points[i].X);
            maxX = Math.Max(maxX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxY = Math.Max(maxY, points[i].Y);
        }

        var straightness = path <= 0 ? 0 : displacement / path;
        var direction = Math.Abs(dx) >= Math.Abs(dy)
            ? (dx < 0 ? "Left" : "Right")
            : (dy < 0 ? "Up" : "Down");
        var smallerAxis = Math.Min(Math.Abs(dx), Math.Abs(dy));
        var axisRatio = smallerAxis <= 0
            ? 999.0
            : Math.Max(Math.Abs(dx), Math.Abs(dy)) / smallerAxis;
        var elapsedMs = Math.Max(0, Environment.TickCount64 - downTick);

        return
            $"points={points.Count} elapsedMs={elapsedMs} path={path:0.0} displacement={displacement:0.0} " +
            $"straightness={straightness:0.000} direction={direction} axisRatio={axisRatio:0.00} " +
            $"dx={dx:0.0} dy={dy:0.0} bbox=({minX:0},{minY:0},{maxX:0},{maxY:0}) " +
            $"start=({first.X:0},{first.Y:0}) end=({last.X:0},{last.Y:0})";
    }

    private static string FormatHwnd(IntPtr hwnd)
        => "0x" + unchecked((ulong)hwnd.ToInt64()).ToString("X");

    private static string FormatNullable(double? value)
        => value.HasValue ? value.Value.ToString("0.000") : "null";

    private static void WaitForForeground(IntPtr hwnd, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (NativeMethods.GetForegroundWindow() != hwnd && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(8);
        }
    }

    private bool AppendPointLocked(Point p)
    {
        _lastPoint = p;
        if (_points.Count == 0)
        {
            _points.Add(p);
            InitMetricsLocked(p);
            return true;
        }
        var prev = _points[^1];
        if (Distance(prev, p) < MinimumRecordedDistance) return false;

        if (_points.Count >= MaximumGesturePointCount) _points[^1] = p;
        else _points.Add(p);
        AccumulateMetricsLocked(prev, p);
        return true;
    }

    private void InitMetricsLocked(Point p)
    {
        _pathLength = 0;
        _minX = _maxX = p.X;
        _minY = _maxY = p.Y;
    }

    private void AccumulateMetricsLocked(Point prev, Point p)
    {
        _pathLength += Distance(prev, p);
        if (p.X < _minX) _minX = p.X; else if (p.X > _maxX) _maxX = p.X;
        if (p.Y < _minY) _minY = p.Y; else if (p.Y > _maxY) _maxY = p.Y;
    }

    /// <summary>当前笔画是否已成"乱画"(来回涂抹):路径总长相对外接框对角线过大。</summary>
    private bool IsScribbleLocked()
    {
        if (_pathLength < ScribbleMinPath) return false;
        var dx = _maxX - _minX;
        var dy = _maxY - _minY;
        var diagonal = Math.Sqrt(dx * dx + dy * dy);
        if (diagonal < ScribbleMinDiagonal) return false;
        return _pathLength / diagonal > ScribblePathRatio;
    }

    private void ResetTrackingLocked(bool hideOverlay = true)
    {
        _state = State.Idle;
        _points.Clear();
        _pathLength = 0;
        _startPoint = default;
        _lastPoint = default;
        _lastArmedPoint = default;
        _startTarget = new WindowTargeter.Target(IntPtr.Zero, ShouldActivate: false);
        _gestureTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _safetyTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (hideOverlay) HideOverlayAsync();
    }

    private void CancelAndAwaitRightUpLocked()
    {
        _state = State.CleanupAwaitingUp;
        _points.Clear();
        _pathLength = 0;
        _startTarget = new WindowTargeter.Target(IntPtr.Zero, ShouldActivate: false);
        _gestureTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _safetyTimer.Change(Timeout.Infinite, Timeout.Infinite);
        HideOverlayAsync();
    }

    private void HideOverlayAsync()
    {
        var gestureId = System.Threading.Volatile.Read(ref _activeOverlayGestureId);
        if (gestureId == 0)
        {
            GestureDiagnosticLogger.Info("overlay_end_async_skipped id=none");
            return;
        }

        if (System.Threading.Interlocked.CompareExchange(ref _activeOverlayGestureId, 0, gestureId) != gestureId)
        {
            GestureDiagnosticLogger.Info($"overlay_end_async_skipped id={gestureId} reason=stale");
            return;
        }

        GestureDiagnosticLogger.Info($"overlay_end_async id={gestureId}");
        _uiDispatcher.BeginInvoke(() => _overlay?.EndGesture(), DispatcherPriority.Render);
    }

    private void BeginOverlaySynchronously(long gestureId, IReadOnlyList<Point> points, bool showTrail)
    {
        if (!showTrail)
        {
            GestureDiagnosticLogger.Info($"overlay_begin_skipped id={gestureId} reason=trail_disabled");
            return;
        }

        if (_uiDispatcher.HasShutdownStarted || _uiDispatcher.HasShutdownFinished)
        {
            GestureDiagnosticLogger.Info($"overlay_begin_skipped id={gestureId} reason=dispatcher_shutdown");
            return;
        }

        var started = Environment.TickCount64;
        try
        {
            if (_uiDispatcher.CheckAccess())
            {
                if (IsOverlayGestureCurrent(gestureId))
                {
                    GetOverlay().BeginGesture(points, showTrail: true);
                }
            }
            else
            {
                _uiDispatcher.Invoke(() =>
                {
                    if (IsOverlayGestureCurrent(gestureId))
                    {
                        GetOverlay().BeginGesture(points, showTrail: true);
                    }
                }, DispatcherPriority.Send);
            }
            GestureDiagnosticLogger.Info(
                $"overlay_begin id={gestureId} elapsedMs={Environment.TickCount64 - started} points={points.Count}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "BeginOverlaySynchronously");
            GestureDiagnosticLogger.Error(ex, $"BeginOverlaySynchronously id={gestureId}");
        }
    }

    private void HideOverlaySynchronously(long? gestureId = null)
    {
        if (gestureId is { } id)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _activeOverlayGestureId, 0, id) != id)
            {
                GestureDiagnosticLogger.Info($"overlay_end_skipped id={id} reason=stale");
                return;
            }
        }
        else
        {
            System.Threading.Volatile.Write(ref _activeOverlayGestureId, 0);
        }

        if (_uiDispatcher.HasShutdownStarted || _uiDispatcher.HasShutdownFinished)
        {
            GestureDiagnosticLogger.Info($"overlay_end_skipped id={gestureId?.ToString() ?? "none"} reason=dispatcher_shutdown");
            return;
        }

        var started = Environment.TickCount64;
        try
        {
            _uiDispatcher.Invoke(() => _overlay?.EndGesture(), DispatcherPriority.Send);
            GestureDiagnosticLogger.Info(
                $"overlay_end id={gestureId?.ToString() ?? "none"} elapsedMs={Environment.TickCount64 - started}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "HideOverlaySynchronously");
            GestureDiagnosticLogger.Error(ex, $"HideOverlaySynchronously id={gestureId?.ToString() ?? "none"}");
        }
    }

    private void QueueOverlayUpdate(long gestureId, IReadOnlyList<Point> points, bool showTrail)
    {
        if (!showTrail)
        {
            return;
        }

        _uiDispatcher.BeginInvoke(() =>
        {
            if (!IsOverlayGestureCurrent(gestureId))
            {
                GestureDiagnosticLogger.Info($"overlay_update_skipped id={gestureId} reason=stale");
                return;
            }

            GetOverlay().UpdateGesture(points, showTrail);
        }, DispatcherPriority.Render);
    }

    private TrailOverlayWindow GetOverlay()
        => _overlay ??= _overlayFactory();

    private bool IsOverlayGestureCurrent(long gestureId)
        => System.Threading.Volatile.Read(ref _activeOverlayGestureId) == gestureId;

    private void ArmSafetyTimerLocked()
    {
        var ms = (int)(Math.Max(SafetyTimeoutSeconds, _prefsSnapshot.GestureTimeoutSeconds + 2) * 1000);
        _safetyTimer.Change(ms, Timeout.Infinite);
    }

    private void ArmGestureTimeoutTimerLocked()
    {
        var ms = (int)(Math.Max(0.5, _prefsSnapshot.GestureTimeoutSeconds) * 1000);
        _gestureTimeoutTimer.Change(ms, Timeout.Infinite);
    }

    private void OnGestureTimeoutFired(object? _)
    {
        lock (_stateLock)
        {
            if (_state == State.Gesturing)
            {
                GestureDiagnosticLogger.Info($"cancel_timeout id={_currentGestureId}");
                CancelAndAwaitRightUpLocked();
            }
        }
    }

    private void OnSafetyTimerFired(object? _)
    {
        lock (_stateLock)
        {
            if (_state != State.Idle)
            {
                GestureDiagnosticLogger.Info($"cancel_safety_timeout id={_currentGestureId} state={_state}");
                CancelAndAwaitRightUpLocked();
            }
        }
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
