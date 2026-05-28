using System.Windows;
using Velto.Models;

namespace Velto.Services;

/// <summary>
/// 移植自 macOS 版 <c>GestureRecognizer.swift</c>:重采样 + 缩放到单位框 + 平移到中心 + 平均欧氏距离匹配。
/// Windows 版额外校验起点到终点、起始段、结束段的方向,避免相似轨迹抢错命令。
///
/// 调用者只在 UI 线程访问,无锁。
/// </summary>
public sealed class GestureRecognizer
{
    private const int    SampleCount       = 64;
    private const double MinimumPathLength = 24;
    private const double StraightGestureThreshold = 0.92;
    private const double CurvedGestureThreshold = 0.84;
    private const double MaxStraightGestureAngle = Math.PI / 6.0;
    private const double MaxEndpointAngle = Math.PI * 0.44;
    private const double MaxStartAngle = Math.PI * 0.40;
    private const double MaxEndAngle = Math.PI * 0.40;
    private const double MinimumCommandScoreGap = 0.045;
    private const double RelativeCommandScoreGap = 0.18;

    private ulong _cachedVersion;
    private List<TemplateEntry> _cachedTemplates = new();

    public sealed record Match(GestureCommand Command, double Distance, double? RunnerUpDistance = null);

    private sealed record TemplateEntry(GestureCommand Command, Point[] Points, GestureFeatures Features);

    private readonly record struct GestureFeatures(
        Vector Direction,
        Vector StartDirection,
        Vector EndDirection,
        double Straightness,
        double AspectRatio);

    public Match? BestCandidate(IReadOnlyList<Point> points, IReadOnlyList<GestureCommand> commands, ulong version)
    {
        var pathLen = PathLength(points);
        if (pathLen < MinimumPathLength)
        {
            return null;
        }

        var candidate = Normalize(points, pathLen);
        if (candidate is null)
        {
            return null;
        }

        var candidateFeatures = ExtractFeatures(candidate);
        var bestByCommand = new Dictionary<Guid, Match>();
        foreach (var template in NormalizedTemplates(commands, version))
        {
            var command = template.Command;
            var d = AdjustedDistance(candidate, template.Points, candidateFeatures, template.Features);
            if (double.IsPositiveInfinity(d))
            {
                continue;
            }

            if (!bestByCommand.TryGetValue(command.Id, out var existing) || d < existing.Distance)
            {
                bestByCommand[command.Id] = new Match(command, d);
            }
        }

        Match? best = null;
        Match? runnerUp = null;
        foreach (var match in bestByCommand.Values)
        {
            if (best is null || match.Distance < best.Distance)
            {
                runnerUp = best;
                best = match;
            }
            else if (runnerUp is null || match.Distance < runnerUp.Distance)
            {
                runnerUp = match;
            }
        }

        return best is null
            ? null
            : best with { RunnerUpDistance = runnerUp?.Distance };
    }

    public Match? BestMatch(
        IReadOnlyList<Point> points,
        IReadOnlyList<GestureCommand> commands,
        ulong version,
        double threshold)
    {
        var best = BestCandidate(points, commands, version);
        if (best is null || best.Distance > threshold)
        {
            return null;
        }
        if (best.RunnerUpDistance is { } runnerUp)
        {
            var requiredGap = Math.Max(MinimumCommandScoreGap, best.Distance * RelativeCommandScoreGap);
            if (runnerUp - best.Distance < requiredGap)
            {
                return null;
            }
        }
        return best;
    }

    /// <summary>外部调用 —— 比如设置 UI 想画出"识别归一化后"的样子时用。</summary>
    public Point[]? Normalize(IReadOnlyList<Point> points)
        => Normalize(points, null);

    private Point[]? Normalize(IReadOnlyList<Point> points, double? knownPathLength)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var resampled = Resample(points, SampleCount, knownPathLength);
        var scaled = ScaleToUnitBox(resampled);
        if (scaled is null)
        {
            return null;
        }

        TranslateToOrigin(scaled);
        return scaled;
    }

    private List<TemplateEntry> NormalizedTemplates(IReadOnlyList<GestureCommand> commands, ulong version)
    {
        if (_cachedVersion == version && _cachedTemplates.Count > 0)
        {
            return _cachedTemplates;
        }

        var list = new List<TemplateEntry>();
        foreach (var command in commands)
        {
            foreach (var template in command.Templates)
            {
                if (template.Count < 2) continue;
                var pts = new Point[template.Count];
                for (int i = 0; i < template.Count; i++)
                {
                    pts[i] = new Point(template[i].X, template[i].Y);
                }
                var normalized = Normalize(pts, null);
                if (normalized is not null)
                {
                    list.Add(new TemplateEntry(command, normalized, ExtractFeatures(normalized)));
                }
            }
        }

        _cachedVersion = version;
        _cachedTemplates = list;
        return list;
    }

    private static Point[] Resample(IReadOnlyList<Point> points, int targetCount, double? knownPathLength)
    {
        if (points.Count == 0) return Array.Empty<Point>();
        var first = points[0];
        if (targetCount <= 1) return new[] { first };

        var total = knownPathLength ?? PathLength(points);
        if (total <= 0)
        {
            var filled = new Point[targetCount];
            for (int i = 0; i < targetCount; i++) filled[i] = first;
            return filled;
        }

        var interval = total / (targetCount - 1);
        var result = new List<Point>(targetCount) { first };
        var accumulated = 0.0;
        var segmentStart = first;

        for (int i = 1; i < points.Count; i++)
        {
            var segmentEnd = points[i];
            var remaining = Distance(segmentStart, segmentEnd);

            while (remaining > 0 && accumulated + remaining >= interval)
            {
                var needed = interval - accumulated;
                var ratio = needed / remaining;
                var p = new Point(
                    segmentStart.X + ratio * (segmentEnd.X - segmentStart.X),
                    segmentStart.Y + ratio * (segmentEnd.Y - segmentStart.Y));
                result.Add(p);
                if (result.Count == targetCount)
                {
                    return result.ToArray();
                }

                segmentStart = p;
                remaining = Distance(segmentStart, segmentEnd);
                accumulated = 0;
            }

            accumulated += remaining;
            segmentStart = segmentEnd;
        }

        // 浮点累积误差可能导致 result 没填满,用最后一点补齐 —— 与 macOS 版一致。
        var pad = points[^1];
        while (result.Count < targetCount)
        {
            result.Add(pad);
        }
        return result.ToArray();
    }

    private static Point[]? ScaleToUnitBox(Point[] points)
    {
        if (points.Length == 0) return null;

        double minX = points[0].X, maxX = points[0].X;
        double minY = points[0].Y, maxY = points[0].Y;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        var scale = Math.Max(maxX - minX, maxY - minY);
        if (scale < 0.0001) return null;

        var result = new Point[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            result[i] = new Point((points[i].X - minX) / scale, (points[i].Y - minY) / scale);
        }
        return result;
    }

    private static void TranslateToOrigin(Point[] points)
    {
        double sx = 0, sy = 0;
        foreach (var p in points) { sx += p.X; sy += p.Y; }
        var cx = sx / points.Length;
        var cy = sy / points.Length;
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Point(points[i].X - cx, points[i].Y - cy);
        }
    }

    private static double AverageDistance(Point[] left, Point[] right)
    {
        var count = Math.Min(left.Length, right.Length);
        if (count == 0) return double.MaxValue;
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            total += Distance(left[i], right[i]);
        }
        return total / count;
    }

    private static double PathLength(IReadOnlyList<Point> points)
    {
        if (points.Count < 2) return 0;
        double total = 0;
        for (int i = 1; i < points.Count; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }
        return total;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static GestureFeatures ExtractFeatures(Point[] points)
    {
        if (points.Length < 2)
        {
            return new GestureFeatures(default, default, default, 0, 1);
        }

        var direct = points[^1] - points[0];
        var direction = NormalizeVector(direct);
        var segmentLength = Math.Clamp(points.Length / 5, 4, 14);
        var startDirection = NormalizeVector(points[Math.Min(points.Length - 1, segmentLength)] - points[0]);
        var endDirection = NormalizeVector(points[^1] - points[Math.Max(0, points.Length - 1 - segmentLength)]);
        var pathLength = PathLength(points);
        var straightness = pathLength > 0.0001 ? Math.Min(1, direct.Length / pathLength) : 0;

        double minX = points[0].X, maxX = points[0].X;
        double minY = points[0].Y, maxY = points[0].Y;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        var width = Math.Max(0.001, maxX - minX);
        var height = Math.Max(0.001, maxY - minY);
        return new GestureFeatures(direction, startDirection, endDirection, straightness, width / height);
    }

    private static double AdjustedDistance(
        Point[] candidate,
        Point[] template,
        GestureFeatures candidateFeatures,
        GestureFeatures templateFeatures)
    {
        var endpointAngle = AngleBetween(candidateFeatures.Direction, templateFeatures.Direction);
        if (endpointAngle > MaxEndpointAngle)
        {
            return double.PositiveInfinity;
        }

        var startAngle = AngleBetween(candidateFeatures.StartDirection, templateFeatures.StartDirection);
        if (startAngle > MaxStartAngle)
        {
            return double.PositiveInfinity;
        }

        var endAngle = AngleBetween(candidateFeatures.EndDirection, templateFeatures.EndDirection);
        if (endAngle > MaxEndAngle)
        {
            return double.PositiveInfinity;
        }

        var candidateIsStraight = candidateFeatures.Straightness >= StraightGestureThreshold;
        var templateIsStraight = templateFeatures.Straightness >= StraightGestureThreshold;
        if (candidateIsStraight && templateIsStraight && endpointAngle > MaxStraightGestureAngle)
        {
            return double.PositiveInfinity;
        }

        var score = AverageDistance(candidate, template);
        score += endpointAngle / Math.PI * 0.20;
        score += startAngle / Math.PI * 0.18;
        score += endAngle / Math.PI * 0.18;

        if (candidateIsStraight != templateIsStraight)
        {
            var curved = candidateIsStraight ? templateFeatures.Straightness : candidateFeatures.Straightness;
            if (curved <= CurvedGestureThreshold)
            {
                score += 0.16;
            }
        }

        var aspectDelta = Math.Abs(Math.Log(candidateFeatures.AspectRatio) - Math.Log(templateFeatures.AspectRatio));
        score += Math.Min(aspectDelta, 1.5) * 0.04;
        return score;
    }

    private static Vector NormalizeVector(Vector vector)
    {
        return vector.Length > 0.0001 ? vector / vector.Length : default;
    }

    private static double AngleBetween(Vector a, Vector b)
    {
        if (a.Length < 0.0001 || b.Length < 0.0001)
        {
            return 0;
        }

        var dot = Math.Clamp((a.X * b.X + a.Y * b.Y) / (a.Length * b.Length), -1, 1);
        return Math.Acos(dot);
    }
}
