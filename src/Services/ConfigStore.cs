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

    /// <summary>
    /// config.json 的格式版本。0(字段缺失)= 旧版($1 / 方向算法时代),加载时做一次性偏好迁移;
    /// 2 = 方向签名时代,迁移已完成,不再重跑。与 <see cref="BackupFile.CurrentFormatVersion"/> 同步。
    /// </summary>
    private const int ConfigFormatVersion = 2;

    private const int StoredTemplatePointCount = 64;
    private const int StoredCoordinateDigits = 1;

    public event Action<ChangeReason>? Changed;

    public enum ChangeReason
    {
        Gestures,
        Preferences,
        Settings,
        BackupImport,
    }

    public IReadOnlyList<GestureCommand> Gestures
    {
        get
        {
            lock (_sync)
            {
                return _gestures.Select(CloneGesture).ToList();
            }
        }
    }

    public AppPreferences Preferences
    {
        get
        {
            lock (_sync)
            {
                return ClonePreferences(_preferences);
            }
        }
    }

    /// <summary>每次手势列表改动 +1。识别器拿来做 O(1) 缓存失效判断。</summary>
    public ulong GesturesVersion
    {
        get
        {
            lock (_sync)
            {
                return _gesturesVersion;
            }
        }
    }

    public sealed record Snapshot(
        IReadOnlyList<GestureCommand> Gestures,
        AppPreferences Preferences,
        ulong GesturesVersion);

    private readonly object _sync = new();
    private List<GestureCommand> _gestures;
    private AppPreferences _preferences;
    private ulong _gesturesVersion;

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
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        var (gestures, prefs, legacyUpgraded) = Load();
        _gestures = gestures;
        _preferences = prefs;
        _gesturesVersion = 1;

        if (legacyUpgraded)
        {
            // 旧版(无 formatVersion)配置完成一次性迁移后立刻落盘盖上新版本号,
            // 此后的启动不再重跑迁移 —— 用户在新版里设置的任何合法值都不会被误当旧默认值重置。
            try { Save(_gestures, _preferences); }
            catch (Exception ex) { Logger.Error(ex, "ConfigStore 迁移落盘"); }
        }
    }

    public Snapshot ReadSnapshot()
    {
        lock (_sync)
        {
            return new Snapshot(
                _gestures.Select(CloneGesture).ToList(),
                ClonePreferences(_preferences),
                _gesturesVersion);
        }
    }

    public void UpdateGestures(Action<List<GestureCommand>> mutate)
    {
        lock (_sync)
        {
            var nextGestures = _gestures.Select(CloneGesture).ToList();
            var nextPreferences = ClonePreferences(_preferences);
            mutate(nextGestures);

            CommitLocked(nextGestures, nextPreferences, gesturesChanged: true);
        }
        Changed?.Invoke(ChangeReason.Gestures);
    }

    public void UpdatePreferences(Action<AppPreferences> mutate)
    {
        lock (_sync)
        {
            var nextGestures = _gestures.Select(CloneGesture).ToList();
            var nextPreferences = ClonePreferences(_preferences);
            mutate(nextPreferences);

            CommitLocked(nextGestures, nextPreferences, gesturesChanged: false);
        }
        Changed?.Invoke(ChangeReason.Preferences);
    }

    public void ReplaceSettings(IEnumerable<GestureCommand> gestures, AppPreferences preferences)
    {
        lock (_sync)
        {
            CommitLocked(
                gestures.Select(CloneGesture).ToList(),
                ClonePreferences(preferences),
                gesturesChanged: true);
        }
        Changed?.Invoke(ChangeReason.Settings);
    }

    public byte[] ExportBackup()
    {
        BackupFile backup;
        lock (_sync)
        {
            backup = new BackupFile
            {
                Gestures = _gestures.Select(CloneGesture).ToList(),
                Preferences = ClonePreferences(_preferences),
            };
        }
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
        if (backup.FormatVersion < BackupFile.CurrentFormatVersion)
        {
            PreferenceMigration.MigrateLegacy(preferences);
        }
        PreferenceMigration.Validate(preferences);
        return (gestures, preferences);
    }

    public void ImportBackup(byte[] data)
    {
        var (gestures, preferences) = ReadBackup(data);
        lock (_sync)
        {
            CommitLocked(gestures, preferences, gesturesChanged: true);
        }
        Changed?.Invoke(ChangeReason.BackupImport);
    }

    private (List<GestureCommand>, AppPreferences, bool LegacyUpgraded) Load()
    {
        if (!File.Exists(_configPath))
        {
            return (DefaultGestures(), AppPreferences.Default, false);
        }

        try
        {
            var json = File.ReadAllBytes(_configPath);
            var payload = JsonSerializer.Deserialize<Payload>(json, _jsonOptions);
            if (payload is null)
            {
                return (DefaultGestures(), AppPreferences.Default, false);
            }

            var gestures = payload.Gestures.Select(CloneGesture).ToList();
            var prefs = payload.Preferences ?? AppPreferences.Default;
            var legacy = payload.FormatVersion < ConfigFormatVersion;
            if (legacy)
            {
                PreferenceMigration.MigrateLegacy(prefs);
            }
            PreferenceMigration.Validate(prefs);
            return (gestures, prefs, legacy);
        }
        catch
        {
            // 损坏的配置不应该让 App 起不来 —— 退回默认,旧文件留个 .bak 方便调查
            try { File.Move(_configPath, _configPath + ".bak", overwrite: true); } catch { /* swallow */ }
            return (DefaultGestures(), AppPreferences.Default, false);
        }
    }

    private void CommitLocked(
        List<GestureCommand> nextGestures,
        AppPreferences nextPreferences,
        bool gesturesChanged)
    {
        var committedGestures = nextGestures.Select(CloneGesture).ToList();
        var committedPreferences = ClonePreferences(nextPreferences);

        Save(committedGestures, committedPreferences);

        _gestures = committedGestures;
        _preferences = committedPreferences;
        if (gesturesChanged)
        {
            _gesturesVersion++;
        }
    }

    private void Save(IReadOnlyList<GestureCommand> gestures, AppPreferences preferences)
    {
        var payload = new Payload
        {
            FormatVersion = ConfigFormatVersion,
            Gestures = gestures.Select(CloneGesture).ToList(),
            Preferences = ClonePreferences(preferences),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);

        // 写到临时文件再原子重命名,避免半写状态。
        var tmp = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = _configPath + ".replace.bak";
        try
        {
            File.WriteAllBytes(tmp, json);
            if (File.Exists(_configPath))
            {
                TryDelete(backup);
                File.Replace(tmp, _configPath, backup, ignoreMetadataErrors: true);
                TryDelete(backup);
            }
            else
            {
                File.Move(tmp, _configPath);
            }
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort cleanup only
        }
    }

    private sealed class Payload
    {
        /// <summary>旧版配置文件没有该字段 → 反序列化为 0,触发一次性迁移。</summary>
        public int FormatVersion { get; set; }
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
            .Select(CompactTemplate)
            .ToList(),
    };

    private static List<StrokePoint> CompactTemplate(IReadOnlyList<StrokePoint> template)
    {
        if (template.Count <= StoredTemplatePointCount)
        {
            return template.Select(RoundPoint).ToList();
        }

        var pathLength = PathLength(template);
        if (pathLength <= 0)
        {
            return template.Take(StoredTemplatePointCount).Select(RoundPoint).ToList();
        }

        return ResampleTemplate(template, StoredTemplatePointCount, pathLength)
            .Select(RoundPoint)
            .ToList();
    }

    private static IEnumerable<StrokePoint> ResampleTemplate(
        IReadOnlyList<StrokePoint> template,
        int targetCount,
        double knownPathLength)
    {
        var first = template[0];
        yield return first;
        if (targetCount <= 1) yield break;

        var interval = knownPathLength / (targetCount - 1);
        var emitted = 1;
        var accumulated = 0.0;
        var segmentStart = first;

        for (int i = 1; i < template.Count; i++)
        {
            var segmentEnd = template[i];
            var remaining = Distance(segmentStart, segmentEnd);

            while (remaining > 0 && accumulated + remaining >= interval)
            {
                var needed = interval - accumulated;
                var ratio = needed / remaining;
                var point = new StrokePoint(
                    segmentStart.X + ratio * (segmentEnd.X - segmentStart.X),
                    segmentStart.Y + ratio * (segmentEnd.Y - segmentStart.Y));
                yield return point;
                emitted++;
                if (emitted == targetCount) yield break;

                segmentStart = point;
                remaining = Distance(segmentStart, segmentEnd);
                accumulated = 0;
            }

            accumulated += remaining;
            segmentStart = segmentEnd;
        }

        var last = template[^1];
        while (emitted < targetCount)
        {
            yield return last;
            emitted++;
        }
    }

    private static double PathLength(IReadOnlyList<StrokePoint> template)
    {
        double total = 0;
        for (int i = 1; i < template.Count; i++)
        {
            total += Distance(template[i - 1], template[i]);
        }
        return total;
    }

    private static double Distance(StrokePoint a, StrokePoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static StrokePoint RoundPoint(StrokePoint point) => new(
        Math.Round(point.X, StoredCoordinateDigits),
        Math.Round(point.Y, StoredCoordinateDigits));

    private static AppPreferences ClonePreferences(AppPreferences source) => new()
    {
        GesturesEnabled = source.GesturesEnabled,
        ShowTrail = source.ShowTrail,
        ShowTrayIcon = source.ShowTrayIcon,
        RecognitionThreshold = source.RecognitionThreshold,
        GestureTimeoutSeconds = source.GestureTimeoutSeconds,
        ScribbleCancelEnabled = source.ScribbleCancelEnabled,
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
