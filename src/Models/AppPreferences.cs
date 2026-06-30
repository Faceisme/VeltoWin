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
    // 默认关闭轨迹反馈:绘制轨迹要在每个 mousemove 上跨线程推数据,关掉后
    // 钩子线程的热路径只剩记录点(便宜)。需要可视反馈的用户可在设置里打开。
    public bool ShowTrail { get; set; } = false;
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// 识别阈值 = 可接受的最大签名差异度。值越小越严格。
    /// 签名匹配的距离约为 0..1:方向/弧向完全一致为 0,默认 0.34 与 macOS 版一致。
    /// </summary>
    public double RecognitionThreshold { get; set; } = 0.34;

    /// <summary>手势超时秒数。绘制中停下不动超过该时间 → 取消当前手势(松开不触发)。默认 3s。</summary>
    public double GestureTimeoutSeconds { get; set; } = 3.0;

    /// <summary>启用后,来回乱画或转圈会立即取消当前手势。</summary>
    public bool ScribbleCancelEnabled { get; set; } = true;

    /// <summary>
    /// 识别到全屏应用(游戏、全屏视频等)位于前台时暂停手势,退出全屏后自动恢复。
    /// 全屏应用通常自己要用右键,暂停可避免手势抢占右键带来的延迟与误触。
    /// </summary>
    public bool PauseInFullscreen { get; set; } = true;

    public GestureTargetPolicy GestureTargetPolicy { get; set; } = GestureTargetPolicy.WindowUnderPointer;

    public static AppPreferences Default => new();
}
