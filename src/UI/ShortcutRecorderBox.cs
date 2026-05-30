using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Velto.Models;
using ModifierKeysWin = Velto.Models.ModifierKeys;

namespace Velto.UI;

/// <summary>
/// 可聚焦控件 —— 点一下"录入快捷键",任意按键组合就会被捕获并存为 <see cref="Shortcut"/>。
/// 单纯修饰键(Ctrl/Alt/Shift/Win)不算"完整快捷键",需要至少一个主键。
/// </summary>
public sealed class ShortcutRecorderBox : Border
{
    private readonly TextBlock _label;
    private bool _capturing;

    public event Action<Shortcut>? ShortcutRecorded;

    public Shortcut? Value
    {
        get => (Shortcut?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(Shortcut), typeof(ShortcutRecorderBox),
            new PropertyMetadata(null, OnValueChanged));

    public ShortcutRecorderBox()
    {
        // 走主题资源,深浅色自动适配
        SetResourceReference(BackgroundProperty, "TextControlBackground");
        SetResourceReference(BorderBrushProperty, "TextControlBorderBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(10, 7, 10, 7);
        MinHeight = 32;
        Focusable = true;
        Cursor = Cursors.Hand;

        _label = new TextBlock { Text = "(点此录入)", VerticalAlignment = VerticalAlignment.Center };
        Child = _label;

        MouseLeftButtonDown += (_, e) => { Focus(); e.Handled = true; };
        GotKeyboardFocus += (_, _) =>
        {
            _capturing = true;
            SetResourceReference(BorderBrushProperty, "SystemControlHighlightAccentBrush");
            UpdateLabel();
        };
        LostKeyboardFocus += (_, _) =>
        {
            _capturing = false;
            SetResourceReference(BorderBrushProperty, "TextControlBorderBrush");
            UpdateLabel();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        UpdateLabel();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShortcutRecorderBox box) box.UpdateLabel();
    }

    private void UpdateLabel()
    {
        if (_capturing)
        {
            _label.Text = "请按下快捷键 (Esc 清空)";
            _label.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlHighlightAccentBrush");
            return;
        }
        if (Value is null)
        {
            _label.Text = "(未设置)";
            _label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        else
        {
            _label.Text = Value.DisplayName;
            _label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            Value = null;
            ShortcutRecorded?.Invoke(new Shortcut(0, ModifierKeysWin.None, ""));
            Keyboard.ClearFocus();
            return;
        }

        // 拿到真实按键 — System.Key 在按下修饰键时给的就是修饰键本身,过滤掉
        var key = (e.Key == Key.System) ? e.SystemKey : e.Key;
        if (IsModifierOnly(key)) return;

        var mods = ModifierKeysWin.None;
        var k = Keyboard.Modifiers;
        if (k.HasFlag(System.Windows.Input.ModifierKeys.Control)) mods |= ModifierKeysWin.Control;
        if (k.HasFlag(System.Windows.Input.ModifierKeys.Alt))     mods |= ModifierKeysWin.Alt;
        if (k.HasFlag(System.Windows.Input.ModifierKeys.Shift))   mods |= ModifierKeysWin.Shift;
        if (k.HasFlag(System.Windows.Input.ModifierKeys.Windows)) mods |= ModifierKeysWin.Win;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var display = FormatDisplay(mods, key, vk);
        var sc = new Shortcut(vk, mods, display);
        Value = sc;
        ShortcutRecorded?.Invoke(sc);
        Keyboard.ClearFocus();
    }

    private static bool IsModifierOnly(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System or Key.None;

    private static string FormatDisplay(ModifierKeysWin mods, Key key, uint vk)
    {
        var sb = new StringBuilder();
        if (mods.HasFlag(ModifierKeysWin.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeysWin.Alt))     sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeysWin.Shift))   sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeysWin.Win))     sb.Append("Win+");
        sb.Append(FriendlyKeyName(key, vk));
        return sb.ToString();
    }

    private static string FriendlyKeyName(Key key, uint vk) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Space => "空格",
        Key.Enter => "回车",
        Key.Tab => "Tab",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemBackslash or Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ when key >= Key.D0 && key <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ when key >= Key.A && key <= Key.Z   => ((char)('A' + (key - Key.A))).ToString(),
        _ when key >= Key.F1 && key <= Key.F24 => $"F{(int)(key - Key.F1) + 1}",
        _ => $"VK{vk:X2}",
    };
}
