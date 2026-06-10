using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Models;
using Velto.Services;

namespace Velto.Tests;

[TestClass]
public sealed class GestureRecognizerTests
{
    [TestMethod]
    public void BestMatch_MatchesAdjacentSingleSegmentDirectionWhenBowMatches()
    {
        var command = Command("Curved next", Bezier((0, 0), (30, 80), (100, 100)));
        var drawn = Bezier((0, 0), (50, 60), (120, 20));

        var match = new GestureRecognizer().BestMatch(drawn, new[] { command }, version: 1, threshold: 0.34);

        Assert.IsNotNull(match);
        Assert.AreEqual(command.Id, match.Command.Id);
    }

    [TestMethod]
    public void BestMatch_RejectsAmbiguousDuplicateSignatures()
    {
        var first = Command("First", Stroke((10, 10), (80, 10)));
        var second = Command("Second", Stroke((15, 20), (95, 20)));
        var drawn = Stroke((0, 0), (100, 1));

        var match = new GestureRecognizer().BestMatch(drawn, new[] { first, second }, version: 1, threshold: 0.34);

        Assert.IsNull(match);
    }

    [TestMethod]
    public void BestMatch_SeparatesSameDirectionGesturesByBow()
    {
        var downBow = Command("Down bow", Stroke((10, 40), (35, 80), (90, 40)));
        var upBow = Command("Up bow", Stroke((10, 40), (35, 0), (90, 40)));
        var drawn = Stroke((12, 42), (38, 82), (94, 43));

        var match = new GestureRecognizer().BestMatch(drawn, new[] { downBow, upBow }, version: 1, threshold: 0.34);

        Assert.IsNotNull(match);
        Assert.AreEqual(downBow.Id, match.Command.Id);
    }

    [TestMethod]
    public void BestMatch_MatchesReversalGestureWithHookAtTurn()
    {
        // "刷新"回归:模板是干净的 L→R 折线,实画折返处带向下小钩。
        var template = new List<Point>();
        for (var x = 300.0; x >= 150; x -= 5) template.Add(new Point(x, 200));
        for (var x = 150.0; x <= 310; x += 5) template.Add(new Point(x, 200));
        var command = Command("刷新", template);

        var drawn = new List<Point>();
        for (var x = 300.0; x >= 150; x -= 5) drawn.Add(new Point(x, 200));
        for (var y = 200.0; y <= 225; y += 5) drawn.Add(new Point(150, y));
        for (var x = 150.0; x <= 310; x += 5) drawn.Add(new Point(x, 225));

        var match = new GestureRecognizer().BestMatch(drawn, new[] { command }, version: 1, threshold: 0.30);

        Assert.IsNotNull(match);
        Assert.AreEqual(command.Id, match.Command.Id);
    }

    [TestMethod]
    public void BestMatch_MatchesTwoSegmentGestureWhenSecondLegWobblesAcrossBucketBoundary()
    {
        // "置顶"回归:模板上挑段偏 UR,实画是竖直的 U,相邻桶只算半个错。
        var command = Command("置顶", Stroke((100, 100), (100, 260), (160, 110)));
        var drawn = Stroke((200, 100), (202, 258), (198, 105));

        var match = new GestureRecognizer().BestMatch(drawn, new[] { command }, version: 1, threshold: 0.30);

        Assert.IsNotNull(match);
        Assert.AreEqual(command.Id, match.Command.Id);
    }

    private static GestureCommand Command(string name, IReadOnlyList<Point> template) => new()
    {
        Name = name,
        Templates = { template.Select(p => new StrokePoint(p.X, p.Y)).ToList() },
    };

    private static Point[] Stroke(params (double X, double Y)[] points)
        => points.Select(p => new Point(p.X, p.Y)).ToArray();

    private static Point[] Bezier(
        (double X, double Y) start,
        (double X, double Y) control,
        (double X, double Y) end)
    {
        var points = new Point[13];
        for (var i = 0; i < points.Length; i++)
        {
            var t = (double)i / (points.Length - 1);
            var mt = 1 - t;
            points[i] = new Point(
                mt * mt * start.X + 2 * mt * t * control.X + t * t * end.X,
                mt * mt * start.Y + 2 * mt * t * control.Y + t * t * end.Y);
        }
        return points;
    }
}
