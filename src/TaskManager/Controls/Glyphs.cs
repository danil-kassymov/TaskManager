using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TaskManager.Models;

namespace TaskManager.Controls;

static class Palette
{
    public static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Gray = Brush("#6B6E78");
    public static readonly SolidColorBrush Light = Brush("#9A9CA5");
    public static readonly SolidColorBrush Yellow = Brush("#F0BF00");
    public static readonly SolidColorBrush Accent = Brush("#5E6AD2");
    public static readonly SolidColorBrush Orange = Brush("#F2994A");
    public static readonly SolidColorBrush Bg = Brush("#0F1012");

    public static SolidColorBrush Of(IssueStatus s) => s switch
    {
        IssueStatus.Todo => Light,
        IssueStatus.InProgress => Yellow,
        IssueStatus.Done => Accent,
        _ => Gray,
    };

    public static readonly string[] LabelColors =
        ["#EB5757", "#F2994A", "#F2C94C", "#4CB782", "#26B5CE", "#5E6AD2", "#BB87FC", "#9A9CA5"];
}

/// <summary>Linear-style status circle drawn directly (no templates → cheap in long lists).</summary>
public sealed class StatusGlyph : FrameworkElement
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(IssueStatus), typeof(StatusGlyph),
        new FrameworkPropertyMetadata(IssueStatus.Todo, FrameworkPropertyMetadataOptions.AffectsRender));

    public IssueStatus Status
    {
        get => (IssueStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public StatusGlyph()
    {
        Width = 14;
        Height = 14;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = Math.Min(ActualWidth, ActualHeight) / 2 - 1;
        var brush = Palette.Of(Status);
        switch (Status)
        {
            case IssueStatus.Backlog:
                var dashed = new Pen(brush, 1.5) { DashStyle = new DashStyle([1.4, 1.4], 0) };
                dc.DrawEllipse(null, dashed, c, r, r);
                break;
            case IssueStatus.Todo:
                dc.DrawEllipse(null, new Pen(brush, 1.5), c, r, r);
                break;
            case IssueStatus.InProgress:
                dc.DrawEllipse(null, new Pen(brush, 1.5), c, r, r);
                var inner = r - 2.5;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(c.X, c.Y - inner), true, true);
                    g.ArcTo(new Point(c.X, c.Y + inner), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(brush, null, geo);
                break;
            case IssueStatus.Done:
                dc.DrawEllipse(brush, null, c, r + 0.5, r + 0.5);
                var check = new StreamGeometry();
                using (var g = check.Open())
                {
                    g.BeginFigure(new Point(c.X - r * 0.45, c.Y + r * 0.02), false, false);
                    g.LineTo(new Point(c.X - r * 0.1, c.Y + r * 0.38), true, true);
                    g.LineTo(new Point(c.X + r * 0.5, c.Y - r * 0.35), true, true);
                }
                dc.DrawGeometry(null, new Pen(Palette.Bg, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, check);
                break;
            case IssueStatus.Canceled:
                dc.DrawEllipse(brush, null, c, r + 0.5, r + 0.5);
                var pen = new Pen(Palette.Bg, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var d = r * 0.38;
                dc.DrawLine(pen, new Point(c.X - d, c.Y - d), new Point(c.X + d, c.Y + d));
                dc.DrawLine(pen, new Point(c.X + d, c.Y - d), new Point(c.X - d, c.Y + d));
                break;
        }
    }
}

/// <summary>Signal-bars priority icon; "!" square for urgent, dashes for none.</summary>
public sealed class PriorityGlyph : FrameworkElement
{
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.Register(
        nameof(Priority), typeof(Priority), typeof(PriorityGlyph),
        new FrameworkPropertyMetadata(Priority.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public Priority Priority
    {
        get => (Priority)GetValue(PriorityProperty);
        set => SetValue(PriorityProperty, value);
    }

    public PriorityGlyph()
    {
        Width = 14;
        Height = 14;
    }

    static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        switch (Priority)
        {
            case Priority.None:
                for (var i = 0; i < 3; i++)
                    dc.DrawRoundedRectangle(Palette.Gray, null, new Rect(1 + i * 4.3, h / 2 - 0.75, 3, 1.5), 0.5, 0.5);
                break;
            case Priority.Urgent:
                dc.DrawRoundedRectangle(Palette.Orange, null, new Rect(1, 1, w - 2, h - 2), 3, 3);
                var ft = new FormattedText("!", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, Palette.Bg, 1.25);
                dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
                break;
            default:
                var filled = Priority switch { Priority.High => 3, Priority.Medium => 2, _ => 1 };
                for (var i = 0; i < 3; i++)
                {
                    var barH = 4 + i * 3.5;
                    dc.DrawRoundedRectangle(i < filled ? Palette.Light : Palette.Gray, null,
                        new Rect(1.5 + i * 4.2, h - 1.5 - barH, 3, barH), 0.8, 0.8);
                }
                break;
        }
    }
}

/// <summary>"#RRGGBB" → brush, for colors stored in the database.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    static readonly Dictionary<string, SolidColorBrush> Cache = [];

    public static SolidColorBrush Get(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Palette.Gray;
        if (!Cache.TryGetValue(hex, out var b))
        {
            try { b = Palette.Brush(hex); }
            catch (FormatException) { b = Palette.Gray; }
            Cache[hex] = b;
        }
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Get(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        IssueStatus s => Names.Of(s),
        Priority p => Names.Of(p),
        _ => "",
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
