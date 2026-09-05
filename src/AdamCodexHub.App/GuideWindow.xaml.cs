using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace AdamCodexHub.App;

public partial class GuideWindow : Window
{
    // Architecture diagram natural size (also read from the bitmap at runtime).
    private const double DefaultNaturalWidth = 2050;
    private const double DefaultNaturalHeight = 1750;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 12.0;
    private const double ZoomFactor = 1.2;

    private double _naturalWidth;
    private double _naturalHeight;
    private double _zoom = 1.0;

    /// <summary>
    /// False while the zoom follows the window width automatically (default + after "Fit").
    /// Any manual zoom (+/−/reset/1:1/Ctrl+wheel) pins the user's scale until "Fit" is pressed.
    /// </summary>
    private bool _userZoomed;

    private bool _dragging;
    private Point _dragStart;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;

    public GuideWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) =>
        {
            if (!_userZoomed)
            {
                Dispatcher.BeginInvoke(ApplyFitToWidth);
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureNaturalSize();
        ApplyFitToWidth();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- Zoom helpers ----------------

    private void EnsureNaturalSize()
    {
        if (_naturalWidth > 0)
        {
            return;
        }

        if (ArchImage.Source is BitmapImage bitmap)
        {
            _naturalWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : DefaultNaturalWidth;
            _naturalHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : DefaultNaturalHeight;
        }
        else
        {
            _naturalWidth = DefaultNaturalWidth;
            _naturalHeight = DefaultNaturalHeight;
        }
    }

    /// <summary>Fits the diagram width into the visible viewport, keeping the aspect ratio.</summary>
    private void ApplyFitToWidth()
    {
        EnsureNaturalSize();
        DiagramScroller.UpdateLayout();

        var viewport = DiagramScroller.ViewportWidth;
        if (viewport <= 10 || _naturalWidth <= 0)
        {
            return;
        }

        _userZoomed = false;
        _zoom = Math.Max(MinZoom, (viewport - 6) / _naturalWidth);
        ApplyImageSize();
        DiagramScroller.ScrollToHorizontalOffset(0);
        UpdateZoomLabel();
    }

    /// <summary>Centered zoom for the toolbar +/− buttons.</summary>
    private void ZoomBy(double factor)
    {
        var viewport = DiagramScroller.ViewportWidth;
        ZoomTo(_zoom * factor, viewport / 2.0);
    }

    /// <summary>Zooms while keeping the content point under <paramref name="anchorX"/> (viewport X) stable.</summary>
    private void ZoomTo(double targetZoom, double anchorX)
    {
        var clamped = Math.Clamp(targetZoom, MinZoom, MaxZoom);
        var factor = clamped / _zoom;
        if (Math.Abs(factor - 1.0) < 0.0001)
        {
            return;
        }

        _userZoomed = true;
        var contentX = DiagramScroller.HorizontalOffset + anchorX;
        _zoom = clamped;
        ApplyImageSize();

        DiagramScroller.UpdateLayout();
        var targetOffset = contentX * factor - anchorX;
        DiagramScroller.ScrollToHorizontalOffset(
            Math.Clamp(targetOffset, 0, Math.Max(0, DiagramScroller.ScrollableWidth)));
        UpdateZoomLabel();
    }

    private void ApplyImageSize()
    {
        ArchImage.Width = _naturalWidth * _zoom;
        ArchImage.Height = _naturalHeight * _zoom;
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{(int)Math.Round(_zoom * 100.0)}%";

    // ---------------- Toolbar ----------------

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(ZoomFactor);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1.0 / ZoomFactor);

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _userZoomed = true;
        _zoom = 1.0;
        ApplyImageSize();
        DiagramScroller.ScrollToHorizontalOffset(0);
        UpdateZoomLabel();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ApplyFitToWidth();

    // ---------------- Mouse interactions ----------------

    private void Diagram_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            // Plain wheel keeps scrolling the page (inner viewer is vertical-only-disabled);
            // Ctrl+wheel zooms anchored at the pointer.
            return;
        }

        var position = e.GetPosition(DiagramScroller);
        var factor = e.Delta > 0 ? ZoomFactor : 1.0 / ZoomFactor;
        ZoomTo(_zoom * factor, position.X);
        e.Handled = true;
    }

    private void Diagram_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartOffsetX = DiagramScroller.HorizontalOffset;
        _dragStartOffsetY = DiagramScroller.VerticalOffset;
        DiagramHost.CaptureMouse();
        e.Handled = true;
    }

    private void Diagram_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = e.GetPosition(this);
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;

        // Grabbing the content and moving it right scrolls back (offset decreases).
        DiagramScroller.ScrollToHorizontalOffset(
            Math.Clamp(_dragStartOffsetX - dx, 0, Math.Max(0, DiagramScroller.ScrollableWidth)));
        DiagramScroller.ScrollToVerticalOffset(
            Math.Clamp(_dragStartOffsetY - dy, 0, Math.Max(0, DiagramScroller.ScrollableHeight)));
    }

    private void Diagram_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        DiagramHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Diagram_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        DiagramHost.ReleaseMouseCapture();
    }
}
