using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Services;

namespace Velto.Tests;

[TestClass]
public sealed class GestureScribbleDetectorTests
{
    [TestMethod]
    public void Update_CancelsAfterBackAndForthTurns()
    {
        var detector = new GestureScribbleDetector();
        detector.Reset(new Point(0, 0));

        var cancelled = false;
        foreach (var point in new[]
        {
            new Point(20, 0),
            new Point(0, 0),
            new Point(20, 0),
            new Point(0, 0),
        })
        {
            cancelled |= detector.Update(point);
        }

        Assert.IsTrue(cancelled);
    }

    [TestMethod]
    public void Update_DoesNotCancelNormalBentGesture()
    {
        var detector = new GestureScribbleDetector();
        detector.Reset(new Point(0, 0));

        var cancelled = false;
        foreach (var point in new[]
        {
            new Point(40, 0),
            new Point(40, 40),
            new Point(80, 40),
        })
        {
            cancelled |= detector.Update(point);
        }

        Assert.IsFalse(cancelled);
    }
}
