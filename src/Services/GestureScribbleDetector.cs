using System.Windows;

namespace Velto.Services;

/// <summary>
/// Detects scribble-to-cancel by accumulating turn angles between movement legs.
/// Back-and-forth scribbles and circles accumulate quickly; ordinary gestures do not.
/// </summary>
public sealed class GestureScribbleDetector
{
    private const double LegMinLength = 10;
    private const double TurnThreshold = 2 * Math.PI;

    private Point _legStart;
    private Vector? _lastLegDirection;
    private double _turnAccumulator;

    public void Reset(Point location)
    {
        _legStart = location;
        _lastLegDirection = null;
        _turnAccumulator = 0;
    }

    public bool Update(Point location)
    {
        var dx = location.X - _legStart.X;
        var dy = location.Y - _legStart.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < LegMinLength)
        {
            return false;
        }

        var direction = new Vector(dx / length, dy / length);
        try
        {
            if (_lastLegDirection is not { } previous)
            {
                return false;
            }

            var dot = Math.Clamp(previous.X * direction.X + previous.Y * direction.Y, -1, 1);
            _turnAccumulator += Math.Acos(dot);
            return _turnAccumulator >= TurnThreshold;
        }
        finally
        {
            _lastLegDirection = direction;
            _legStart = location;
        }
    }
}
