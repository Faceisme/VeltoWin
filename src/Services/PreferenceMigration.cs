using Velto.Models;

namespace Velto.Services;

/// <summary>
/// 偏好值的一次性迁移与每次加载的合法性校验。
///
/// 两者必须分开:迁移把"旧版算法时代的默认值"换成新默认,只能在 formatVersion
/// 升级时跑一次 —— 否则用户在新版滑条范围内主动设置的 0.18/0.22/0.5s 会在每次启动被误判
/// 为旧默认值而重置(修复前的真实 bug)。校验只兜"超出滑条范围"的异常数据(手改 JSON 等),
/// 每次加载都跑也不会碰合法值。
///
/// 独立成无状态静态类,单测可直接调用,不会触发 <see cref="ConfigStore"/> 的静态初始化
/// (那会去读真实 %APPDATA% 配置)。
/// </summary>
internal static class PreferenceMigration
{
    // 与 SettingsWindow.xaml 两个滑条的 Minimum/Maximum 保持一致。
    internal const double ThresholdMin = 0.05;
    internal const double ThresholdMax = 0.40;
    internal const double TimeoutMin = 0.5;
    internal const double TimeoutMax = 10;

    /// <summary>
    /// 识别算法从 "$1 曲线距离" 换成 "方向签名差异度" 后的一次性迁移:
    /// 旧默认阈值 0.18($1)/ 0.22(方向)→ 新默认 0.34;旧的短超时(&lt; 1s,典型 0.6s)→ 新默认 3s。
    /// 仅对 formatVersion 早于当前版本的配置 / 备份调用。
    /// </summary>
    internal static void MigrateLegacy(AppPreferences prefs)
    {
        const double oldShapeDefault = 0.18;
        const double oldDirectionDefault = 0.22;
        if (Math.Abs(prefs.RecognitionThreshold - oldShapeDefault) < 0.0001 ||
            Math.Abs(prefs.RecognitionThreshold - oldDirectionDefault) < 0.0001)
        {
            Logger.Info($"识别阈值 {prefs.RecognitionThreshold:0.00} 为旧版默认值,迁移为新默认 {AppPreferences.Default.RecognitionThreshold:0.00}");
            prefs.RecognitionThreshold = AppPreferences.Default.RecognitionThreshold;
        }

        if (prefs.GestureTimeoutSeconds < 1.0)
        {
            Logger.Info($"手势超时 {prefs.GestureTimeoutSeconds:0.0}s 来自旧版配置,迁移为默认 {AppPreferences.Default.GestureTimeoutSeconds:0.0}s");
            prefs.GestureTimeoutSeconds = AppPreferences.Default.GestureTimeoutSeconds;
        }
    }

    /// <summary>超出设置界面滑条范围的值重置为默认。范围内的值原样保留。</summary>
    internal static void Validate(AppPreferences prefs)
    {
        if (prefs.RecognitionThreshold < ThresholdMin || prefs.RecognitionThreshold > ThresholdMax)
        {
            Logger.Info($"识别阈值 {prefs.RecognitionThreshold:0.00} 超出范围 [{ThresholdMin:0.00}, {ThresholdMax:0.00}],重置为默认 {AppPreferences.Default.RecognitionThreshold:0.00}");
            prefs.RecognitionThreshold = AppPreferences.Default.RecognitionThreshold;
        }

        if (prefs.GestureTimeoutSeconds < TimeoutMin || prefs.GestureTimeoutSeconds > TimeoutMax)
        {
            Logger.Info($"手势超时 {prefs.GestureTimeoutSeconds:0.0}s 超出范围 [{TimeoutMin:0.#}, {TimeoutMax:0.#}]s,重置为默认 {AppPreferences.Default.GestureTimeoutSeconds:0.0}s");
            prefs.GestureTimeoutSeconds = AppPreferences.Default.GestureTimeoutSeconds;
        }
    }
}
