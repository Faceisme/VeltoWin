using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Velto.Models;

namespace Velto.UI;

/// <summary>
/// 录入手势样本的画布。右键按下拖动 → 一条样本(和实际触发手势时一样用右键,手感一致)。
/// 支持显示已有样本(只读叠加显示)和当前正在录的样本(高亮)。
///
/// 右键能落到这里的前提:设置窗口激活时 <see cref="Velto.Services.GestureGate"/> 会暂停全局钩子,
/// 否则右键会被钩子吞掉,WPF 收不到 MouseRightButtonDown。
/// </summary>
public sealed class GestureSampleCanvas : Border
{
    private readonly Canvas _canvas;
    private readonly Polyline _live;
    private bool _drawing;
    private List<Point> _currentPoints = new();

    public event Action<List<StrokePoint>>? SampleRecorded;

    public GestureSampleCanvas()
    {
        // 画布表面走主题资源,深浅色自动适配
        SetResourceReference(BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");
        SetResourceReference(BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        ClipToBounds = true;
        MinHeight = 200;

        _canvas = new Canvas();
        Child = _canvas;

        _live = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0xB8)),
            StrokeThickness = 4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _canvas.Children.Add(_live);

        MouseRightButtonDown += OnDown;
        MouseRightButtonUp += OnUp;
        MouseMove += OnMove;
        MouseLeave += (_, _) => { if (_drawing) Finish(); };
    }

    /// <summary>清空显示。已存的样本通过 <see cref="ShowTemplates"/> 重画。</summary>
    public void Clear()
    {
        _live.Points.Clear();
        // 移除所有静态样本(用 Tag 区分)
        for (int i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            if (_canvas.Children[i] is Polyline pl && pl != _live)
            {
                _canvas.Children.RemoveAt(i);
            }
        }
    }

    /// <summary>把多个样本叠加显示成淡色 polyline。</summary>
    public void ShowTemplates(IReadOnlyList<IReadOnlyList<StrokePoint>> templates)
    {
        Clear();
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            // Layout 还没完成,等一会儿再画
            Loaded += DeferredDraw;
            return;

            void DeferredDraw(object? s, RoutedEventArgs e)
            {
                Loaded -= DeferredDraw;
                ShowTemplates(templates);
            }
        }

        foreach (var tpl in templates)
        {
            if (tpl.Count < 2) continue;
            var fit = FitToCanvas(tpl);
            var pl = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0xB8, 0xB8)),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            foreach (var p in fit) pl.Points.Add(p);
            _canvas.Children.Insert(0, pl);
        }
    }

    private List<Point> FitToCanvas(IReadOnlyList<StrokePoint> source)
    {
        var w = ActualWidth - 24;
        var h = ActualHeight - 24;
        if (source.Count == 0) return new();
        double minX = source[0].X, maxX = source[0].X;
        double minY = source[0].Y, maxY = source[0].Y;
        foreach (var p in source)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        var dx = Math.Max(1, maxX - minX);
        var dy = Math.Max(1, maxY - minY);
        var scale = Math.Min(w / dx, h / dy);
        var offX = (ActualWidth - dx * scale) / 2 - minX * scale;
        var offY = (ActualHeight - dy * scale) / 2 - minY * scale;
        var result = new List<Point>(source.Count);
        foreach (var p in source)
        {
            result.Add(new Point(p.X * scale + offX, p.Y * scale + offY));
        }
        return result;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _drawing = true;
        _currentPoints = new List<Point>();
        _live.Points.Clear();
        var p = e.GetPosition(_canvas);
        _currentPoints.Add(p);
        _live.Points.Add(p);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;
        var p = e.GetPosition(_canvas);
        if (_currentPoints.Count > 0)
        {
            var prev = _currentPoints[^1];
            var dx = prev.X - p.X;
            var dy = prev.Y - p.Y;
            if (dx * dx + dy * dy < 4) return; // 2px 最小记录间距
        }
        _currentPoints.Add(p);
        _live.Points.Add(p);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        Finish();
        e.Handled = true;
    }

    private void Finish()
    {
        _drawing = false;
        ReleaseMouseCapture();
        if (_currentPoints.Count >= 2)
        {
            var pts = _currentPoints.Select(p => new StrokePoint(p.X, p.Y)).ToList();
            SampleRecorded?.Invoke(pts);
        }
        _live.Points.Clear();
    }
}
