using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Velto.Models;
using Velto.Services;

namespace Velto.UI;

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly List<GestureCommand> _draftGestures = new();
    private readonly List<GestureRow> _rows = new();
    private AppPreferences _draftPreferences = AppPreferences.Default;
    private bool _draftAutoStart;
    private bool _hasUnsavedChanges;
    private bool _isApplyingDraft;
    private IDisposable? _activeGestureSuspension;

    // XAML 加载 Slider 时会触发 ValueChanged,所以默认先屏蔽事件。
    private bool _suppressEvents = true;

    public SettingsWindow(ConfigStore store)
    {
        _store = store;
        InitializeComponent();

        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Resources/Velto.png", UriKind.Absolute));
        }
        catch { /* 找不到资源就算了 */ }

        SourceInitialized += (_, _) => MicaBackdrop.Apply(this);

        ShortcutBox.ShortcutRecorded += OnShortcutRecorded;
        SampleCanvas.SampleRecorded += OnSampleRecorded;

        ReloadFromStore();

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;

        // 设置窗口活动时暂停全局手势,让右键能落到录制画布(以及让窗口内右键菜单可用)。
        // 失活/关闭时立即恢复,保证别处的手势照常工作。
        Activated += (_, _) => SuspendGesturesWhileActive();
        Deactivated += (_, _) => ResumeGesturesAfterInactive();
        Closed += (_, _) => ResumeGesturesAfterInactive();
    }

    private Guid? SelectedGestureId => (GestureList.SelectedItem as GestureRow)?.Command.Id;

    private void SuspendGesturesWhileActive()
        => _activeGestureSuspension ??= GestureGate.Suspend();

    private void ResumeGesturesAfterInactive()
    {
        _activeGestureSuspension?.Dispose();
        _activeGestureSuspension = null;
    }

    private MessageBoxResult ShowMessageBox(
        string text,
        MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        using var _ = GestureGate.Suspend();
        return MessageBox.Show(this, text, "Velto", buttons, icon);
    }

    private bool ShowFileDialog(FileDialog dialog)
    {
        using var _ = GestureGate.Suspend();
        return dialog.ShowDialog(this) == true;
    }

    private void OnStoreChanged(ConfigStore.ChangeReason reason)
    {
        Dispatcher.Invoke(() =>
        {
            if (_isApplyingDraft) return;
            if (_hasUnsavedChanges)
            {
                DirtyStatusText.Text = "配置已在别处变更,保存会覆盖外部更改";
                return;
            }
            ReloadFromStore(SelectedGestureId);
        });
    }

    private void ReloadFromStore(Guid? selectedId = null)
    {
        _suppressEvents = true;
        try
        {
            _draftGestures.Clear();
            foreach (var gesture in _store.Gestures)
            {
                _draftGestures.Add(CloneGesture(gesture));
            }

            _draftPreferences = ClonePreferences(_store.Preferences);
            _draftAutoStart = AutoStart.IsEnabled;

            RebuildGestureRows(selectedId ?? _draftGestures.FirstOrDefault()?.Id);
            LoadPreferenceControls();

            _hasUnsavedChanges = false;
            UpdateActionButtons();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void RebuildGestureRows(Guid? selectedId)
    {
        _rows.Clear();
        foreach (var gesture in _draftGestures)
        {
            _rows.Add(new GestureRow(gesture));
        }

        GestureList.ItemsSource = null;
        GestureList.ItemsSource = _rows;

        if (_rows.Count == 0)
        {
            GestureList.SelectedIndex = -1;
            ShowEmptyDetail();
            return;
        }

        var idx = selectedId is null ? -1 : _rows.FindIndex(r => r.Command.Id == selectedId);
        GestureList.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void LoadPreferenceControls()
    {
        EnabledCheck.IsOn = _draftPreferences.GesturesEnabled;
        TrailCheck.IsOn = _draftPreferences.ShowTrail;
        PauseFullscreenCheck.IsOn = _draftPreferences.PauseInFullscreen;
        TrayIconCheck.IsOn = _draftPreferences.ShowTrayIcon;
        AutoStartCheck.IsOn = _draftAutoStart;
        ScribbleCancelCheck.IsOn = _draftPreferences.ScribbleCancelEnabled;
        ThresholdSlider.Value = _draftPreferences.RecognitionThreshold;
        ThresholdLabel.Text = _draftPreferences.RecognitionThreshold.ToString("0.00");
        TimeoutSlider.Value = _draftPreferences.GestureTimeoutSeconds;
        TimeoutLabel.Text = $"{_draftPreferences.GestureTimeoutSeconds:0.#}s";
        TargetCombo.SelectedIndex = _draftPreferences.GestureTargetPolicy == GestureTargetPolicy.WindowUnderPointer ? 0 : 1;
    }

    // ───────────────────────── List / Detail ─────────────────────────

    private void OnGestureSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = GestureList.SelectedItem as GestureRow;
        if (row is null)
        {
            ShowEmptyDetail();
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        var wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        try
        {
            NameBox.Text = row.Command.Name;
            ShortcutBox.Value = row.Command.Shortcut;
            SampleCanvas.ShowTemplates(row.Command.Templates);
            UpdateSampleCount(row);
        }
        finally
        {
            _suppressEvents = wasSuppressed;
        }
    }

    private void ShowEmptyDetail()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
        SampleCanvas.Clear();
        SampleCount.Text = "0 个样本";
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var row = GestureList.SelectedItem as GestureRow;
        if (row is null) return;

        row.Command.Name = NameBox.Text;
        row.Refresh();
        MarkDirty();
    }

    private void OnShortcutRecorded(Shortcut sc)
    {
        if (_suppressEvents) return;
        var row = GestureList.SelectedItem as GestureRow;
        if (row is null) return;

        row.Command.Shortcut = sc.VirtualKey == 0 ? null : sc;
        row.Refresh();
        MarkDirty();
    }

    private void OnSampleRecorded(List<StrokePoint> sample)
    {
        if (_suppressEvents) return;
        var row = GestureList.SelectedItem as GestureRow;
        if (row is null) return;

        row.Command.Templates.Add(sample);
        SampleCanvas.ShowTemplates(row.Command.Templates);
        UpdateSampleCount(row);
        MarkDirty();
    }

    private void OnClearSamples(object sender, RoutedEventArgs e)
    {
        var row = GestureList.SelectedItem as GestureRow;
        if (row is null) return;

        row.Command.Templates.Clear();
        SampleCanvas.ShowTemplates(row.Command.Templates);
        UpdateSampleCount(row);
        MarkDirty();
    }

    private void OnClearAllGestures(object sender, RoutedEventArgs e)
    {
        if (_draftGestures.Count == 0) return;
        if (ShowMessageBox("确定清空所有手势和样本吗?", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        _draftGestures.Clear();
        RebuildGestureRows(null);
        MarkDirty();
    }

    private void OnAddGesture(object sender, RoutedEventArgs e)
    {
        var gesture = new GestureCommand { Name = "新手势" };
        _draftGestures.Add(gesture);
        RebuildGestureRows(gesture.Id);
        MarkDirty();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDeleteGesture(object sender, RoutedEventArgs e)
    {
        var row = GestureList.SelectedItem as GestureRow;
        if (row is null) return;
        if (ShowMessageBox($"确定删除手势「{row.Command.Name}」?", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK) return;

        var oldIndex = GestureList.SelectedIndex;
        _draftGestures.RemoveAll(g => g.Id == row.Command.Id);
        Guid? nextId = null;
        if (_draftGestures.Count > 0)
        {
            var nextIndex = Math.Clamp(oldIndex, 0, _draftGestures.Count - 1);
            nextId = _draftGestures[nextIndex].Id;
        }

        RebuildGestureRows(nextId);
        MarkDirty();
    }

    private void UpdateSampleCount(GestureRow row)
        => SampleCount.Text = $"{row.Command.Templates.Count} 个样本";

    // ───────────────────────── Preferences ─────────────────────────

    private void OnPrefsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _draftPreferences.GesturesEnabled = EnabledCheck.IsOn;
        _draftPreferences.ShowTrail = TrailCheck.IsOn;
        _draftPreferences.PauseInFullscreen = PauseFullscreenCheck.IsOn;
        _draftPreferences.ScribbleCancelEnabled = ScribbleCancelCheck.IsOn;
        MarkDirty();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _draftAutoStart = AutoStartCheck.IsOn;
        MarkDirty();
    }

    private void OnTrayIconChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var show = TrayIconCheck.IsOn;
        _draftPreferences.ShowTrayIcon = show;
        if (!show)
        {
            // 隐藏托盘后必须告诉用户怎么回来,否则等于把设置入口锁死了。
            ShowMessageBox(
                "保存后托盘图标会隐藏。\n\n要再次打开本设置窗口,直接重新运行 Velto(双击 Velto.exe)即可 —— 已在运行的实例会把设置窗口唤起。",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        MarkDirty();
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var v = Math.Round(e.NewValue, 2);
        ThresholdLabel.Text = v.ToString("0.00");
        _draftPreferences.RecognitionThreshold = v;
        MarkDirty();
    }

    private void OnTimeoutChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var v = Math.Round(e.NewValue, 1);
        TimeoutLabel.Text = $"{v:0.#}s";
        _draftPreferences.GestureTimeoutSeconds = v;
        MarkDirty();
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _draftPreferences.GestureTargetPolicy = TargetCombo.SelectedIndex == 1
            ? GestureTargetPolicy.ActiveWindow
            : GestureTargetPolicy.WindowUnderPointer;
        MarkDirty();
    }

    // ───────────────────────── Save / Discard ─────────────────────────

    private void OnSaveChanges(object sender, RoutedEventArgs e)
    {
        SaveDraft();
    }

    private void OnDiscardChanges(object sender, RoutedEventArgs e)
    {
        ReloadFromStore(SelectedGestureId);
    }

    private bool SaveDraft()
    {
        var selectedId = SelectedGestureId;
        _isApplyingDraft = true;
        try
        {
            _store.ReplaceSettings(_draftGestures, _draftPreferences);
            AutoStart.SetEnabled(_draftAutoStart);
        }
        catch (Exception ex)
        {
            ShowMessageBox("保存失败:" + ex.Message, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _isApplyingDraft = false;
        }

        ReloadFromStore(selectedId);
        return true;
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (!_hasUnsavedChanges) return;

        var result = ShowMessageBox(
            "有未保存的修改。要先保存吗?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes && !SaveDraft())
        {
            e.Cancel = true;
        }
    }

    private void MarkDirty()
    {
        if (_suppressEvents) return;
        _hasUnsavedChanges = true;
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        SaveButton.IsEnabled = _hasUnsavedChanges;
        DiscardButton.IsEnabled = _hasUnsavedChanges;
        DirtyStatusText.Text = _hasUnsavedChanges ? "有未保存的修改" : "所有修改已保存";
    }

    // ───────────────────────── Import / Export ─────────────────────────

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"Velto-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "JSON (*.json)|*.json",
        };
        if (!ShowFileDialog(dlg)) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, _store.ExportBackup());
        }
        catch (Exception ex)
        {
            ShowMessageBox("导出失败:" + ex.Message, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (!ShowFileDialog(dlg)) return;
        try
        {
            var (gestures, preferences) = _store.ReadBackup(File.ReadAllBytes(dlg.FileName));
            _draftGestures.Clear();
            _draftGestures.AddRange(gestures);
            _draftPreferences = preferences;
            RebuildGestureRows(_draftGestures.FirstOrDefault()?.Id);
            LoadPreferenceControls();
            MarkDirty();
        }
        catch (Exception ex)
        {
            ShowMessageBox("导入失败:" + ex.Message, MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        ScribbleCancelEnabled = source.ScribbleCancelEnabled,
        PauseInFullscreen = source.PauseInFullscreen,
        GestureTargetPolicy = source.GestureTargetPolicy,
    };

    /// <summary>List 数据源行 —— 把 Shortcut 拆成可绑定字符串。</summary>
    public sealed class GestureRow : INotifyPropertyChanged
    {
        public GestureCommand Command { get; }
        public string Name => string.IsNullOrWhiteSpace(Command.Name) ? "(未命名)" : Command.Name;
        public string ShortcutDisplay => Command.Shortcut?.DisplayName ?? "未设置快捷键";

        public GestureRow(GestureCommand command) { Command = command; }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            PropertyChanged?.Invoke(this, new(nameof(ShortcutDisplay)));
        }
    }
}
