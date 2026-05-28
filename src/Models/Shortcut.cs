using System.Text.Json.Serialization;

namespace Velto.Models;

/// <summary>
/// Win 版本的快捷键模型 —— 用 Win32 Virtual-Key 编码 + 修饰键位集合表示。
/// 字段含义与 macOS 版本的 <c>Shortcut</c> 平行,但不再共享数值(VK 跟 macOS keyCode 不同表)。
/// </summary>
public sealed class Shortcut : IEquatable<Shortcut>
{
    [JsonInclude]
    public uint VirtualKey { get; init; }

    [JsonInclude]
    public ModifierKeys Modifiers { get; init; }

    [JsonInclude]
    public string DisplayName { get; init; } = string.Empty;

    public Shortcut() { }

    public Shortcut(uint virtualKey, ModifierKeys modifiers, string displayName)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        DisplayName = displayName;
    }

    public bool Equals(Shortcut? other)
        => other is not null
        && VirtualKey == other.VirtualKey
        && Modifiers == other.Modifiers;

    public override bool Equals(object? obj) => Equals(obj as Shortcut);

    public override int GetHashCode() => HashCode.Combine(VirtualKey, (int)Modifiers);
}

[Flags]
public enum ModifierKeys
{
    None    = 0,
    Control = 1 << 0,
    Alt     = 1 << 1,
    Shift   = 1 << 2,
    Win     = 1 << 3,
}
