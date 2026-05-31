using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Velto.Services;
using Velto.Win32;

namespace Velto.UI;

/// <summary>
/// 横跨整个虚拟桌面的全屏透明窗口,画手势轨迹。
///
/// 关键属性:
///   - AllowsTransparency + Background=Transparent → 真透明
///   - WS_EX_TRANSPARENT → 鼠标事件穿透,绝不抢光标
///   - WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE → 不进 Alt+Tab、不抢焦点
///   - Topmost=True,但 ShowActivated=False
///   - Owner = null,避免被设置窗口拉到副本里
///
/// 鼠标钩子是在系统屏幕像素坐标里工作的,这个窗口为了能直接用这套坐标,
/// 直接覆盖虚拟桌面的尺寸并把内部 Canvas 也放在那个坐标系里。
/// </summary>
public partial class TrailOverlayWindow : Window
{
    private bool _blocksInput;

    public TrailOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ApplyVirtualScreenBounds();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
              NativeMethods.WS_EX_TOOLWINDOW  | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
    }

    private void ApplyVirtualScreenBounds()
    {
        // 屏幕像素 → WPF DIP。WPF 默认按设备无关 96dpi,我们要把物理像素换算回 DIP。
        var src = PresentationSource.FromVisual(this);
        var ttd = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var dpiX = ttd.M11 == 0 ? 1.0 : ttd.M11;
        var dpiY = ttd.M22 == 0 ? 1.0 : ttd.M22;

        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        Left = x / dpiX;
        Top = y / dpiY;
        Width = w / dpiX;
        Height = h / dpiY;
        InputShield.Width = Width;
        InputShield.Height = Height;
        _virtualOriginX = x;
        _virtualOriginY = y;
        _dpiX = dpiX;
        _dpiY = dpiY;
        GestureDiagnosticLogger.Info(
            $"overlay_bounds left={Left:0.0} top={Top:0.0} width={Width:0.0} height={Height:0.0} " +
            $"virtual=({x},{y},{w},{h}) dpi=({dpiX:0.000},{dpiY:0.000})");
    }

    private double _virtualOriginX, _virtualOriginY;
    private double _dpiX = 1.0, _dpiY = 1.0;

    public void BeginGesture(IReadOnlyList<Point> points, bool showTrail)
    {
        if (!showTrail)
        {
            EndGesture();
            return;
        }

        EnsureVisible();
        SetBlocksInput(false);
        DrawTrail(points);
    }

    public void UpdateGesture(IReadOnlyList<Point> points, bool showTrail)
    {
        if (!showTrail)
        {
            EndGesture();
            return;
        }

        EnsureVisible();
        SetBlocksInput(false);
        DrawTrail(points);
    }

    public void EndGesture()
    {
        ClearTrail();
        SetBlocksInput(false);
        Visibility = Visibility.Hidden;
        Topmost = false;
    }

    public void Show(IReadOnlyList<Point> points)
    {
        BeginGesture(points, showTrail: true);
    }

    public void Update(IReadOnlyList<Point> points)
    {
        DrawTrail(points);
    }

    public new void Hide()
    {
        EndGesture();
    }

    private void EnsureVisible()
    {
        if (!IsVisible)
        {
            ApplyVirtualScreenBounds();
            Topmost = true;
            Visibility = Visibility.Visible;
        }
    }

    private void SetBlocksInput(bool blocksInput)
    {
        if (_blocksInput == blocksInput) return;
        _blocksInput = blocksInput;

        IsHitTestVisible = blocksInput;
        HostCanvas.IsHitTestVisible = blocksInput;
        InputShield.Visibility = blocksInput ? Visibility.Visible : Visibility.Collapsed;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if (blocksInput)
        {
            ex &= ~NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            ex |= NativeMethods.WS_EX_TRANSPARENT;
        }
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        GestureDiagnosticLogger.Info(
            $"overlay_blocks_input blocks={blocksInput} hwnd=0x{unchecked((ulong)hwnd.ToInt64()):X} ex=0x{unchecked((ulong)ex):X}");
    }

    private void DrawTrail(IReadOnlyList<Point> points)
    {
        if (points.Count < 2)
        {
            ClearTrail();
            return;
        }

        // 鼠标钩子坐标是物理像素;转成本窗口的 DIP
        if (TrailLine.Points.Count > points.Count)
        {
            TrailLine.Points.Clear();
        }

        if (TrailLine.Points.Count == 0)
        {
            for (int i = 0; i < points.Count; i++)
            {
                TrailLine.Points.Add(ToLocal(points[i]));
            }
        }
        else
        {
            // 增量追加,避免每帧重建整条 polyline
            for (int i = TrailLine.Points.Count; i < points.Count; i++)
            {
                TrailLine.Points.Add(ToLocal(points[i]));
            }
        }

        var head = ToLocal(points[^1]);
        ShowHead(head);
    }

    private void ClearTrail()
    {
        TrailLine.Points.Clear();
        HideHead();
    }

    private Point ToLocal(Point screenPx)
        => new((screenPx.X - _virtualOriginX) / _dpiX, (screenPx.Y - _virtualOriginY) / _dpiY);

    private void ShowHead(Point center)
    {
        HeadDotOuter.Visibility = Visibility.Visible;
        HeadDotInner.Visibility = Visibility.Visible;
        Canvas.SetLeft(HeadDotOuter, center.X - HeadDotOuter.Width / 2);
        Canvas.SetTop(HeadDotOuter,  center.Y - HeadDotOuter.Height / 2);
        Canvas.SetLeft(HeadDotInner, center.X - HeadDotInner.Width / 2);
        Canvas.SetTop(HeadDotInner,  center.Y - HeadDotInner.Height / 2);
    }

    private void HideHead()
    {
        HeadDotOuter.Visibility = Visibility.Hidden;
        HeadDotInner.Visibility = Visibility.Hidden;
    }
}
