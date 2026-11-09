using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Spacewise.Core;

namespace Spacewise.Desktop;
public sealed class FolderTrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<FolderTrendPoint>),
        typeof(FolderTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<FolderTrendPoint>? Points { get => (IReadOnlyList<FolderTrendPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (Points is not { Count: > 0 } points) return;
        double maximum = Math.Max(1, points.Max(p => p.LogicalBytes));
        DateTime start = points[0].TakenUtc;
        double range = Math.Max(1, (points[^1].TakenUtc - start).TotalDays);
        Point? previous = null;
        foreach (FolderTrendPoint p in points)
        {
            var point = new Point(10 + Math.Max(0, ActualWidth - 20) * (p.TakenUtc - start).TotalDays / range,
                10 + Math.Max(0, ActualHeight - 45) * (1 - p.LogicalBytes / maximum));
            if (previous is { } from) context.DrawLine(new Pen(Brushes.SteelBlue, 2), from, point);
            context.DrawEllipse(Brushes.SteelBlue, null, point, 3, 3); previous = point;
        }
        var text = new FormattedText($"{start:dd.MM} — {points[^1].TakenUtc:dd.MM} · {DisplayFormat.Size(points[^1].LogicalBytes)}", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, (Brush?)TryFindResource("TextBrush") ?? Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(text, new Point(10, ActualHeight - 22));
    }
}
