using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velto.Services;

namespace Velto.Tests;

[TestClass]
public sealed class ElevationServiceTests
{
    [TestMethod]
    public void CreateRestartAsAdministratorStartInfo_UsesShellRunAsVerb()
    {
        var startInfo = ElevationService.CreateRestartAsAdministratorStartInfo(
            @"C:\Tools\Velto.exe");

        Assert.IsNotNull(startInfo);
        Assert.AreEqual(@"C:\Tools\Velto.exe", startInfo.FileName);
        Assert.AreEqual("runas", startInfo.Verb);
        Assert.IsTrue(startInfo.UseShellExecute);
        Assert.AreEqual(@"C:\Tools", startInfo.WorkingDirectory);
    }

    [TestMethod]
    public void CreateRestartAsAdministratorStartInfo_RejectsMissingPath()
    {
        var startInfo = ElevationService.CreateRestartAsAdministratorStartInfo("");

        Assert.IsNull(startInfo);
    }
}
