using System.IO;
using System.Text;

namespace Velto.Services;

public static class GestureDiagnosticLogger
{
    private static readonly object Lock = new();

    public static string CurrentPath
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return Path.Combine(desktop, $"Velto-Gesture-Diagnostics-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(Exception ex, string context)
        => Write("ERROR", $"{context} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] pid={Environment.ProcessId} {message}{Environment.NewLine}";
        try
        {
            lock (Lock)
            {
                File.AppendAllText(CurrentPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never affect gesture handling.
        }
    }
}
