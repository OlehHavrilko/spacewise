using System.Windows;
using System.Windows.Controls;
using Spacewise.Core;

namespace Spacewise.Desktop;

public sealed class TreemapPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(TreemapPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static void SetWeight(DependencyObject target, double value) => target.SetValue(WeightProperty, value);
    public static double GetWeight(DependencyObject target) => (double)target.GetValue(WeightProperty);

    private IReadOnlyList<MapRectangle> Layout(Size size) => TreemapLayout.Arrange(
        InternalChildren.Cast<UIElement>().Select(GetWeight).ToArray(), size.Width, size.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        var layout = Layout(size);
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(new(layout[i].Width, layout[i].Height));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = Layout(finalSize);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var r = layout[i];
            InternalChildren[i].Arrange(new Rect(r.X, r.Y, r.Width, r.Height));
        }
        return finalSize;
    }
}
