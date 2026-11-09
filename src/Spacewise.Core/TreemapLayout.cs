namespace Spacewise.Core;

public readonly record struct MapRectangle(double X, double Y, double Width, double Height);

public static class TreemapLayout
{
    public static IReadOnlyList<MapRectangle> Arrange(IReadOnlyList<double> weights, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var rectangles = new MapRectangle[weights.Count];
        var sums = new double[weights.Count + 1];
        for (int i = 0; i < weights.Count; i++)
            sums[i + 1] = sums[i] + (double.IsFinite(weights[i]) ? Math.Max(0, weights[i]) : 0);
        if (!double.IsFinite(sums[^1])) throw new ArgumentOutOfRangeException(nameof(weights));
        if (sums[^1] == 0) return rectangles;
        void Split(int start, int end, MapRectangle area)
        {
            if (end - start == 1) { rectangles[start] = area; return; }
            double total = sums[end] - sums[start];
            int middle = start + 1;
            while (middle < end - 1 && sums[middle] - sums[start] < total / 2) middle++;
            double fraction = total > 0 ? (sums[middle] - sums[start]) / total : 0;
            if (area.Width >= area.Height)
            {
                double cut = area.Width * fraction;
                Split(start, middle, area with { Width = cut });
                Split(middle, end, new(area.X + cut, area.Y, area.Width - cut, area.Height));
            }
            else
            {
                double cut = area.Height * fraction;
                Split(start, middle, area with { Height = cut });
                Split(middle, end, new(area.X, area.Y + cut, area.Width, area.Height - cut));
            }
        }
        Split(0, weights.Count, new(0, 0, width, height));
        return rectangles;
    }
}
