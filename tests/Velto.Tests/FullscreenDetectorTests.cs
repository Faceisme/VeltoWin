using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Services;
using Velto.Win32;

namespace Velto.Tests;

[TestClass]
public sealed class FullscreenDetectorTests
{
    private static NativeMethods.RECT Rect(int left, int top, int right, int bottom)
        => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static readonly NativeMethods.RECT Monitor = Rect(0, 0, 1920, 1080);

    [TestMethod]
    public void CoversMonitor_ExactMatch_IsFullscreen()
    {
        Assert.IsTrue(FullscreenDetector.CoversMonitor(Rect(0, 0, 1920, 1080), Monitor));
    }

    [TestMethod]
    public void CoversMonitor_SlightlyLargerThanMonitor_IsFullscreen()
    {
        // 某些全屏游戏会把窗口设得比显示器略大几像素。
        Assert.IsTrue(FullscreenDetector.CoversMonitor(Rect(-2, -2, 1922, 1082), Monitor));
    }

    [TestMethod]
    public void CoversMonitor_MaximizedLeavingTaskbar_IsNotFullscreen()
    {
        // 最大化窗口只铺工作区,底部露出 40px 任务栏 → 不算全屏。
        Assert.IsFalse(FullscreenDetector.CoversMonitor(Rect(0, 0, 1920, 1040), Monitor));
    }

    [TestMethod]
    public void CoversMonitor_WindowedSmaller_IsNotFullscreen()
    {
        Assert.IsFalse(FullscreenDetector.CoversMonitor(Rect(100, 100, 800, 600), Monitor));
    }

    [TestMethod]
    public void CoversMonitor_SecondMonitorWithOffset_IsFullscreen()
    {
        // 多显示器:第二块屏从 x=1920 起,窗口需以该屏自身坐标判定。
        var second = Rect(1920, 0, 3840, 1080);
        Assert.IsTrue(FullscreenDetector.CoversMonitor(Rect(1920, 0, 3840, 1080), second));
    }

    [TestMethod]
    public void CoversMonitor_DegenerateMonitor_IsNotFullscreen()
    {
        // 拿不到有效显示器矩形时不应误判为全屏。
        Assert.IsFalse(FullscreenDetector.CoversMonitor(Rect(0, 0, 0, 0), Rect(0, 0, 0, 0)));
    }
}
