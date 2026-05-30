using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velto.Models;

namespace Velto.Services;

/// <summary>
/// 配置持久化。 <c>%APPDATA%\Velto\config.json</c>:存手势 + 偏好。
///
/// 任何修改都通过 <see cref="UpdateGestures"/> / <see cref="UpdatePreferences"/>,
/// 改完会原子地落盘并触发 <see cref="Changed"/>,订阅方(GestureEngine / SettingsWindow)
/// 即可同步。
///
/// 单例。所有访问应在 UI 线程上 —— 鼠标钩子线程要读最新的手势/偏好,
/// 走 <see cref="GestureEngine"/> 内部的快照副本,不直接来这里。
/// </summary>
public sealed class ConfigStore
{
    public static ConfigStore Shared { get; } = new();

    public event Action<ChangeReason>? Changed;

    public enum ChangeReason
    {
        Gestures,
        Preferences,
        Settings,
        BackupImport,
    }

    public IReadOnlyList<GestureCommand> Gestures => _gestures;
    public AppPreferences Preferences => _preferences;

    /// <summary>每次手势列表改动 +1。识别器拿来做 O(1) 缓存失效判断。</summary>
    public ulong GesturesVersion { get; private set; }

    private List<GestureCommand> _gestures;
    private AppPreferences _preferences;

    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOptions;

    private ConfigStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Velto");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        var (gestures, prefs) = Load();
        _gestures = gestures;
        _preferences = prefs;
        GesturesVersion = 1;
    }

    public void UpdateGestures(Action<List<GestureCommand>> mutate)
    {
        mutate(_gestures);
        GesturesVersion++;
        Save();
        Changed?.Invoke(ChangeReason.Gestures);
    }

    public void UpdatePreferences(Action<AppPreferences> mutate)
    {
        mutate(_preferences);
        Save();
        Changed?.Invoke(ChangeReason.Preferences);
    }

    public void ReplaceSettings(IEnumerable<GestureCommand> gestures, AppPreferences preferences)
    {
        _gestures = gestures.Select(CloneGesture).ToList();
        _preferences = ClonePreferences(preferences);
        GesturesVersion++;
        Save();
        Changed?.Invoke(ChangeReason.Settings);
    }

    public byte[] ExportBackup()
    {
        var backup = new BackupFile
        {
            Gestures = _gestures,
            Preferences = _preferences,
        };
        return JsonSerializer.SerializeToUtf8Bytes(backup, _jsonOptions);
    }

    public (List<GestureCommand> Gestures, AppPreferences Preferences) ReadBackup(byte[] data)
    {
        var backup = JsonSerializer.Deserialize<BackupFile>(data, _jsonOptions)
                     ?? throw new InvalidDataException("无法解析备份文件");
        if (backup.FormatVersion > BackupFile.CurrentFormatVersion)
        {
            throw new InvalidDataException($"不支持的备份版本: {backup.FormatVersion}");
        }
        if (backup.Gestures.Count == 0)
        {
            throw new InvalidDataException("备份里没有手势配置");
        }

        var gestures = backup.Gestures.Select(CloneGesture).ToList();
        var preferences = ClonePreferences(backup.Preferences);
        preferences.GesturesEnabled = true;
        return (gestures, preferences);
    }

    public void ImportBackup(byte[] data)
    {
        var (gestures, preferences) = ReadBackup(data);
        _gestures = gestures;
        _preferences = preferences;
        GesturesVersion++;
        Save();
        Changed?.Invoke(ChangeReason.BackupImport);
    }

    private (List<GestureCommand>, AppPreferences) Load()
    {
        if (!File.Exists(_configPath))
        {
            return (DefaultGestures(), AppPreferences.Default);
        }

        try
        {
            var json = File.ReadAllBytes(_configPath);
            var payload = JsonSerializer.Deserialize<Payload>(json, _jsonOptions);
            if (payload is null)
            {
                return (DefaultGestures(), AppPreferences.Default);
            }

            var gestures = payload.Gestures.Count > 0 ? payload.Gestures : DefaultGestures();
            var prefs = payload.Preferences ?? AppPreferences.Default;
            prefs.GesturesEnabled = true; // 启动时强制开启,跟 mac 版一致
            MigrateRecognitionThreshold(prefs);
            return (gestures, prefs);
        }
        catch
        {
            // 损坏的配置不应该让 App 起不来 —— 退回默认,旧文件留个 .bak 方便调查
            try { File.Move(_configPath, _configPath + ".bak", overwrite: true); } catch { /* swallow */ }
            return (DefaultGestures(), AppPreferences.Default);
        }
    }

    /// <summary>
    /// 识别算法从"方向序列编辑距离"换成"曲线匹配"后,阈值量级变了。
    /// 旧配置里的阈值(典型 0.34 / 0.50,在新尺度下过于宽松会乱触发)落在新滑条范围
    /// [0.05, 0.40] 之外时,重置为新默认值。范围内的值视为用户在新尺度下已自行调过,保留。
    /// </summary>
    private static void MigrateRecognitionThreshold(AppPreferences prefs)
    {
        const double newMin = 0.05, newMax = 0.40;
        if (prefs.RecognitionThreshold < newMin || prefs.RecognitionThreshold > newMax)
        {
            Logger.Info($"识别阈值 {prefs.RecognitionThreshold:0.00} 超出新尺度范围,迁移为默认 {AppPreferences.Default.RecognitionThreshold:0.00}");
            prefs.RecognitionThreshold = AppPreferences.Default.RecognitionThreshold;
        }
    }

    private void Save()
    {
        var payload = new Payload
        {
            Gestures = _gestures,
            Preferences = _preferences,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);

        // 写到临时文件再原子重命名,避免半写状态。
        var tmp = _configPath + ".tmp";
        File.WriteAllBytes(tmp, json);
        File.Move(tmp, _configPath, overwrite: true);
    }

    private sealed class Payload
    {
        public List<GestureCommand> Gestures { get; set; } = new();
        public AppPreferences? Preferences { get; set; }
    }

    private static GestureCommand CloneGesture(GestureCommand source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Shortcut = source.Shortcut is null
            ? null
            : new Shortcut(source.Shortcut.VirtualKey, source.Shortcut.Modifiers, source.Shortcut.DisplayName),
        Templates = source.Templates
            .Select(template => template.Select(p => new StrokePoint(p.X, p.Y)).ToList())
            .ToList(),
    };

    private static AppPreferences ClonePreferences(AppPreferences source) => new()
    {
        GesturesEnabled = source.GesturesEnabled,
        ShowTrail = source.ShowTrail,
        ShowTrayIcon = source.ShowTrayIcon,
        RecognitionThreshold = source.RecognitionThreshold,
        GestureTimeoutSeconds = source.GestureTimeoutSeconds,
        GestureTargetPolicy = source.GestureTargetPolicy,
    };

    /// <summary>
    /// 与 macOS 版相同的四个默认手势,只是把 ⌘ 换成 Ctrl(后退/前进改成 Alt+方向键,
    /// 这是 Windows 浏览器/资源管理器的通用前进后退)。
    /// </summary>
    private static List<GestureCommand> DefaultGestures() => new()
    {
        new GestureCommand
        {
            Name = "后退",
            Templates = { LineTemplate(new(130, 80), new(20, 80)) },
            Shortcut = new Shortcut(0x25 /* VK_LEFT */, ModifierKeys.Alt, "Alt+←"),
        },
        new GestureCommand
        {
            Name = "前进",
            Templates = { LineTemplate(new(20, 80), new(130, 80)) },
            Shortcut = new Shortcut(0x27 /* VK_RIGHT */, ModifierKeys.Alt, "Alt+→"),
        },
        new GestureCommand
        {
            Name = "新建标签页",
            Templates = { LineTemplate(new(80, 130), new(80, 20)) },
            Shortcut = new Shortcut(0x54 /* VK_T */, ModifierKeys.Control, "Ctrl+T"),
        },
        new GestureCommand
        {
            Name = "关闭标签页",
            Templates = { LineTemplate(new(80, 20), new(80, 130)) },
            Shortcut = new Shortcut(0x57 /* VK_W */, ModifierKeys.Control, "Ctrl+W"),
        },
    };

    private static List<StrokePoint> LineTemplate(StrokePoint a, StrokePoint b)
    {
        const int steps = 12;
        var list = new List<StrokePoint>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            list.Add(new StrokePoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
        }
        return list;
    }
}
