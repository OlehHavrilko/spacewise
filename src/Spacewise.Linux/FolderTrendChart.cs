using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Spacewise.Core;

namespace Spacewise.Linux;
public sealed class FolderTrendChart : Control
{
    private IReadOnlyList<FolderTrendPoint> points = [];
    public void SetPoints(IReadOnlyList<FolderTrendPoint> value) { points = value; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (points.Count == 0) return;
        double maximum = Math.Max(1, points.Max(p => p.LogicalBytes));
        DateTime start = points[0].TakenUtc;
        double range = Math.Max(1, (points[^1].TakenUtc - start).TotalDays);
        Point? previous = null;
        foreach (FolderTrendPoint p in points)
        {
            var point = new Point(10 + Math.Max(0, Bounds.Width - 20) * (p.TakenUtc - start).TotalDays / range,
                10 + Math.Max(0, Bounds.Height - 45) * (1 - p.LogicalBytes / maximum));
            if (previous is { } from) context.DrawLine(new Pen(Brushes.SteelBlue, 2), from, point);
            context.DrawEllipse(Brushes.SteelBlue, null, point, 3, 3); previous = point;
        }
        string label = $"{start:dd.MM} — {points[^1].TakenUtc:dd.MM} · {DisplayFormat.Size(points[^1].LogicalBytes)}";
        context.DrawText(new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.SlateGray), new Point(10, Bounds.Height - 22));
    }
}
