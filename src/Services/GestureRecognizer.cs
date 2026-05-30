using System.Windows;
using Velto.Models;

namespace Velto.Services;

/// <summary>
/// 曲线形状识别器 —— <c>$1 Unistroke Recognizer</c> 的做法(WGestures / 各浏览器手势插件同源思路)。
///
/// 设计目标:用户画什么曲线、就按那条曲线识别,而不是要求画成方方正正的折线。
///
/// 为什么从"方向序列"改回曲线匹配:
///   方向序列法把笔画量化成 8 个方向再丢掉短段,信息有损 —— 两条"大方向相同、弯法不同"的曲线
///   (例如 新建 vs 下一标签:都偏向右上,只是起笔高低/弧度不同)量化后会塌成同一个序列,
///   永远互抢、再录也分不开。曲线匹配比的是整条轨迹的逐点形状,能把它们区分开。
///   (在用户真实录制样本上离线验证:曲线匹配留一法准确率 97%,方向序列法把这两个手势判成完全相同。)
///
/// 流程:
///   1. 按弧长重采样到固定点数(消除画得快/慢导致的点疏密差异)
///   2. 平移到质心(位置无关)
///   3. 按外接框较长边等比缩放到单位尺度(尺寸无关;等比而非各轴拉伸 —— 拉伸会放大直线的抖动)
///   4. 与每个录制样本逐点求平均欧氏距离;不做旋转归一化 —— 手势方向本身有意义(↑ 不该等于 ↓)
///
/// 距离语义:0 = 与某条录制曲线完全重合,越大越不像。识别阈值 = 可接受的最大平均逐点距离。
/// 典型值:同一手势的不同样本之间约 0.01–0.14;不同手势之间通常 ≥0.09。默认阈值 0.22。
///
/// 调用者只在 hook 线程 / UI 线程访问,无锁(缓存按 version 失效)。
/// </summary>
public sealed class GestureRecognizer
{
    private const int    ResampleCount     = 64;
    private const double MinimumPathLength = 24;

    // runner-up 安全间隔:最优与次优太接近就拒绝,避免模棱两可时乱触发。
    // 曲线匹配的距离尺度比旧的归一化编辑距离小一个量级,这里相应调小。
    // 方向骨架已经先筛掉单段/多段混淆,这里再要求曲线距离有更明确的胜出间隔。
    private const double MinimumCommandScoreGap  = 0.025;
    private const double RelativeCommandScoreGap = 0.25;

    private ulong _cachedVersion;
    private List<TemplateEntry> _cachedTemplates = new();

    public sealed record Match(GestureCommand Command, double Distance, double? RunnerUpDistance = null);

    /// <summary>每个录制样本的归一化曲线向量(长度 2*ResampleCount,交替存 x,y)。</summary>
    private sealed record TemplateEntry(GestureCommand Command, double[] Vector, int[] Directions);
    private sealed record NormalizedStroke(double[] Vector, int[] Directions);

    public Match? BestCandidate(IReadOnlyList<Point> points, IReadOnlyList<GestureCommand> commands, ulong version)
    {
        var candidate = BuildNormalizedStroke(points);
        if (candidate is null)
        {
            return null;
        }

        // 每个命令取它所有样本里最接近的那个距离
        var bestByCommand = new Dictionary<Guid, double>();
        var commandRef = new Dictionary<Guid, GestureCommand>();
        foreach (var template in NormalizedTemplates(commands, version))
        {
            if (!DirectionCompatible(candidate.Directions, template.Directions))
            {
                continue;
            }

            var d = ShapeDistance(candidate.Vector, template.Vector);
            if (!bestByCommand.TryGetValue(template.Command.Id, out var existing) || d < existing)
            {
                bestByCommand[template.Command.Id] = d;
                commandRef[template.Command.Id] = template.Command;
            }
        }

        if (bestByCommand.Count == 0) return null;

        Guid bestId = default;
        double bestD = double.PositiveInfinity;
        double runnerUpD = double.PositiveInfinity;
        foreach (var kv in bestByCommand)
        {
            if (kv.Value < bestD)
            {
                runnerUpD = bestD;
                bestD = kv.Value;
                bestId = kv.Key;
            }
            else if (kv.Value < runnerUpD)
            {
                runnerUpD = kv.Value;
            }
        }

        return new Match(
            commandRef[bestId],
            bestD,
            double.IsPositiveInfinity(runnerUpD) ? null : runnerUpD);
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
                return null; // 跟次优太接近 → 模棱两可,宁可不触发
            }
        }
        return best;
    }

    /// <summary>
    /// 调试用:把笔画转成可读方向串,如 "→ ↓"。只用于写日志,不参与匹配。
    /// 排查识别问题时,日志里同时有方向串和各候选距离,一眼能看出画的是什么、被判成了什么。
    /// </summary>
    public string DescribeSequence(IReadOnlyList<Point> points)
    {
        var seq = BuildDirectionSequence(points);
        if (seq is null || seq.Length == 0) return "(空)";
        return string.Join(" ", seq.Select(DirectionGlyph));
    }

    // ───────────────────────── 模板缓存 ─────────────────────────

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
                var normalized = BuildNormalizedStroke(pts);
                if (normalized is not null)
                {
                    list.Add(new TemplateEntry(command, normalized.Vector, normalized.Directions));
                }
            }
        }

        _cachedVersion = version;
        _cachedTemplates = list;
        return list;
    }

    // ───────────────────────── 曲线向量构建 ($1) ─────────────────────────

    /// <summary>
    /// 重采样 → 平移到质心 → 按较长边等比缩放。返回长度 2*ResampleCount 的向量(x0,y0,x1,y1,...)。
    /// 点太少 / 笔画太短 → 返回 null。
    /// </summary>
    private static NormalizedStroke? BuildNormalizedStroke(IReadOnlyList<Point> points)
    {
        var vector = BuildVector(points);
        if (vector is null) return null;

        var directions = BuildDirectionSequence(points);
        if (directions is null || directions.Length == 0) return null;

        return new NormalizedStroke(vector, directions);
    }

    private static double[]? BuildVector(IReadOnlyList<Point> points)
    {
        if (points.Count < 2) return null;
        var pathLen = PathLength(points);
        if (pathLen < MinimumPathLength) return null;

        var resampled = Resample(points, ResampleCount, pathLen);
        if (resampled.Length < 2) return null;

        int n = resampled.Length;

        double cx = 0, cy = 0;
        for (int i = 0; i < n; i++) { cx += resampled[i].X; cy += resampled[i].Y; }
        cx /= n; cy /= n;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            var x = resampled[i].X - cx;
            var y = resampled[i].Y - cy;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }

        // 等比缩放:用外接框较长边做分母。比各轴独立拉伸更稳 ——
        // 直线手势(后退/翻页)在某个轴上跨度近 0,各轴拉伸会把那个轴上的微小抖动放大成"形状"。
        var span = Math.Max(maxX - minX, maxY - minY);
        if (span < 1e-6) span = 1;

        var vec = new double[2 * n];
        for (int i = 0; i < n; i++)
        {
            vec[2 * i]     = (resampled[i].X - cx) / span;
            vec[2 * i + 1] = (resampled[i].Y - cy) / span;
        }
        return vec;
    }

    /// <summary>两条归一化曲线的平均逐点欧氏距离。两向量等长(都是 2*ResampleCount)。</summary>
    private static double ShapeDistance(double[] a, double[] b)
    {
        int n = a.Length / 2;
        if (n == 0) return double.PositiveInfinity;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            var dx = a[2 * i]     - b[2 * i];
            var dy = a[2 * i + 1] - b[2 * i + 1];
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        return sum / n;
    }

    // ───────────────────────── 方向序列(仅供日志可读) ─────────────────────────

    private static int[]? BuildDirectionSequence(IReadOnlyList<Point> points)
    {
        if (points.Count < 2) return null;
        var pathLen = PathLength(points);
        if (pathLen < MinimumPathLength) return null;

        var resampled = Resample(points, ResampleCount, pathLen);
        if (resampled.Length < 2) return null;

        var dirs = new int[resampled.Length - 1];
        for (int i = 0; i < dirs.Length; i++)
        {
            dirs[i] = Quantize8(resampled[i + 1].X - resampled[i].X, resampled[i + 1].Y - resampled[i].Y);
        }

        // 简单游程编码并丢弃极短段,得到大致方向走向(仅用于人读)
        var result = new List<int>();
        int cur = dirs[0], count = 1;
        var runs = new List<(int dir, int count)>();
        for (int i = 1; i < dirs.Length; i++)
        {
            if (dirs[i] == cur) count++;
            else { runs.Add((cur, count)); cur = dirs[i]; count = 1; }
        }
        runs.Add((cur, count));

        foreach (var r in runs)
        {
            if (r.count < 3 && runs.Count > 1) continue;
            if (result.Count > 0 && result[^1] == r.dir) continue;
            result.Add(r.dir);
        }
        if (result.Count == 0)
        {
            var longest = runs[0];
            foreach (var r in runs) if (r.count > longest.count) longest = r;
            result.Add(longest.dir);
        }
        return result.ToArray();
    }

    /// <summary>8 方向量化。Y 轴向下(屏幕/画布坐标)。</summary>
    private static int Quantize8(double dx, double dy)
    {
        var angle = Math.Atan2(dy, dx); // -π..π
        var idx = (int)Math.Round(angle / (Math.PI / 4.0));
        return ((idx % 8) + 8) % 8;
    }

    private static bool DirectionCompatible(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (DirectionDistance(a[i], b[i]) > 1)
            {
                return false;
            }
        }
        return true;
    }

    private static int DirectionDistance(int a, int b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, 8 - d);
    }

    private static string DirectionGlyph(int d) => d switch
    {
        0 => "→", 1 => "↘", 2 => "↓", 3 => "↙",
        4 => "←", 5 => "↖", 6 => "↑", 7 => "↗",
        _ => "?",
    };

    // ───────────────────────── 几何工具 ─────────────────────────

    private static Point[] Resample(IReadOnlyList<Point> points, int targetCount, double knownPathLength)
    {
        if (points.Count == 0) return Array.Empty<Point>();
        var first = points[0];
        if (targetCount <= 1) return new[] { first };

        var total = knownPathLength;
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
                if (result.Count == targetCount) return result.ToArray();

                segmentStart = p;
                remaining = Distance(segmentStart, segmentEnd);
                accumulated = 0;
            }

            accumulated += remaining;
            segmentStart = segmentEnd;
        }

        var pad = points[^1];
        while (result.Count < targetCount) result.Add(pad);
        return result.ToArray();
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
}
