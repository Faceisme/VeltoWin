using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Models;
using Velto.Services;

namespace Velto.Tests;

[TestClass]
public sealed class PreferenceMigrationTests
{
    [TestMethod]
    public void MigrateLegacy_ResetsOldDefaultsToSignatureScale()
    {
        var prefs = new AppPreferences { RecognitionThreshold = 0.22, GestureTimeoutSeconds = 0.6 };

        PreferenceMigration.MigrateLegacy(prefs);

        Assert.AreEqual(0.34, prefs.RecognitionThreshold, 0.001);
        Assert.AreEqual(3.0, prefs.GestureTimeoutSeconds, 0.001);
    }

    [TestMethod]
    public void MigrateLegacy_ResetsOldShapeDefaultThreshold()
    {
        var prefs = new AppPreferences { RecognitionThreshold = 0.18 };

        PreferenceMigration.MigrateLegacy(prefs);

        Assert.AreEqual(0.34, prefs.RecognitionThreshold, 0.001);
    }

    [TestMethod]
    public void MigrateLegacy_KeepsCustomValuesAwayFromOldDefaults()
    {
        var prefs = new AppPreferences { RecognitionThreshold = 0.30, GestureTimeoutSeconds = 5.0 };

        PreferenceMigration.MigrateLegacy(prefs);

        Assert.AreEqual(0.30, prefs.RecognitionThreshold, 0.001);
        Assert.AreEqual(5.0, prefs.GestureTimeoutSeconds, 0.001);
    }

    [TestMethod]
    public void Validate_KeepsDeliberateValuesThatLegacyMigrationWouldReset()
    {
        // 版本化迁移的关键回归:formatVersion 已是当前版本时只走 Validate,
        // 用户在滑条范围内主动设置的 0.22 阈值 / 0.5s 超时不再被启动重置。
        var prefs = new AppPreferences { RecognitionThreshold = 0.22, GestureTimeoutSeconds = 0.5 };

        PreferenceMigration.Validate(prefs);

        Assert.AreEqual(0.22, prefs.RecognitionThreshold, 0.001);
        Assert.AreEqual(0.5, prefs.GestureTimeoutSeconds, 0.001);
    }

    [TestMethod]
    public void Validate_ResetsOutOfRangeValuesToDefaults()
    {
        var prefs = new AppPreferences { RecognitionThreshold = 0.9, GestureTimeoutSeconds = 0.05 };

        PreferenceMigration.Validate(prefs);

        Assert.AreEqual(0.34, prefs.RecognitionThreshold, 0.001);
        Assert.AreEqual(3.0, prefs.GestureTimeoutSeconds, 0.001);
    }
}
