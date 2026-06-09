using System.Windows;

namespace Velto.Services;

/// <summary>
/// Reduces a mouse stroke to a direction signature: an 8-way direction sequence
/// plus a signed bow metric for single-segment curved gestures.
/// </summary>
public static class GestureDirection
{
    public readonly record struct Signature(int[] Sequence, double Bow)
    {
        public static Signature Empty { get; } = new(Array.Empty<int>(), 0);
        public bool IsEmpty => Sequence.Length == 0;
        public int BowSign => GestureDirection.BowSign(Bow);
    }

    private const int SampleCount = 64;
    private const double MinimumPathLength = 24;
    private const int CornerWindow = 6;
    private const double CornerAngleThreshold = 55 * Math.PI / 180;
    private const double BowSignThreshold = 0.025;
    private const double BowStrongThreshold = 0.06;
    private const double BowOppositePenalty = 0.6;
    private const double BowStraightPenalty = 0.4;

    private static readonly string[] Glyphs = { "R", "DR", "D", "DL", "L", "UL", "U", "UR" };

    public static Signature FromPoints(IReadOnlyList<Point> points)
    {
        var total = PathLength(points);
        if (total < MinimumPathLength)
        {
            return Signature.Empty;
        }

        var resampled = Resample(points, SampleCount, total);
        if (resampled.Length < 2)
        {
            return Signature.Empty;
        }

        var sequence = DirectionSequence(resampled);
        return sequence.Length == 0
            ? Signature.Empty
            : new Signature(sequence, BowMetric(resampled));
    }

    public static Signature Canonical(IEnumerable<IReadOnlyList<Point>> samples)
    {
        var signatures = samples
            .Select(FromPoints)
            .Where(static s => !s.IsEmpty)
            .ToList();
        if (signatures.Count == 0)
        {
            return Signature.Empty;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var signature in signatures)
        {
            var key = SequenceKey(signature.Sequence);
            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var top = counts.Values.Max();
        var canonicalSequence = signatures[^1].Sequence;
        for (var i = signatures.Count - 1; i >= 0; i--)
        {
            if (counts[SequenceKey(signatures[i].Sequence)] == top)
            {
                canonicalSequence = signatures[i].Sequence;
                break;
            }
        }

        var canonicalKey = SequenceKey(canonicalSequence);
        var bows = signatures
            .Where(s => SequenceKey(s.Sequence) == canonicalKey)
            .Select(static s => s.Bow)
            .ToList();
        var averageBow = bows.Count == 0 ? 0 : bows.Average();
        return new Signature(canonicalSequence.ToArray(), averageBow);
    }

    public static double Distance(Signature lhs, Signature rhs)
    {
        double sequenceDistance;
        if (lhs.Sequence.Length == 1 &&
            rhs.Sequence.Length == 1 &&
            lhs.BowSign != 0 &&
            lhs.BowSign == rhs.BowSign &&
            CircularBucketDistance(lhs.Sequence[0], rhs.Sequence[0]) <= 1)
        {
            sequenceDistance = 0;
        }
        else
        {
            sequenceDistance = SequenceDistance(lhs.Sequence, rhs.Sequence);
        }

        return lhs.Sequence.Length == 1 && rhs.Sequence.Length == 1
            ? sequenceDistance + BowPenalty(lhs.Bow, rhs.Bow)
            : sequenceDistance;
    }

    public static string Arrows(IReadOnlyList<int> sequence)
        => string.Join("", sequence.Select(static d => d >= 0 && d < Glyphs.Length ? Glyphs[d] : "?"));

    public static string DisplayString(Signature signature)
        => Arrows(signature.Sequence) + (signature.Sequence.Length == 1 ? BowGlyph(signature.BowSign) : "");

    public static int BowSign(double bow)
    {
        if (bow > BowSignThreshold) return 1;
        if (bow < -BowSignThreshold) return -1;
        return 0;
    }

    private static int[] DirectionSequence(IReadOnlyList<Point> points)
    {
        var n = points.Count;
        var w = CornerWindow;
        if (n <= 2 * w + 1)
        {
            return NetDirection(points, 0, n - 1) is { } single ? new[] { single } : Array.Empty<int>();
        }

        var turns = new double[n];
        for (var i = w; i < n - w; i++)
        {
            var ax = points[i].X - points[i - w].X;
            var ay = points[i].Y - points[i - w].Y;
            var bx = points[i + w].X - points[i].X;
            var by = points[i + w].Y - points[i].Y;
            var na = Math.Sqrt(ax * ax + ay * ay);
            var nb = Math.Sqrt(bx * bx + by * by);
            if (na <= 0 || nb <= 0)
            {
                continue;
            }

            var cosine = Math.Clamp((ax * bx + ay * by) / (na * nb), -1, 1);
            turns[i] = Math.Acos(cosine);
        }

        var corners = new List<int>();
        for (var i = w; i < n - w; i++)
        {
            if (turns[i] <= CornerAngleThreshold)
            {
                continue;
            }

            var lower = Math.Max(0, i - w);
            var upper = Math.Min(n - 1, i + w);
            var hasLargerNeighbor = false;
            for (var j = lower; j <= upper; j++)
            {
                if (turns[j] > turns[i])
                {
                    hasLargerNeighbor = true;
                    break;
                }
            }
            if (hasLargerNeighbor)
            {
                continue;
            }

            if (corners.Count > 0 && i - corners[^1] <= w)
            {
                continue;
            }

            corners.Add(i);
        }

        var bounds = new List<int>(corners.Count + 2) { 0 };
        bounds.AddRange(corners);
        bounds.Add(n - 1);

        var sequence = new List<int>();
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var direction = NetDirection(points, bounds[i], bounds[i + 1]);
            if (direction is null || (sequence.Count > 0 && sequence[^1] == direction.Value))
            {
                continue;
            }
            sequence.Add(direction.Value);
        }

        return sequence.ToArray();
    }

    private static int? NetDirection(IReadOnlyList<Point> points, int start, int end)
    {
        var dx = points[end].X - points[start].X;
        var dy = points[end].Y - points[start].Y;
        return dx == 0 && dy == 0 ? null : Quantize(dx, dy);
    }

    private static int Quantize(double dx, double dy)
    {
        var bucket = (int)Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4.0));
        return ((bucket % 8) + 8) % 8;
    }

    private static double BowMetric(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return 0;
        }

        var first = points[0];
        var last = points[^1];
        var cx = last.X - first.X;
        var cy = last.Y - first.Y;
        var chord = Math.Sqrt(cx * cx + cy * cy);
        if (chord <= 0 || chord < 0.2 * PathLength(points))
        {
            return 0;
        }

        var ux = cx / chord;
        var uy = cy / chord;
        var sum = 0.0;
        foreach (var point in points)
        {
            sum += ux * (point.Y - first.Y) - uy * (point.X - first.X);
        }

        return (sum / points.Count) / chord;
    }

    private static double BowPenalty(double lhs, double rhs)
    {
        var lhsSign = BowSign(lhs);
        var rhsSign = BowSign(rhs);
        if (lhsSign == 0 && rhsSign == 0)
        {
            return 0;
        }
        if (lhsSign != 0 && rhsSign != 0 && lhsSign != rhsSign)
        {
            return BowOppositePenalty;
        }
        if (lhsSign == 0 || rhsSign == 0)
        {
            return Math.Max(Math.Abs(lhs), Math.Abs(rhs)) > BowStrongThreshold
                ? BowStraightPenalty
                : 0;
        }
        return 0;
    }

    private static double SequenceDistance(IReadOnlyList<int> lhs, IReadOnlyList<int> rhs)
    {
        if (lhs.SequenceEqual(rhs))
        {
            return 0;
        }
        if (lhs.Count == 0 || rhs.Count == 0)
        {
            return 1;
        }
        return (double)Levenshtein(lhs, rhs) / Math.Max(lhs.Count, rhs.Count);
    }

    private static int Levenshtein(IReadOnlyList<int> lhs, IReadOnlyList<int> rhs)
    {
        var previous = Enumerable.Range(0, rhs.Count + 1).ToArray();
        var current = new int[rhs.Count + 1];
        for (var i = 1; i <= lhs.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= rhs.Count; j++)
            {
                var cost = lhs[i - 1] == rhs[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }
        return previous[rhs.Count];
    }

    private static Point[] Resample(IReadOnlyList<Point> points, int targetCount, double knownPathLength)
    {
        if (points.Count == 0)
        {
            return Array.Empty<Point>();
        }

        var first = points[0];
        if (targetCount <= 1)
        {
            return new[] { first };
        }

        if (knownPathLength <= 0)
        {
            return Enumerable.Repeat(first, targetCount).ToArray();
        }

        var interval = knownPathLength / (targetCount - 1);
        var result = new List<Point>(targetCount) { first };
        var accumulated = 0.0;
        var segmentStart = first;

        for (var i = 1; i < points.Count; i++)
        {
            var segmentEnd = points[i];
            var remaining = Distance(segmentStart, segmentEnd);
            while (remaining > 0 && accumulated + remaining >= interval)
            {
                var needed = interval - accumulated;
                var ratio = needed / remaining;
                var point = new Point(
                    segmentStart.X + ratio * (segmentEnd.X - segmentStart.X),
                    segmentStart.Y + ratio * (segmentEnd.Y - segmentStart.Y));
                result.Add(point);
                if (result.Count == targetCount)
                {
                    return result.ToArray();
                }

                segmentStart = point;
                remaining = Distance(segmentStart, segmentEnd);
                accumulated = 0;
            }

            accumulated += remaining;
            segmentStart = segmentEnd;
        }

        var last = points[^1];
        while (result.Count < targetCount)
        {
            result.Add(last);
        }
        return result.ToArray();
    }

    private static double PathLength(IReadOnlyList<Point> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
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

    private static int CircularBucketDistance(int a, int b)
    {
        var d = Math.Abs(a - b) % 8;
        return Math.Min(d, 8 - d);
    }

    private static string SequenceKey(IReadOnlyList<int> sequence)
        => string.Join(",", sequence);

    private static string BowGlyph(int sign) => sign switch
    {
        1 => "+bow",
        -1 => "-bow",
        _ => string.Empty,
    };
}
