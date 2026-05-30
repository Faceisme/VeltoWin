namespace Velto.Services;

/// <summary>
/// 全局"手势暂停闸门"。设置窗口激活时置为 <c>true</c>,让 <see cref="GestureEngine"/>
/// 把右键事件整体放行(不吞、不识别)。
///
/// 为什么需要它:录制画布改用「右键」拖动后,如果全局低层钩子照常吞掉右键,
/// WPF 根本收不到 MouseRightButtonDown,画布就没法录。设置窗口处于活动状态时,
/// 用户是在配置而不是在别处比划手势,这时把手势整体让路最干净:
/// 既让画布能拿到右键拖动,也顺带让设置窗口里的右键菜单(输入框粘贴等)正常工作。
///
/// volatile 单字段:hook 线程无锁读,UI 线程写。
/// </summary>
public static class GestureGate
{
    private static volatile bool _suspended;

    /// <summary>true = 暂停手势识别,所有鼠标事件放行给系统/WPF。</summary>
    public static bool Suspended
    {
        get => _suspended;
        set => _suspended = value;
    }
}
