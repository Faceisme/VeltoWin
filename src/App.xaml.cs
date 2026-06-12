using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ModernWpf;
using Velto.Services;
using Velto.UI;

namespace Velto;

public partial class App : Application
{
    private const string ShowSettingsSignalName = "Velto.ShowSettings";
    private static readonly bool EnableShellTransitionDiagnostics =
        GestureDiagnosticLogger.IsSwitchEnabled("VELTO_DIAG") ||
        GestureDiagnosticLogger.IsSwitchEnabled("VELTO_SHELL_DIAG");

    private HookThread? _hookThread;
    private TrailOverlayWindow? _overlay;
    private GestureEngine? _engine;
    private TrayIcon? _tray;
    private ShellTransitionDiagnostic? _shellDiagnostic;
    private System.Threading.Mutex? _instanceMutex;

    private EventWaitHandle? _showSettingsSignal;
    private EventWaitHandle? _listenerStopSignal;
    private Thread? _listenerThread;

    public App()
    {
        FollowSystemTheme();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        InstallGlobalExceptionHandlers();
        Logger.Info($"Velto starting — pid={Environment.ProcessId}, exe={Environment.ProcessPath}");

        if (!TryAcquireSingleInstance())
        {
            // 已经有实例在跑 —— 与其默默退出,不如唤起它的设置窗口。
            // 这样即便托盘图标被隐藏,用户双击 Velto.exe 也能回到设置。
            Logger.Info("另一实例已在运行,发信号唤起设置后退出");
            SignalExistingInstanceToShowSettings();
            Shutdown();
            return;
        }

        try
        {
            var store = ConfigStore.Shared;

            // 在开始菜单放一个快捷方式,保证"开始菜单搜索 Velto"能找到程序。
            StartMenuShortcut.Ensure();

            _engine = new GestureEngine(store, CreateOverlay, Dispatcher);

            _hookThread = new HookThread();
            _hookThread.Start(_engine.HandleMouseEvent);
            Logger.Info("MouseHook installed on dedicated input thread");

            // 预热轨迹覆盖层:空闲时先把窗口和 HWND 建好,
            // 首次手势显示轨迹时不必现场创建整个 WPF 窗口(可达上百毫秒)。
            Dispatcher.InvokeAsync(() =>
            {
                var overlay = CreateOverlay();
                new System.Windows.Interop.WindowInteropHelper(overlay).EnsureHandle();
            }, DispatcherPriority.ApplicationIdle);

            if (EnableShellTransitionDiagnostics)
            {
                _shellDiagnostic = new ShellTransitionDiagnostic();
            }

            _tray = new TrayIcon(store);
            StartShowSettingsListener();
            Logger.Info("Tray icon ready,启动完成");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OnStartup 致命异常");
            throw;
        }
    }

    private static void FollowSystemTheme()
    {
        ThemeManager.Current.ApplicationTheme = null;
    }

    private TrailOverlayWindow CreateOverlay()
        => _overlay ??= new TrailOverlayWindow();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        Dispatcher.BeginInvoke(() =>
        {
            FollowSystemTheme();

            foreach (var window in Windows.OfType<SettingsWindow>())
            {
                MicaBackdrop.Apply(window);
            }
        });
    }

    // ──────────────────────── 第二实例唤起设置 ────────────────────────

    private void StartShowSettingsListener()
    {
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsSignalName);
        _listenerStopSignal = new EventWaitHandle(false, EventResetMode.ManualReset);

        _listenerThread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showSettingsSignal, _listenerStopSignal };
            while (true)
            {
                var idx = WaitHandle.WaitAny(handles);
                if (idx == 1) break; // stop
                Dispatcher.BeginInvoke(() => _tray?.OpenSettings());
            }
        })
        {
            IsBackground = true,
            Name = "Velto.ShowSettingsListener",
        };
        _listenerThread.Start();
    }

    private static void SignalExistingInstanceToShowSettings()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowSettingsSignalName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SignalExistingInstanceToShowSettings");
        }
    }

    private void InstallGlobalExceptionHandlers()
    {
        // 任何线程的未处理异常 —— 比如 ThreadPool 上跑的 RunGesture / Timer 回调
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Logger.Error(ex, "AppDomain UnhandledException");
        };
        // WPF UI 线程异常 —— 设置窗口里点开各种 control 时
        DispatcherUnhandledException += (_, e) =>
        {
            Logger.Error(e.Exception, "Dispatcher UnhandledException");
            // 不 e.Handled = true:让原本的弹窗继续显示,免得用户看到 app 没反应不知所措
        };
        // Task 上抛但没人 await 的 —— 比如 BeginInvoke 里抛
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error(e.Exception, "Task UnobservedTaskException");
            e.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info($"Velto exiting (code={e.ApplicationExitCode})");
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listenerStopSignal?.Set();
        _tray?.Dispose();
        _shellDiagnostic?.Dispose();
        _hookThread?.Stop();
        _engine?.Dispose();
        _overlay?.Close();
        _showSettingsSignal?.Dispose();
        _listenerStopSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        // 不用 Global\ 前缀 —— 普通用户在受限环境(标准用户帐户)创建 Global\ 互斥体可能失败。
        // 当前用户会话级互斥就够,Velto 本来就是按用户跑的工具。
        const string name = "Velto.SingleInstance";
        _instanceMutex = new System.Threading.Mutex(initiallyOwned: true, name, out var createdNew);
        return createdNew;
    }
}
