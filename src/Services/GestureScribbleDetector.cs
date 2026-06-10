using System.Windows;

namespace Velto.Services;

/// <summary>
/// Detects scribble-to-cancel by accumulating turn angles between movement legs.
/// Back-and-forth scribbles and circles accumulate quickly; ordinary gestures do not.
/// </summary>
public sealed class GestureScribbleDetector
{
    private const double LegMinLength = 10;
    // 单次小于 ~20° 的转角是手抖噪声,不累计 —— 否则慢速长手势会被抖动磨过阈值。
    private const double NoiseTurnFloor = 0.35;
    // 折返类手势(L→R / D→U)本身自带一个 180° 转角,阈值要给足余量:
    // 一段折返 + 一次收笔回钩 ≈ 2π,真正的乱画(三次以上往返)很快超过 2.9π。
    private const double TurnThreshold = 2.9 * Math.PI;

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
            var turn = Math.Acos(dot);
            if (turn >= NoiseTurnFloor)
            {
                _turnAccumulator += turn;
            }
            return _turnAccumulator >= TurnThreshold;
        }
        finally
        {
            _lastLegDirection = direction;
            _legStart = location;
        }
    }
}
