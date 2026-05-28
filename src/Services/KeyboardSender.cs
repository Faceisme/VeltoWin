using Velto.Models;
using Velto.Win32;

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

    public static void Send(Shortcut shortcut)
        => SendKey(shortcut.VirtualKey, shortcut.Modifiers);

    public static void SendKey(uint virtualKey, ModifierKeys modifiers)
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
        NativeMethods.SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
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
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
