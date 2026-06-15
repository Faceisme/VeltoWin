using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace Velto.Services;

public static class ElevationService
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ElevationService.IsElevated");
                return false;
            }
        }
    }

    public static ProcessStartInfo? CreateRestartAsAdministratorStartInfo(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(executablePath);
        return new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
        };
    }

    public static bool TryStartSelfAsAdministrator()
    {
        var startInfo = CreateRestartAsAdministratorStartInfo(Environment.ProcessPath);
        if (startInfo is null)
        {
            Logger.Warn("Elevation restart failed: current executable path is unavailable");
            return false;
        }

        try
        {
            Process.Start(startInfo);
            Logger.Info($"Elevation restart requested: {startInfo.FileName}");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Logger.Info("Elevation restart cancelled by user");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ElevationService.TryStartSelfAsAdministrator");
            return false;
        }
    }
}
