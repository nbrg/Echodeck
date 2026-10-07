using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Echodeck.App.Controls;

/// <summary>
/// Waveform + timeline with draggable start/end markers, built for trimming in a couple of seconds.
/// <list type="bullet">
/// <item>Drag a marker (or its handle) to move it.</item>
/// <item>Drag across empty space to draw a new selection.</item>
/// <item>Click without dragging to move the nearest marker there.</item>
/// </list>
/// Times are in seconds. Rendering is a single OnRender pass over a precomputed peak array,
/// so it stays cheap even while dragging.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const double MarkerHitPx = 8;
    private const double TimelineHeight = 20;
    private const double MinSelection = 0.05;

    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)));
    private static readonly Brush WaveDim = Frozen(new SolidColorBrush(Color.FromRgb(0x5C, 0x5E, 0x66)));
    private static readonly Brush WaveSelected = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xFF)));
    private static readonly Brush SelectionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x58, 0x65, 0xF2)));
    private static readonly Brush MarkerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB5, 0xBA, 0xC1)));
    private static readonly Pen MarkerPen = Frozen(new Pen(MarkerBrush, 2));
    private static readonly Pen PlayheadPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xB2, 0x32)), 2));
    private static readonly Pen TickPen = Frozen(new Pen(TextBrush, 1));

    private enum DragMode { None, Start, End, Create }
    private DragMode _drag;
    private double _anchor;
    private Point _downPoint;
    private bool _moved;

    public WaveformView()
    {
        Focusable = false;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
    }

    public float[]? Peaks { get => (float[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    // ------------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double waveHeight = Math.Max(10, height - TimelineHeight);
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        double selStartX = TimeToX(SelectionStart), selEndX = TimeToX(SelectionEnd);
        dc.DrawRectangle(SelectionFill, null, new Rect(selStartX, 0, Math.Max(0, selEndX - selStartX), waveHeight));

        DrawWaveform(dc, width, waveHeight, selStartX, selEndX);
        DrawTimeline(dc, width, waveHeight);

        DrawMarker(dc, selStartX, waveHeight);
        DrawMarker(dc, selEndX, waveHeight);

        if (Playhead >= 0 && Duration > 0)
        {
            double x = TimeToX(Playhead);
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, waveHeight));
        }
    }

    private void DrawWaveform(DrawingContext dc, double width, double waveHeight, double selStartX, double selEndX)
    {
        var peaks = Peaks;
        if (peaks is null || peaks.Length == 0) return;
        double mid = waveHeight / 2;
        int columns = (int)Math.Ceiling(width);

        var dim = new StreamGeometry();
        var bright = new StreamGeometry();
        using (var dimCtx = dim.Open())
        using (var brightCtx = bright.Open())
        {
            for (int x = 0; x < columns; x++)
            {
                int from = (int)((long)x * peaks.Length / columns);
                int to = Math.Max(from + 1, (int)((long)(x + 1) * peaks.Length / columns));
                float max = 0f;
                for (int i = from; i < Math.Min(to, peaks.Length); i++) max = Math.Max(max, peaks[i]);

                double h = Math.Max(1, max * (waveHeight - 6));
                var ctx = x >= selStartX && x <= selEndX ? brightCtx : dimCtx;
                ctx.BeginFigure(new Point(x, mid - h / 2), true, true);
                ctx.LineTo(new Point(x + 1, mid - h / 2), false, false);
                ctx.LineTo(new Point(x + 1, mid + h / 2), false, false);
                ctx.LineTo(new Point(x, mid + h / 2), false, false);
            }
        }
        dim.Freeze();
        bright.Freeze();
        dc.DrawGeometry(WaveDim, null, dim);
        dc.DrawGeometry(WaveSelected, null, bright);
    }

    private void DrawTimeline(DrawingContext dc, double width, double waveHeight)
    {
        double duration = Duration;
        if (duration <= 0) return;
        double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60 };
        double step = steps.FirstOrDefault(s => s / duration * width >= 70, 60);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        for (double t = 0; t <= duration + 1e-9; t += step)
        {
            double x = TimeToX(t);
            dc.DrawLine(TickPen, new Point(x, waveHeight), new Point(x, waveHeight + 4));
            string label = step < 1 ? t.ToString("0.0#", CultureInfo.InvariantCulture) + "s" : $"{t:0}s";
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, TextBrush, dpi);
            double tx = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, width - text.Width));
            dc.DrawText(text, new Point(tx, waveHeight + 4));
        }
    }

    private static void DrawMarker(DrawingContext dc, double x, double waveHeight)
    {
        dc.DrawLine(MarkerPen, new Point(x, 0), new Point(x, waveHeight));
        dc.DrawRectangle(MarkerBrush, null, new Rect(x - 5, 0, 10, 10));
        dc.DrawRectangle(MarkerBrush, null, new Rect(x - 5, waveHeight - 10, 10, 10));
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0) return;
        _downPoint = e.GetPosition(this);
        _moved = false;
        double x = _downPoint.X;
        double startX = TimeToX(SelectionStart), endX = TimeToX(SelectionEnd);

        // Prefer the closer marker when both are in range (very short selections).
        if (Math.Abs(x - startX) <= MarkerHitPx || Math.Abs(x - endX) <= MarkerHitPx)
            _drag = Math.Abs(x - startX) <= Math.Abs(x - endX) ? DragMode.Start : DragMode.End;
        else
        {
            _drag = DragMode.Create;
            _anchor = XToTime(x);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        if (_drag == DragMode.None)
        {
            double startX = TimeToX(SelectionStart), endX = TimeToX(SelectionEnd);
            Cursor = Math.Abs(p.X - startX) <= MarkerHitPx || Math.Abs(p.X - endX) <= MarkerHitPx ? Cursors.SizeWE : Cursors.IBeam;
            return;
        }

        if (!_moved && Math.Abs(p.X - _downPoint.X) < 3) return;
        _moved = true;
        double t = XToTime(p.X);
        switch (_drag)
        {
            case DragMode.Start:
                SelectionStart = Math.Min(t, SelectionEnd - MinSelection);
                break;
            case DragMode.End:
                SelectionEnd = Math.Max(t, SelectionStart + MinSelection);
                break;
            case DragMode.Create:
                double a = Math.Min(_anchor, t), b = Math.Max(_anchor, t);
                if (b - a < MinSelection) b = Math.Min(Duration, a + MinSelection);
                SetSelection(a, b);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragMode.None) return;

        if (!_moved && _drag == DragMode.Create)
        {
            // Simple click: move whichever marker is nearer to the click.
            double t = XToTime(_downPoint.X);
            if (Math.Abs(t - SelectionStart) <= Math.Abs(t - SelectionEnd))
                SelectionStart = Math.Min(t, SelectionEnd - MinSelection);
            else
                SelectionEnd = Math.Max(t, SelectionStart + MinSelection);
        }

        _drag = DragMode.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SetSelection(double start, double end)
    {
        // Order matters so the two-way bindings never see start > end.
        if (start < SelectionEnd) { SelectionStart = start; SelectionEnd = end; }
        else { SelectionEnd = end; SelectionStart = start; }
    }

    private double TimeToX(double t) => Duration <= 0 ? 0 : Math.Clamp(t / Duration, 0, 1) * ActualWidth;

    private double XToTime(double x) => ActualWidth <= 0 ? 0 : Math.Clamp(x / ActualWidth, 0, 1) * Duration;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
