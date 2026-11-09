using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Spacewise.Core;

namespace Spacewise.Linux;

public sealed class SpaceMap : Control
{
    private IReadOnlyList<FolderAggregate> folders = [];
    public void SetFolders(IReadOnlyList<FolderAggregate> value) { folders = value; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rectangles = TreemapLayout.Arrange(folders.Select(p => (double)p.LogicalBytes).ToArray(), Bounds.Width, Bounds.Height);
        IBrush[] colors = [Brushes.SteelBlue, Brushes.Teal, Brushes.DarkSlateBlue, Brushes.DarkCyan, Brushes.SlateBlue];
        for (int i = 0; i < rectangles.Count; i++)
        {
            MapRectangle r = rectangles[i];
            context.DrawRectangle(colors[i % colors.Length], new Pen(Brushes.White, 2), new Rect(r.X, r.Y, r.Width, r.Height));
            if (r.Width < 100 || r.Height < 40) continue;
            string label = Path.GetFileName(folders[i].Path) + "\n" + DisplayFormat.Size(folders[i].LogicalBytes);
            var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, Brushes.White) { MaxTextWidth = Math.Max(1, r.Width - 12), MaxTextHeight = Math.Max(1, r.Height - 12) };
            context.DrawText(text, new Point(r.X + 6, r.Y + 6));
        }
    }
}
