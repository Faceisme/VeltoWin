using Velto.Models;
using Velto.Win32;
using System.Runtime.InteropServices;

namespace Velto.Services;

/// <summary>
/// 通过 SendInput 合成键盘事件。所有合成事件都带上
/// <see cref="NativeMethods.SyntheticEventMarker"/>,日后若需自识别(目前用不到)有据可查。
/// </summary>
public static class KeyboardSender
{
    // Virtual Key codes (subset — UI 录入时可以传任意 VK)
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU    = 0x12; // Alt
    public const ushort VK_SHIFT   = 0x10;
    public const ushort VK_LWIN    = 0x5B;
    public const ushort VK_RWIN    = 0x5C;
    public const ushort VK_ESCAPE  = 0x1B;
    public const ushort VK_LEFT    = 0x25;
    public const ushort VK_RIGHT   = 0x27;
    public const ushort VK_F4      = 0x73;
    public const ushort VK_W       = 0x57;

    public static uint Send(Shortcut shortcut)
        => SendKey(shortcut.VirtualKey, shortcut.Modifiers);

    public static uint SendWindowClose()
        => SendKey(VK_F4, ModifierKeys.Alt);

    public static bool ShouldUseWindowCloseFallback(Shortcut shortcut, string processName, string className)
    {
        if (shortcut.Modifiers != ModifierKeys.Control || shortcut.VirtualKey != VK_W)
        {
            return false;
        }

        if (IsKnownTabCloseProcess(processName))
        {
            return false;
        }

        return processName.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("TaskManagerWindow", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBrowserNavigationShortcut(Shortcut shortcut)
        => BrowserNavigationXButton(shortcut) != 0;

    public static bool TrySendBrowserNavigationInput(Shortcut shortcut)
        => TrySendBrowserNavigationInput(shortcut, IntPtr.Zero);

    public static bool TrySendBrowserNavigationInput(Shortcut shortcut, IntPtr targetHwnd)
        => TrySendBrowserNavigationInput(shortcut, targetHwnd, out _);

    public static bool TrySendBrowserNavigationInput(Shortcut shortcut, IntPtr targetHwnd, out string method)
    {
        method = "none";
        var appCommand = BrowserNavigationAppCommand(shortcut);
        if (appCommand == 0)
        {
            method = "not-browser-navigation";
            return false;
        }

        if (TryPostBrowserAppCommand(targetHwnd, appCommand))
        {
            method = "wm-appcommand";
            return true;
        }

        var xButton = BrowserNavigationXButton(shortcut);
        if (xButton == 0) return false;
        SendMouseXButton(xButton);
        method = "xbutton";
        return true;
    }

    private static int BrowserNavigationAppCommand(Shortcut shortcut)
    {
        if (shortcut.Modifiers != ModifierKeys.Alt)
        {
            return 0;
        }

        return shortcut.VirtualKey switch
        {
            VK_LEFT => NativeMethods.APPCOMMAND_BROWSER_BACKWARD,
            VK_RIGHT => NativeMethods.APPCOMMAND_BROWSER_FORWARD,
            _ => 0,
        };
    }

    private static uint BrowserNavigationXButton(Shortcut shortcut)
    {
        if (shortcut.Modifiers != ModifierKeys.Alt)
        {
            return 0;
        }

        return shortcut.VirtualKey switch
        {
            VK_LEFT => NativeMethods.XBUTTON1,
            VK_RIGHT => NativeMethods.XBUTTON2,
            _ => 0,
        };
    }

    private static bool IsKnownTabCloseProcess(string processName)
        => processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("opera", StringComparison.OrdinalIgnoreCase);

    private static bool TryPostBrowserAppCommand(IntPtr targetHwnd, int appCommand)
    {
        var hwnd = targetHwnd != IntPtr.Zero ? targetHwnd : NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var lParam = (IntPtr)(appCommand << 16);
        var posted = NativeMethods.PostMessageW(hwnd, NativeMethods.WM_APPCOMMAND, hwnd, lParam);
        if (!posted)
        {
            Logger.Warn($"WM_APPCOMMAND post failed, hwnd=0x{unchecked((ulong)hwnd.ToInt64()):X}, Win32 Error={Marshal.GetLastWin32Error()}");
        }
        return posted;
    }

    public static uint SendKey(uint virtualKey, ModifierKeys modifiers)
    {
        var inputs = new List<NativeMethods.INPUT>(8);

        // 修饰键按下
        if (modifiers.HasFlag(ModifierKeys.Control)) inputs.Add(Key(VK_CONTROL, down: true));
        if (modifiers.HasFlag(ModifierKeys.Alt))     inputs.Add(Key(VK_MENU,    down: true));
        if (modifiers.HasFlag(ModifierKeys.Shift))   inputs.Add(Key(VK_SHIFT,   down: true));
        if (modifiers.HasFlag(ModifierKeys.Win))     inputs.Add(Key(VK_LWIN,    down: true));

        // 主键
        inputs.Add(Key((ushort)virtualKey, down: true));
        inputs.Add(Key((ushort)virtualKey, down: false));

        // 修饰键松开 —— 倒序释放
        if (modifiers.HasFlag(ModifierKeys.Win))     inputs.Add(Key(VK_LWIN,    down: false));
        if (modifiers.HasFlag(ModifierKeys.Shift))   inputs.Add(Key(VK_SHIFT,   down: false));
        if (modifiers.HasFlag(ModifierKeys.Alt))     inputs.Add(Key(VK_MENU,    down: false));
        if (modifiers.HasFlag(ModifierKeys.Control)) inputs.Add(Key(VK_CONTROL, down: false));

        var arr = inputs.ToArray();
        var sent = SendInputs(arr, $"shortcut {FormatShortcutForLog(virtualKey, modifiers)}");
        if (sent != arr.Length && sent > 0)
        {
            // 部分注入失败可能留下"卡住"的键(down 进去了、对应的 up 被挡)——
            // 症状是系统级 Ctrl/Alt 错乱。兜底:补发全部 KEYUP。
            // 若整次调用都被挡(sent=0,典型 UIPI),没有键被按下,无需清理。
            ReleaseKeys(virtualKey, modifiers);
        }
        return sent;
    }

    private static void ReleaseKeys(uint virtualKey, ModifierKeys modifiers)
    {
        var inputs = new List<NativeMethods.INPUT>(5)
        {
            Key((ushort)virtualKey, down: false),
        };
        if (modifiers.HasFlag(ModifierKeys.Win))     inputs.Add(Key(VK_LWIN,    down: false));
        if (modifiers.HasFlag(ModifierKeys.Shift))   inputs.Add(Key(VK_SHIFT,   down: false));
        if (modifiers.HasFlag(ModifierKeys.Alt))     inputs.Add(Key(VK_MENU,    down: false));
        if (modifiers.HasFlag(ModifierKeys.Control)) inputs.Add(Key(VK_CONTROL, down: false));
        SendInputs(inputs.ToArray(), "stuck-key cleanup");
    }

    private static NativeMethods.INPUT Key(ushort vk, bool down)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
        };
        input.U.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
            time = 0,
            dwExtraInfo = NativeMethods.SyntheticEventMarker,
        };
        // Arrow keys + insert/delete 等"扩展键"需要 EXTENDEDKEY 标记;给所有有此性质的键统一打上。
        if (IsExtendedKey(vk))
        {
            input.U.ki.dwFlags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        }
        return input;
    }

    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        // 方向键
        0x25 or 0x26 or 0x27 or 0x28 => true,
        // Insert / Delete / Home / End / PageUp / PageDown
        0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 => true,
        // Numpad Enter / Right Alt / Right Ctrl / Win 键也属于 extended,
        // 但发送通用快捷键时用左 Alt/Ctrl 即可,不展开。
        _ => false,
    };

    private static void SendMouseXButton(uint xButton)
    {
        var inputs = new[]
        {
            MouseXButton(xButton, down: true),
            MouseXButton(xButton, down: false),
        };
        SendInputs(inputs, $"mouse xbutton {xButton}");
    }

    private static NativeMethods.INPUT MouseXButton(uint xButton, bool down)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
        };
        input.U.mi = new NativeMethods.MOUSEINPUT
        {
            mouseData = xButton,
            dwFlags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP,
            dwExtraInfo = NativeMethods.SyntheticEventMarker,
        };
        return input;
    }

    /// <summary>合成一次完整的右键单击 —— 给"普通右键单击没触发手势"的场景回放用。</summary>
    public static void ReplayRightClick()
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.INPUTUNION
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dwFlags = NativeMethods.MOUSEEVENTF_RIGHTDOWN,
                        dwExtraInfo = NativeMethods.SyntheticEventMarker,
                    },
                },
            },
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.INPUTUNION
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dwFlags = NativeMethods.MOUSEEVENTF_RIGHTUP,
                        dwExtraInfo = NativeMethods.SyntheticEventMarker,
                    },
                },
            },
        };
        SendInputs(inputs, "right click replay");
    }

    private static uint SendInputs(NativeMethods.INPUT[] inputs, string context)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            Logger.Warn($"SendInput({context}) sent {sent}/{inputs.Length}, Win32 Error={Marshal.GetLastWin32Error()}");
        }
        return sent;
    }

    private static string FormatShortcutForLog(uint virtualKey, ModifierKeys modifiers)
        => $"{modifiers}+VK{virtualKey:X2}";
}
