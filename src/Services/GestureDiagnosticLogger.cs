using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Velto.Services;

public static class GestureDiagnosticLogger
{
    private static readonly object Lock = new();

    public static bool Enabled { get; } =
        IsSwitchEnabled("VELTO_DIAG") ||
        IsSwitchEnabled("VELTO_GESTURE_DIAG") ||
        IsSwitchEnabled("VELTO_SHELL_DIAG");

    public static string CurrentPath
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return Path.Combine(desktop, $"Velto-Gesture-Diagnostics-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    /// <summary>
    /// 插值字符串走这个重载:<see cref="Enabled"/> 为 false 时编译器直接跳过所有插值表达式的求值
    /// (out bool shouldAppend 模式)。钩子热路径上的 Info($"...") 在诊断关闭时因此是零成本 ——
    /// 不构造字符串、不调用插值孔里的任何方法(如 WindowTargeter.Describe 的系统调用)。
    /// </summary>
    public static void Info(ref InfoInterpolatedStringHandler message)
    {
        if (message.Enabled)
        {
            Write("INFO ", message.ToStringAndClear());
        }
    }

    public static void Error(Exception ex, string context)
        => Write("ERROR", $"{context} :: {ex.GetType().Name}: {ex.Message}");

    [InterpolatedStringHandler]
    public ref struct InfoInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _inner;

        internal bool Enabled { get; }

        public InfoInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            Enabled = GestureDiagnosticLogger.Enabled;
            shouldAppend = Enabled;
            _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
        public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

        internal string ToStringAndClear() => _inner.ToStringAndClear();
    }

    public static bool IsSwitchEnabled(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static void Write(string level, string message)
    {
        if (!Enabled)
        {
            return;
        }

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
