using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Velto.Services;
using Velto.Win32;

namespace Velto.UI;

/// <summary>
/// Pure Win32 tray icon: Shell_NotifyIconW + a hidden HwndSource message window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint WM_TRAYICON = NativeMethods.WM_APP + 1;

    private const uint MenuOpenSettings = 1001;
    private const uint MenuToggleGestures = 1002;
    private const uint MenuExit = 1003;

    private readonly ConfigStore _store;
    private readonly HwndSource _messageSource;
    private readonly uint _taskbarCreatedMessage;

    private SettingsWindow? _settingsWindow;
    private IntPtr _iconHandle;
    private bool _ownsIconHandle;
    private bool _iconAdded;

    public TrayIcon(ConfigStore store)
    {
        _store = store;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        var parameters = new HwndSourceParameters("VeltoTrayMessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);

        (_iconHandle, _ownsIconHandle) = LoadTrayIcon();
        if (_store.Preferences.ShowTrayIcon)
        {
            AddTrayIcon();
        }

        // 保存设置后(ReplaceSettings 触发 Changed)同步托盘显隐
        _store.Changed += _ => SetVisible(_store.Preferences.ShowTrayIcon);
    }

    /// <summary>被 App 的"再次启动 → 唤起设置"信号调用,即使托盘隐藏也能回到设置。</summary>
    public void OpenSettings() => ShowSettings();

    /// <summary>按偏好显示/隐藏托盘图标。隐藏只是从通知区移除,进程和手势照常工作。</summary>
    public void SetVisible(bool visible)
    {
        if (visible == _iconAdded) return;
        if (visible) AddTrayIcon();
        else RemoveTrayIcon();
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded) return;
        var data = CreateNotifyIconData();
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var message = unchecked((uint)msg);
        if (message == WM_TRAYICON)
        {
            handled = true;
            HandleTrayMessage(lParam);
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            handled = true;
            AddTrayIcon();
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void HandleTrayMessage(IntPtr lParam)
    {
        var eventMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
        switch (eventMessage)
        {
            case NativeMethods.WM_LBUTTONDBLCLK:
                ShowSettings();
                break;
            case NativeMethods.WM_CONTEXTMENU:
            case NativeMethods.WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new UIntPtr(MenuOpenSettings), "设置...");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);

            var toggleFlags = NativeMethods.MF_STRING |
                              (_store.Preferences.GesturesEnabled ? NativeMethods.MF_CHECKED : 0);
            NativeMethods.AppendMenuW(menu, toggleFlags, new UIntPtr(MenuToggleGestures), "启用鼠标手势");

            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new UIntPtr(MenuExit), "退出 Velto");

            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                cursor = default;
            }

            NativeMethods.SetForegroundWindow(_messageSource.Handle);
            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                _messageSource.Handle,
                IntPtr.Zero);
            NativeMethods.PostMessageW(_messageSource.Handle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            HandleMenuCommand(command);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private void HandleMenuCommand(uint command)
    {
        switch (command)
        {
            case MenuOpenSettings:
                ShowSettings();
                break;
            case MenuToggleGestures:
                _store.UpdatePreferences(p => p.GesturesEnabled = !p.GesturesEnabled);
                break;
            case MenuExit:
                Application.Current.Shutdown();
                break;
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_store);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            _settingsWindow.Activate();
        }
    }

    private void AddTrayIcon()
    {
        var data = CreateNotifyIconData();
        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data))
        {
            var addError = Marshal.GetLastWin32Error();
            if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data))
            {
                Logger.Info($"Shell_NotifyIconW(NIM_ADD) failed: {addError}; NIM_MODIFY failed: {Marshal.GetLastWin32Error()}");
                return;
            }
        }

        _iconAdded = true;
        data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
    }

    private NativeMethods.NOTIFYICONDATAW CreateNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _messageSource.Handle,
        uID = TrayIconId,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _iconHandle,
        szTip = "Velto - 鼠标手势",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static (IntPtr Handle, bool OwnsHandle) LoadTrayIcon()
    {
        try
        {
            var width = Math.Max(16, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON));
            var height = Math.Max(16, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON));
            var iconBytes = ReadTrayIconBytes();

            var handle = CreateIconFromIco(iconBytes, width, height);
            if (handle != IntPtr.Zero)
            {
                return (handle, true);
            }

            var iconPath = ExtractTrayIcon(iconBytes);
            Logger.Info($"CreateIconFromResourceEx tray icon failed: {Marshal.GetLastWin32Error()}");
            handle = NativeMethods.LoadImageW(
                IntPtr.Zero,
                iconPath,
                NativeMethods.IMAGE_ICON,
                width,
                height,
                NativeMethods.LR_LOADFROMFILE);
            if (handle != IntPtr.Zero)
            {
                return (handle, true);
            }

            Logger.Info($"LoadImageW tray icon failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "LoadTrayIcon");
        }

        return (NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IDI_APPLICATION), false);
    }

    private static byte[] ReadTrayIconBytes()
    {
        var uri = new Uri("pack://application:,,,/Resources/Velto.ico", UriKind.Absolute);
        var info = Application.GetResourceStream(uri)
                   ?? throw new FileNotFoundException("Embedded tray icon resource was not found.", uri.ToString());

        using var stream = info.Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ExtractTrayIcon(byte[] bytes)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velto");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "Velto.ico");

        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes))
        {
            File.WriteAllBytes(path, bytes);
        }

        return path;
    }

    private static IntPtr CreateIconFromIco(byte[] icoBytes, int desiredWidth, int desiredHeight)
    {
        if (icoBytes.Length < 6 ||
            BitConverter.ToUInt16(icoBytes, 0) != 0 ||
            BitConverter.ToUInt16(icoBytes, 2) != 1)
        {
            return IntPtr.Zero;
        }

        var count = BitConverter.ToUInt16(icoBytes, 4);
        var bestOffset = 0;
        var bestLength = 0;
        var bestScore = int.MaxValue;

        for (int i = 0; i < count; i++)
        {
            var entryOffset = 6 + i * 16;
            if (entryOffset + 16 > icoBytes.Length)
            {
                break;
            }

            var width = icoBytes[entryOffset] == 0 ? 256 : icoBytes[entryOffset];
            var height = icoBytes[entryOffset + 1] == 0 ? 256 : icoBytes[entryOffset + 1];
            var bytesInRes = checked((int)BitConverter.ToUInt32(icoBytes, entryOffset + 8));
            var imageOffset = checked((int)BitConverter.ToUInt32(icoBytes, entryOffset + 12));
            if (bytesInRes <= 0 || imageOffset < 0 || imageOffset + bytesInRes > icoBytes.Length)
            {
                continue;
            }

            var score = Math.Abs(width - desiredWidth) + Math.Abs(height - desiredHeight);
            if (width < desiredWidth || height < desiredHeight)
            {
                score += 1000;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestOffset = imageOffset;
                bestLength = bytesInRes;
            }
        }

        if (bestLength == 0)
        {
            return IntPtr.Zero;
        }

        var iconBits = new byte[bestLength];
        Array.Copy(icoBytes, bestOffset, iconBits, 0, bestLength);
        return NativeMethods.CreateIconFromResourceEx(
            iconBits,
            (uint)iconBits.Length,
            fIcon: true,
            dwVer: 0x00030000,
            cxDesired: desiredWidth,
            cyDesired: desiredHeight,
            flags: 0);
    }

    public void Dispose()
    {
        if (_iconAdded)
        {
            var data = CreateNotifyIconData();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _iconAdded = false;
        }

        _messageSource.RemoveHook(WndProc);
        _messageSource.Dispose();

        if (_ownsIconHandle && _iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
        }
        _iconHandle = IntPtr.Zero;
    }
}
