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

    private static Point[] Stroke(params (double X, double Y)[] points)
        => points.Select(p => new Point(p.X, p.Y)).ToArray();
}
