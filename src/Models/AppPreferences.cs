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
    /// 识别阈值 = 可接受的最大平均逐点曲线距离。值越小越严格。
    /// 曲线匹配($1)的尺度:同手势样本间约 0.01–0.14,不同手势间通常 ≥0.09,故默认 0.18。
    /// (注意:这与旧"方向序列编辑距离"的阈值不是一个量级,旧配置会在加载时迁移。)
    /// </summary>
    public double RecognitionThreshold { get; set; } = 0.18;

    /// <summary>手势超时秒数。绘制中停下不动超过该时间 → 取消当前手势(松开不触发)。默认 0.5s。</summary>
    public double GestureTimeoutSeconds { get; set; } = 0.5;

    public GestureTargetPolicy GestureTargetPolicy { get; set; } = GestureTargetPolicy.WindowUnderPointer;

    public static AppPreferences Default => new();
}
