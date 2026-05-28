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
    private readonly TrailOverlayWindow _overlay;
    private readonly Dispatcher _uiDispatcher;
    private readonly GestureRecognizer _recognizer = new();

    // 偏好快照 —— 钩子回调里读,避免每次过 ConfigStore。锁保护更新。
    private AppPreferences _prefsSnapshot;
    private IReadOnlyList<GestureCommand> _gesturesSnapshot;
    private ulong _gesturesVersionSnapshot;

    private State _state = State.Idle;
    private readonly List<Point> _points = new(256);
    private Point _startPoint;
    private Point _lastPoint;
    private Point _lastArmedPoint;
    private NativeMethods.POINT _startPointWin;

    // System.Threading.Timer:回调跑在 ThreadPool 上;callback 内重新进 lock 同步状态。
    private readonly System.Threading.Timer _gestureTimeoutTimer;
    private readonly System.Threading.Timer _safetyTimer;

    private const double MovementThreshold        = 10;
    private const double MinimumRecordedDistance  = 2;
    private const double TimeoutRearmDistance     = 8;
    private const int    MaximumGesturePointCount = 512;
    private const double SafetyTimeoutSeconds     = 8;

    public GestureEngine(ConfigStore store, TrailOverlayWindow overlay, Dispatcher uiDispatcher)
    {
        _store = store;
        _overlay = overlay;
        _uiDispatcher = uiDispatcher;
        _prefsSnapshot = store.Preferences;
        _gesturesSnapshot = store.Gestures;
        _gesturesVersionSnapshot = store.GesturesVersion;

        _gestureTimeoutTimer = new System.Threading.Timer(OnGestureTimeoutFired, null, Timeout.Infinite, Timeout.Infinite);
        _safetyTimer = new System.Threading.Timer(OnSafetyTimerFired, null, Timeout.Infinite, Timeout.Infinite);

        store.Changed += _ =>
        {
            // ConfigStore.Changed 在 UI 线程上 fire。我们用锁短暂阻塞 hook 线程读快照,毫秒级。
            lock (_stateLock)
            {
                _prefsSnapshot = store.Preferences;
                _gesturesSnapshot = store.Gestures;
                _gesturesVersionSnapshot = store.GesturesVersion;
            }
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
        lock (_stateLock)
        {
            if (!_prefsSnapshot.GesturesEnabled) return false;

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
        _points.Clear();
        _points.Add(_startPoint);
        ArmSafetyTimerLocked();
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
                AppendPointLocked(p);
                if (Distance(_startPoint, p) >= MovementThreshold)
                {
                    _state = State.Gesturing;
                    ArmGestureTimeoutTimerLocked();
                    _lastArmedPoint = p;
                    if (_prefsSnapshot.ShowTrail)
                    {
                        var snapshot = _points.ToArray();
                        _uiDispatcher.BeginInvoke(() => _overlay.Show(snapshot), DispatcherPriority.Render);
                    }
                }
                return false;
            }
            case State.Gesturing:
            {
                var p = new Point(e.X, e.Y);
                var added = AppendPointLocked(p);
                if (added)
                {
                    if (Distance(_lastArmedPoint, p) >= TimeoutRearmDistance)
                    {
                        ArmGestureTimeoutTimerLocked();
                        _lastArmedPoint = p;
                    }
                    if (_prefsSnapshot.ShowTrail)
                    {
                        var snapshot = _points.ToArray();
                        _uiDispatcher.BeginInvoke(() => _overlay.Update(snapshot), DispatcherPriority.Render);
                    }
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
                // 纯单击 → 回放右键事件,让系统右键菜单弹出。
                ResetTrackingLocked();
                KeyboardSender.ReplayRightClick();
                return true;
            }
            case State.Gesturing:
            {
                AppendPointLocked(new Point(e.X, e.Y));
                var capturedPoints = _points.ToArray();
                var capturedStartWin = _startPointWin;
                var capturedPrefs = _prefsSnapshot;
                var capturedGestures = _gesturesSnapshot;
                var capturedVersion = _gesturesVersionSnapshot;

                ResetTrackingLocked();

                // SetForegroundWindow + 等前台切换 + SendInput 可能慢,放到 ThreadPool,
                // 让 hook 线程立刻返回继续接事件。
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    RunGesture(capturedPoints, capturedStartWin, capturedPrefs, capturedGestures, capturedVersion);
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
        Point[] points,
        NativeMethods.POINT startPointWin,
        AppPreferences prefs,
        IReadOnlyList<GestureCommand> gestures,
        ulong version)
    {
        try
        {
            var match = _recognizer.BestMatch(points, gestures, version, prefs.RecognitionThreshold);
            if (match is null)
            {
                Logger.Info($"gesture no match (points={points.Length}, threshold={prefs.RecognitionThreshold:0.00})");
                return;
            }

            var shortcut = match.Command.Shortcut;
            if (shortcut is null)
            {
                Logger.Info($"gesture matched '{match.Command.Name}' but no shortcut bound");
                return;
            }

            Logger.Info($"gesture matched '{match.Command.Name}' (distance={match.Distance:0.000}) → {shortcut.DisplayName}");

            var target = WindowTargeter.Resolve(prefs.GestureTargetPolicy, startPointWin);
            WindowTargeter.PrepareForExecution(target);

            if (target.ShouldActivate)
            {
                Thread.Sleep(60);
            }
            KeyboardSender.Send(shortcut);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "RunGesture");
        }
    }

    private bool AppendPointLocked(Point p)
    {
        _lastPoint = p;
        if (_points.Count == 0)
        {
            _points.Add(p);
            return true;
        }
        var prev = _points[^1];
        if (Distance(prev, p) < MinimumRecordedDistance) return false;

        if (_points.Count >= MaximumGesturePointCount) _points[^1] = p;
        else _points.Add(p);
        return true;
    }

    private void ResetTrackingLocked()
    {
        _state = State.Idle;
        _points.Clear();
        _startPoint = default;
        _lastPoint = default;
        _lastArmedPoint = default;
        _gestureTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _safetyTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _uiDispatcher.BeginInvoke(() => _overlay.Hide(), DispatcherPriority.Render);
    }

    private void CancelAndAwaitRightUpLocked()
    {
        _state = State.CleanupAwaitingUp;
        _points.Clear();
        _gestureTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _safetyTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _uiDispatcher.BeginInvoke(() => _overlay.Hide(), DispatcherPriority.Render);
    }

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
            if (_state == State.Gesturing) CancelAndAwaitRightUpLocked();
        }
    }

    private void OnSafetyTimerFired(object? _)
    {
        lock (_stateLock)
        {
            if (_state != State.Idle) CancelAndAwaitRightUpLocked();
        }
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
