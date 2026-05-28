using System.Windows;
using System.Windows.Threading;
using Velto.Services;
using Velto.UI;

namespace Velto;

public partial class App : Application
{
    private HookThread? _hookThread;
    private TrailOverlayWindow? _overlay;
    private GestureEngine? _engine;
    private TrayIcon? _tray;
    private System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallGlobalExceptionHandlers();
        Logger.Info($"Velto starting — pid={Environment.ProcessId}, exe={Environment.ProcessPath}");

        if (!TryAcquireSingleInstance())
        {
            Logger.Info("另一实例已在运行,退出");
            Shutdown();
            return;
        }

        try
        {
            var store = ConfigStore.Shared;

            _overlay = new TrailOverlayWindow();
            // 先 Show + Hide 一次让 hwnd 真的建出来,后续从 hook 线程 BeginInvoke 过来时不用走 lazy realize。
            _overlay.Show();
            _overlay.Hide();

            _engine = new GestureEngine(store, _overlay, Dispatcher);

            _hookThread = new HookThread();
            _hookThread.Start(_engine.HandleMouseEvent);
            Logger.Info("MouseHook installed on dedicated input thread");

            _tray = new TrayIcon(store);
            Logger.Info("Tray icon ready,启动完成");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OnStartup 致命异常");
            throw;
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
        _tray?.Dispose();
        _hookThread?.Stop();
        _engine?.Dispose();
        _overlay?.Close();
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
