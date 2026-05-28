using System.Text.Json.Serialization;

namespace Velto.Models;

/// <summary>
/// 二维点。JSON 里直接序列化为 {"x":..., "y":...},跟 macOS 版的 StrokePoint 兼容。
/// </summary>
public readonly record struct StrokePoint(double X, double Y);

/// <summary>
/// 一条手势 = 名称 + 一组样本(每个样本是一串点) + 触发的快捷键。
/// 一个手势可以录入多个样本提升识别容错。
/// </summary>
public sealed class GestureCommand
{
    [JsonInclude]
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Templates of the same gesture. Each template is its own stroke sample.</summary>
    public List<List<StrokePoint>> Templates { get; set; } = new();

    public Shortcut? Shortcut { get; set; }
}
