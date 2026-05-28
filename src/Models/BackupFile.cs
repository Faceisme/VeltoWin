namespace Velto.Models;

/// <summary>
/// 导出 / 导入用的 JSON 包装。跟 macOS 版的 VeltoBackupFile 结构一致,
/// 但里面装的是 Windows 版的 Shortcut(VirtualKey + Modifiers),
/// 不能跟 mac 版的备份文件互相 import。
/// </summary>
public sealed class BackupFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string AppName { get; set; } = "Velto for Windows";
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public List<GestureCommand> Gestures { get; set; } = new();
    public AppPreferences Preferences { get; set; } = AppPreferences.Default;
}
