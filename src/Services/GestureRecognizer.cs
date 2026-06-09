using System.Windows;
using Velto.Models;

namespace Velto.Services;

/// <summary>
/// Signature-based unistroke recognizer. Each command is reduced to one
/// canonical direction signature, matching the current macOS Velto strategy.
/// </summary>
public sealed class GestureRecognizer
{
    private const double AmbiguityMargin = 0.05;

    private readonly object _cacheLock = new();
    private ulong _cachedVersion;
    private List<CommandSignature> _cachedSignatures = new();

    public sealed record Match(
        GestureCommand Command,
        double Distance,
        double? RunnerUpDistance = null,
        string Strategy = "signature");

    private sealed record CommandSignature(GestureCommand Command, GestureDirection.Signature Signature);

    public Match? BestCandidate(IReadOnlyList<Point> points, IReadOnlyList<GestureCommand> commands, ulong version)
    {
        var candidate = GestureDirection.FromPoints(points);
        if (candidate.IsEmpty)
        {
            return null;
        }

        var ranked = CommandSignatures(commands, version)
            .Where(static entry => !entry.Signature.IsEmpty)
            .Select(entry => new Match(
                entry.Command,
                GestureDirection.Distance(candidate, entry.Signature)))
            .OrderBy(static match => match.Distance)
            .ThenBy(static match => match.Command.Name, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count == 0)
        {
            return null;
        }

        var best = ranked[0];
        return best with
        {
            RunnerUpDistance = ranked.Count > 1 ? ranked[1].Distance : null,
        };
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

        if (best.RunnerUpDistance is { } runnerUp &&
            runnerUp - best.Distance < AmbiguityMargin)
        {
            return null;
        }

        return best;
    }

    public string DescribeSimpleDirection(IReadOnlyList<Point> points)
    {
        var signature = GestureDirection.FromPoints(points);
        if (signature.Sequence.Length != 1)
        {
            return "none";
        }

        return signature.Sequence[0] switch
        {
            0 => "Right",
            2 => "Down",
            4 => "Left",
            6 => "Up",
            _ => "none",
        };
    }

    public string DescribeCandidates(
        IReadOnlyList<Point> points,
        IReadOnlyList<GestureCommand> commands,
        ulong version,
        int maxCount = 5)
    {
        var candidate = GestureDirection.FromPoints(points);
        if (candidate.IsEmpty)
        {
            return "candidate=invalid";
        }

        return string.Join(" | ", CommandSignatures(commands, version)
            .Where(static entry => !entry.Signature.IsEmpty)
            .Select(entry => new
            {
                entry.Command,
                Distance = GestureDirection.Distance(candidate, entry.Signature),
                Signature = GestureDirection.DisplayString(entry.Signature),
            })
            .OrderBy(static row => row.Distance)
            .ThenBy(static row => row.Command.Name, StringComparer.Ordinal)
            .Take(maxCount)
            .Select((row, index) =>
                $"#{index + 1} name='{row.Command.Name}' d={row.Distance:0.000} sig={row.Signature}"));
    }

    public string DescribeSequence(IReadOnlyList<Point> points)
    {
        var signature = GestureDirection.FromPoints(points);
        return signature.IsEmpty ? "(empty)" : GestureDirection.DisplayString(signature);
    }

    public GestureDirection.Signature CanonicalSignature(GestureCommand command)
        => GestureDirection.Canonical(command.Templates.Select(ToPoints));

    private List<CommandSignature> CommandSignatures(IReadOnlyList<GestureCommand> commands, ulong version)
    {
        lock (_cacheLock)
        {
            if (_cachedVersion == version)
            {
                return _cachedSignatures;
            }

            var signatures = commands
                .Select(command => new CommandSignature(command, CanonicalSignature(command)))
                .ToList();

            _cachedVersion = version;
            _cachedSignatures = signatures;
            return signatures;
        }
    }

    private static Point[] ToPoints(IReadOnlyList<StrokePoint> template)
    {
        var points = new Point[template.Count];
        for (var i = 0; i < template.Count; i++)
        {
            points[i] = new Point(template[i].X, template[i].Y);
        }
        return points;
    }
}
