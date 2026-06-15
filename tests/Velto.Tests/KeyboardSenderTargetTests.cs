using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Models;
using Velto.Services;

namespace Velto.Tests;

[TestClass]
public sealed class KeyboardSenderTargetTests
{
    [TestMethod]
    public void ShouldUseWindowCloseFallback_UsesAltF4ForTaskManagerCtrlW()
    {
        var shortcut = new Shortcut(0x57, ModifierKeys.Control, "Ctrl+W");

        Assert.IsTrue(KeyboardSender.ShouldUseWindowCloseFallback(shortcut, "Taskmgr", "TaskManagerWindow"));
    }

    [TestMethod]
    public void ShouldUseWindowCloseFallback_UsesAltF4ForVisualStudioCodeCtrlW()
    {
        var shortcut = new Shortcut(0x57, ModifierKeys.Control, "Ctrl+W");

        Assert.IsTrue(KeyboardSender.ShouldUseWindowCloseFallback(shortcut, "Code", "Chrome_WidgetWin_1"));
    }

    [TestMethod]
    public void ShouldUseWindowCloseFallback_KeepsCtrlWForBrowsers()
    {
        var shortcut = new Shortcut(0x57, ModifierKeys.Control, "Ctrl+W");

        Assert.IsFalse(KeyboardSender.ShouldUseWindowCloseFallback(shortcut, "chrome", "Chrome_WidgetWin_1"));
        Assert.IsFalse(KeyboardSender.ShouldUseWindowCloseFallback(shortcut, "msedge", "Chrome_WidgetWin_1"));
        Assert.IsFalse(KeyboardSender.ShouldUseWindowCloseFallback(shortcut, "firefox", "MozillaWindowClass"));
    }

    [TestMethod]
    public void ShouldUseWindowCloseFallback_IgnoresOtherShortcuts()
    {
        var shortcut = new Shortcut(0x74, ModifierKeys.None, "F5");

        Assert.IsFalse(KeyboardSender.ShouldUseWindowCloseFallback(shortcut, "Taskmgr", "TaskManagerWindow"));
    }
}
