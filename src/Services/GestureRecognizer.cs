using System.Windows;
using Velto.Models;

namespace Velto.Services;

/// <summary>
/// 方向序列识别器 —— 鼠标手势工具的标准做法(Opera / FireGestures / StrokesPlus / WGestures)。
///
/// 取代原来从 macOS 版移植的 <c>$1</c> 形状匹配。<c>$1</c> 比的是"整体形状相似度",
/// 方向相近的简单笔画归一化后点云高度重叠,判定边界薄、易误识别(Ctrl+Tab / Ctrl+T 互抢就是这个原因)。
/// 方向序列天然尺度无关、位置无关、方向敏感、抗抖动,结构上就不可能把 ↑ 和 ↓、→ 和 →↓ 搞混。
///
/// 流程:
///   1. 按弧长重采样到固定点数(消除画得快/慢导致的点疏密差异)
///   2. 每段量化到 8 个方向之一(→↘↓↙←↖↑↗)
///   3. 众数平滑,消除单段方向抖动
///   4. 游程编码合并连续同向,丢弃过短 run(拐角抖动)
///   5. 得到方向序列,再用"带方向感知替换代价的归一化编辑距离"比对
///
/// 距离语义:0 = 序列完全一致,1 = 完全不同。识别阈值(默认 0.34)= 允许序列里多大比例不一致。
/// 相邻方向(45°)替换代价 0.25 → 单方向手势容许 ±45° 的画歪,但 90° 以上必然判为不同手势。
///
/// 调用者只在 hook 线程 / UI 线程访问,无锁(缓存按 version 失效)。
/// </summary>
public sealed class GestureRecognizer
{
    private const int    ResampleCount     = 64;
    private const double MinimumPathLength = 24;

    /// <summary>众数平滑窗口(奇数)。消除单段方向抖动。</summary>
    private const int SmoothingWindow = 3;

    /// <summary>一个方向 run 至少占这么多段,否则当拐角抖动丢弃。63 段里 4 段 ≈ 6%。</summary>
    private const int MinRunSegments = 4;

    // runner-up 安全间隔:最优与次优太接近就拒绝,避免模棱两可时乱触发。
    private const double MinimumCommandScoreGap  = 0.05;
    private const double RelativeCommandScoreGap = 0.20;

    private ulong _cachedVersion;
    private List<TemplateEntry> _cachedTemplates = new();

    public sealed record Match(GestureCommand Command, double Distance, double? RunnerUpDistance = null);

    private sealed record TemplateEntry(GestureCommand Command, int[] Sequence);

    public Match? BestCandidate(IReadOnlyList<Point> points, IReadOnlyList<GestureCommand> commands, ulong version)
    {
        var candidate = BuildSequence(points);
        if (candidate is null || candidate.Length == 0)
        {
            return null;
        }

        // 每个命令取它所有样本里最接近的那个距离
        var bestByCommand = new Dictionary<Guid, double>();
        var commandRef = new Dictionary<Guid, GestureCommand>();
        foreach (var template in NormalizedTemplates(commands, version))
        {
            if (template.Sequence.Length == 0) continue;
            var d = SequenceDistance(candidate, template.Sequence);
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

    /// <summary>调试用:把笔画转成可读方向串,如 "→ ↓"。日志里看误识别非常直观。</summary>
    public string DescribeSequence(IReadOnlyList<Point> points)
    {
        var seq = BuildSequence(points);
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
                var seq = BuildSequence(pts);
                if (seq is not null && seq.Length > 0)
                {
                    list.Add(new TemplateEntry(command, seq));
                }
            }
        }

        _cachedVersion = version;
        _cachedTemplates = list;
        return list;
    }

    // ───────────────────────── 序列构建 ─────────────────────────

    private static int[]? BuildSequence(IReadOnlyList<Point> points)
    {
        if (points.Count < 2) return null;
        var pathLen = PathLength(points);
        if (pathLen < MinimumPathLength) return null;

        var resampled = Resample(points, ResampleCount, pathLen);
        if (resampled.Length < 2) return null;

        // 1. 逐段量化方向
        var dirs = new int[resampled.Length - 1];
        for (int i = 0; i < dirs.Length; i++)
        {
            dirs[i] = Quantize8(resampled[i + 1].X - resampled[i].X, resampled[i + 1].Y - resampled[i].Y);
        }

        // 2. 众数平滑去抖
        Smooth(dirs, SmoothingWindow);

        // 3. 游程编码
        var runs = RunLengthEncode(dirs);

        // 4. 丢弃过短 run(拐角抖动),丢完相邻同向再合并
        var filtered = new List<(int dir, int count)>();
        foreach (var run in runs)
        {
            if (run.count < MinRunSegments && runs.Count > 1)
            {
                continue;
            }
            if (filtered.Count > 0 && filtered[^1].dir == run.dir)
            {
                filtered[^1] = (run.dir, filtered[^1].count + run.count);
            }
            else
            {
                filtered.Add(run);
            }
        }

        // 全被当噪声丢光(极短/全程抖动)→ 用最长 run 兜底,至少给个主方向
        if (filtered.Count == 0)
        {
            var longest = runs[0];
            foreach (var r in runs)
            {
                if (r.count > longest.count) longest = r;
            }
            filtered.Add(longest);
        }

        // 5. 输出方向序列(再 collapse 一次相邻同向以防万一)
        var result = new List<int>(filtered.Count);
        foreach (var r in filtered)
        {
            if (result.Count > 0 && result[^1] == r.dir) continue;
            result.Add(r.dir);
        }
        return result.ToArray();
    }

    /// <summary>众数滤波:每个位置取窗口内出现最多的方向,平局保留中心方向。消除孤立方向翻转。</summary>
    private static void Smooth(int[] dirs, int window)
    {
        if (dirs.Length < 3 || window < 3) return;
        var half = window / 2;
        var copy = (int[])dirs.Clone();
        Span<int> counts = stackalloc int[8];
        for (int i = 0; i < dirs.Length; i++)
        {
            counts.Clear();
            int lo = Math.Max(0, i - half);
            int hi = Math.Min(dirs.Length - 1, i + half);
            for (int j = lo; j <= hi; j++) counts[copy[j]]++;

            int best = copy[i];
            int bestCount = counts[copy[i]];
            for (int d = 0; d < 8; d++)
            {
                if (counts[d] > bestCount) { bestCount = counts[d]; best = d; }
            }
            dirs[i] = best;
        }
    }

    private static List<(int dir, int count)> RunLengthEncode(int[] dirs)
    {
        var runs = new List<(int, int)>();
        if (dirs.Length == 0) return runs;
        int cur = dirs[0], count = 1;
        for (int i = 1; i < dirs.Length; i++)
        {
            if (dirs[i] == cur) { count++; }
            else { runs.Add((cur, count)); cur = dirs[i]; count = 1; }
        }
        runs.Add((cur, count));
        return runs;
    }

    /// <summary>8 方向量化。Y 轴向下(屏幕/画布坐标),候选与模板同一约定。</summary>
    private static int Quantize8(double dx, double dy)
    {
        var angle = Math.Atan2(dy, dx); // -π..π
        var idx = (int)Math.Round(angle / (Math.PI / 4.0));
        return ((idx % 8) + 8) % 8;
    }

    /// <summary>编辑距离(带方向感知替换代价),按较长序列长度归一化到 [0,1]。</summary>
    private static double SequenceDistance(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 && m == 0) return 0;
        if (n == 0 || m == 0) return 1;

        var dp = new double[n + 1, m + 1];
        for (int i = 0; i <= n; i++) dp[i, 0] = i; // 删除,每个 1
        for (int j = 0; j <= m; j++) dp[0, j] = j; // 插入,每个 1

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                var sub = dp[i - 1, j - 1] + SubstitutionCost(a[i - 1], b[j - 1]);
                var del = dp[i - 1, j] + 1;
                var ins = dp[i, j - 1] + 1;
                dp[i, j] = Math.Min(sub, Math.Min(del, ins));
            }
        }
        return dp[n, m] / Math.Max(n, m);
    }

    /// <summary>替换代价:同向 0,相邻 45° 便宜(0.25),相反 180° 最贵(1.0)。</summary>
    private static double SubstitutionCost(int a, int b)
    {
        if (a == b) return 0;
        int diff = Math.Abs(a - b);
        int circular = Math.Min(diff, 8 - diff); // 0..4 个 45° 步
        return circular / 4.0;
    }

    private static string DirectionGlyph(int d) => d switch
    {
        0 => "→", 1 => "↘", 2 => "↓", 3 => "↙",
        4 => "←", 5 => "↖", 6 => "↑", 7 => "↗",
        _ => "?",
    };

    // ───────────────────────── 几何工具(沿用) ─────────────────────────

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
