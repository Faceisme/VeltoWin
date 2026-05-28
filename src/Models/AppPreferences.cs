namespace Velto.Models;

public enum GestureTargetPolicy
{
    /// <summary>把动作发送到鼠标按下时光标所在的窗口。</summary>
    WindowUnderPointer,

    /// <summary>把动作发送到当前活动窗口(不切前台)。</summary>
    ActiveWindow,
}

/// <summary>
/// 全局偏好。字段与 macOS 版的 <c>AppPreferences</c> 一一对应,
/// 但只保留鼠标手势相关的子集 —— 窗口管理 / 切换器都不做。
/// </summary>
public sealed class AppPreferences
{
    public bool GesturesEnabled { get; set; } = true;
    public bool ShowTrail { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>识别阈值。值越小越严格。macOS 版默认 0.34。</summary>
    public double RecognitionThreshold { get; set; } = 0.34;

    /// <summary>手势超时秒数。绘制中超过该时间静止 → 取消。</summary>
    public double GestureTimeoutSeconds { get; set; } = 3.0;

    public GestureTargetPolicy GestureTargetPolicy { get; set; } = GestureTargetPolicy.WindowUnderPointer;

    public static AppPreferences Default => new();
}
