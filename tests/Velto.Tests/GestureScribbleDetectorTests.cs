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
    public void Update_DoesNotCancelReversalGestureWithTerminalOvershoot()
    {
        // L→R 折返手势自带 180° 转角,收笔再带一次回钩(又一个 180°)也不该作废,
        // 否则"刷新/重新打开/置顶"这类折返手势经常被误杀。
        var detector = new GestureScribbleDetector();
        detector.Reset(new Point(200, 0));

        var cancelled = false;
        foreach (var point in new[]
        {
            new Point(120, 0),
            new Point(40, 0),    // 向左
            new Point(120, 2),
            new Point(200, 2),   // 折返向右
            new Point(188, 2),   // 收笔回钩
        })
        {
            cancelled |= detector.Update(point);
        }

        Assert.IsFalse(cancelled);
    }

    [TestMethod]
    public void Update_IgnoresSmallWobbleTurns()
    {
        // 慢速长手势的手抖(单次 < ~20°)不该累计进乱画判定。
        var detector = new GestureScribbleDetector();
        detector.Reset(new Point(0, 0));

        var cancelled = false;
        for (var i = 1; i <= 40; i++)
        {
            cancelled |= detector.Update(new Point(i * 12, i % 2 == 0 ? 0 : 2));
        }

        Assert.IsFalse(cancelled);
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
