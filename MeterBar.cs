using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NdiAudioRouter;

/// <summary>
/// Vúmetre vertical fi: pista arrodonida, farcit en segments (verd → groc → vermell), retenció de pic
/// i escala en dB amb la marca de -18 dB (nivell d'ajust) ressaltada.
/// Level i Peak van de 0 a 1 sobre una escala de -60 a 0 dBFS.
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DimmedProperty = DependencyProperty.Register(
        nameof(Dimmed), typeof(bool), typeof(MeterBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    public bool Dimmed { get => (bool)GetValue(DimmedProperty); set => SetValue(DimmedProperty, value); }

    const double BarW = 8;            // gruix de la barra (fina)
    const double Pad = 6;             // marge superior/inferior per no tallar les etiquetes
    static readonly double[] Marks = { 0, -6, -12, -18, -24, -36, -48 };

    static readonly Color TrackColor = Color.FromRgb(0x0B, 0x0D, 0x11);
    static readonly Brush TrackBrush = Frozen(new SolidColorBrush(TrackColor));
    internal static readonly Brush FillBrush = MakeFill();
    static readonly Brush SegBrush = MakeSegments();
    static readonly Brush PeakBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xF7)));
    static readonly Brush ClipBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB1)));
    static readonly Brush AlignBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));
    static readonly Pen TickPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)), 1));
    static readonly Pen AlignPen = Frozen(new Pen(AlignBrush, 1.5));

    FormattedText[]? _labels;
    double _labelDpi;

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static Brush MakeFill()
    {
        // La pista fa 60 dB: el verd arriba fins a -18 dB (0,70), groc cap a -9 dB, vermell al final.
        var b = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.00));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.70));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xA3, 0xD6, 0x3A), 0.80));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xFA, 0xCC, 0x15), 0.90));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.96));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 1.00));
        return Frozen(b);
    }

    static Brush MakeSegments()
    {
        // Línia fosca d'1 px cada 4 px: dona l'aspecte de LEDs
        var drawing = new GeometryDrawing(TrackBrush, null, new RectangleGeometry(new Rect(0, 3, 10, 1)));
        drawing.Freeze();
        var b = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 10, 4),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 10, 4),
            ViewportUnits = BrushMappingMode.Absolute
        };
        return Frozen(b);
    }

    FormattedText[] Labels()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_labels != null && Math.Abs(_labelDpi - dpi) < 0.001) return _labels;
        var tf = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _labels = Marks.Select(m => new FormattedText(
            m.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            tf, 10, Math.Abs(m + 18) < 0.01 ? AlignBrush : LabelBrush, dpi)).ToArray();
        _labelDpi = dpi;
        return _labels;
    }

    static double Norm(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= BarW || h <= 2 * Pad + 10) return;

        double barX = Math.Round((w - BarW) / 2 - 4), top = Pad, barH = h - 2 * Pad;
        var track = new Rect(barX, top, BarW, barH);
        double r = BarW / 2;

        // Pista
        dc.DrawRoundedRectangle(TrackBrush, null, track, r, r);

        // Farcit (el degradat omple tota la pista; es retalla fins al nivell)
        double lvl = Math.Clamp(Level, 0, 1);
        if (lvl > 0.002)
        {
            double fy = top + barH * (1 - lvl);
            if (Dimmed) dc.PushOpacity(0.3);
            dc.PushClip(new RectangleGeometry(track, r, r));
            dc.PushClip(new RectangleGeometry(new Rect(barX, fy, BarW, top + barH - fy)));
            dc.DrawRectangle(FillBrush, null, track);
            dc.Pop();
            dc.Pop();
            if (Dimmed) dc.Pop();
        }

        // Segments (aspecte LED) sobre tota la barra
        dc.PushClip(new RectangleGeometry(track, r, r));
        dc.DrawRectangle(SegBrush, null, track);
        dc.Pop();

        // Retenció de pic
        double pk = Math.Clamp(Peak, 0, 1);
        if (pk > 0.002)
        {
            double py = top + barH * (1 - pk);
            var brush = pk > Norm(-3) ? ClipBrush : PeakBrush;
            if (Dimmed) dc.PushOpacity(0.35);
            dc.DrawRoundedRectangle(brush, null, new Rect(barX - 2, py - 1, BarW + 4, 2), 1, 1);
            if (Dimmed) dc.Pop();
        }

        // Escala: marques a banda i banda i etiquetes a la dreta
        var labels = Labels();
        for (int i = 0; i < Marks.Length; i++)
        {
            double y = top + barH * (1 - Norm(Marks[i]));
            bool align = Math.Abs(Marks[i] + 18) < 0.01;
            double len = align ? 6 : 3;
            var pen = align ? AlignPen : TickPen;
            dc.DrawLine(pen, new Point(barX - 3, y), new Point(barX - 3 - len, y));
            dc.DrawLine(pen, new Point(barX + BarW + 3, y), new Point(barX + BarW + 3 + len, y));
            dc.DrawText(labels[i], new Point(barX + BarW + 3 + len + 3, y - labels[i].Height / 2));
        }
    }
}
