using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Services;

namespace Velto.Tests;

[TestClass]
public sealed class GestureDirectionTests
{
    [TestMethod]
    public void FromPoints_ReducesNoisyHorizontalStrokeToSingleRightDirection()
    {
        var points = new[]
        {
            new Point(10, 20),
            new Point(24, 21),
            new Point(38, 19),
            new Point(55, 20),
            new Point(72, 22),
            new Point(90, 20),
        };

        var signature = GestureDirection.FromPoints(points);

        Assert.IsFalse(signature.IsEmpty);
        CollectionAssert.AreEqual(new[] { 0 }, signature.Sequence);
    }

    [TestMethod]
    public void Canonical_UsesMajorityDirectionSequence()
    {
        var signature = GestureDirection.Canonical(new[]
        {
            Stroke((10, 10), (80, 10)),
            Stroke((10, 10), (10, 80)),
            Stroke((15, 20), (90, 22)),
        });

        CollectionAssert.AreEqual(new[] { 0 }, signature.Sequence);
    }

    [TestMethod]
    public void Canonical_WhenTiedUsesLastRecordedDirectionSequence()
    {
        var signature = GestureDirection.Canonical(new[]
        {
            Stroke((10, 10), (80, 10)),
            Stroke((10, 10), (10, 80)),
        });

        CollectionAssert.AreEqual(new[] { 2 }, signature.Sequence);
    }

    [TestMethod]
    public void Distance_SeparatesSingleSegmentGesturesWithOppositeBow()
    {
        var downwardBow = GestureDirection.FromPoints(Stroke((10, 40), (35, 70), (65, 70), (90, 40)));
        var upwardBow = GestureDirection.FromPoints(Stroke((10, 40), (35, 10), (65, 10), (90, 40)));

        var distance = GestureDirection.Distance(downwardBow, upwardBow);

        Assert.IsTrue(distance >= 0.6, $"Expected opposite bows to be separated, got {distance:0.000}.");
    }

    [TestMethod]
    public void Distance_TreatsNearbySingleSegmentDirectionsAsEquivalentWhenBowMatches()
    {
        var rightDownBow = GestureDirection.FromPoints(Stroke((10, 20), (45, 50), (90, 62)));
        var diagonalDownBow = GestureDirection.FromPoints(Stroke((10, 10), (48, 54), (88, 82)));

        var distance = GestureDirection.Distance(rightDownBow, diagonalDownBow);

        Assert.AreEqual(0, distance, 0.001);
    }

    [TestMethod]
    public void FromPoints_DropsShortHookAtReversalTurn()
    {
        // 模拟"刷新"画法:向左 → 折返处带一个向下的小钩 → 向右。
        // 小钩不该成为独立方向段,否则 [L,D,R] 对 [L,R] 距离 0.33 超阈值。
        var points = new List<Point>();
        for (var x = 300.0; x >= 150; x -= 5) points.Add(new Point(x, 200));
        for (var y = 200.0; y <= 225; y += 5) points.Add(new Point(150, y));
        for (var x = 150.0; x <= 310; x += 5) points.Add(new Point(x, 225));

        var signature = GestureDirection.FromPoints(points);

        CollectionAssert.AreEqual(new[] { 4, 0 }, signature.Sequence,
            $"Expected L,R but got [{GestureDirection.Arrows(signature.Sequence)}]");
    }

    [TestMethod]
    public void Distance_TreatsAdjacentBucketSubstitutionAsHalfError()
    {
        // "置顶"场景:上挑段在 U/UR 桶边界摇摆,模板是 [D,UR],实画 [D,U]。
        var drawn = new GestureDirection.Signature(new[] { 2, 6 }, 0);
        var template = new GestureDirection.Signature(new[] { 2, 7 }, 0);

        var distance = GestureDirection.Distance(drawn, template);

        Assert.AreEqual(0.25, distance, 0.001);
    }

    [TestMethod]
    public void Distance_PrefersExactBucketOverAdjacentBucketWhenBowMatches()
    {
        // 画一条带轻微弓形的竖直上划:精确同桶的直线命令要赢过相邻桶的曲线命令,
        // 不能打成 0:0 平局被歧义保护拒绝。
        var drawn = new GestureDirection.Signature(new[] { 6 }, 0.04);
        var straightUp = new GestureDirection.Signature(new[] { 6 }, 0.04);
        var curvedUpRight = new GestureDirection.Signature(new[] { 7 }, 0.04);

        var exact = GestureDirection.Distance(drawn, straightUp);
        var adjacent = GestureDirection.Distance(drawn, curvedUpRight);

        Assert.AreEqual(0, exact, 0.001);
        Assert.IsTrue(adjacent >= 0.05, $"Adjacent bucket should cost a margin, got {adjacent:0.000}");
    }

    private static Point[] Stroke(params (double X, double Y)[] points)
        => points.Select(p => new Point(p.X, p.Y)).ToArray();
}
