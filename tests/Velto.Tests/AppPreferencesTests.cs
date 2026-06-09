using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Models;

namespace Velto.Tests;

[TestClass]
public sealed class AppPreferencesTests
{
    [TestMethod]
    public void DefaultPreferences_UseSignatureRecognitionScale()
    {
        var preferences = AppPreferences.Default;

        Assert.AreEqual(0.34, preferences.RecognitionThreshold, 0.001);
        Assert.AreEqual(3.0, preferences.GestureTimeoutSeconds, 0.001);
        Assert.IsTrue(preferences.ScribbleCancelEnabled);
    }
}
