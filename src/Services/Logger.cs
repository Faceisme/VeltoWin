using System.IO;
using System.Text;

namespace Velto.Services;

/// <summary>
/// 极简文件日志器 —— 不引第三方依赖,自己写到 <c>%APPDATA%\Velto\logs\velto-YYYYMMDD.log</c>。
///
/// 设计取舍:
///   - 同步写 + 锁,简单。手势工具不会有高频日志(几乎不打热路径),性能不是问题。
///   - 写失败时静默吞异常 —— 日志器自己不能成为崩溃源。
///   - 按日期切割文件,不做大小限制(personal tool,体积可控)。
///   - 不暴露级别 enum,直接 4 个静态方法:<see cref="Info"/> / <see cref="Warn"/> / <see cref="Error"/> /
///     <see cref="Error(Exception, string)"/>。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _directory;

    static Logger()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Velto", "logs");
        try { Directory.CreateDirectory(_directory); } catch { /* swallow */ }
    }

    public static void Info(string message)  => Write("INFO ", message);
    public static void Warn(string message)  => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(Exception ex, string context)
        => Write("ERROR", $"{context} :: {ex.GetType().Name}: {ex.Message}\n{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        try
        {
            var path = Path.Combine(_directory, $"velto-{DateTime.Now:yyyyMMdd}.log");
            lock (_lock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch { /* logger never throws */ }
    }
}
