namespace Spacewise.Core;

public sealed record VolumeInfo(string Root, string VolumeGuid, string Label, string FileSystem, long TotalBytes, long FreeBytes);

public interface IVolumeCatalog
{
    // Fixed, ready volumes only.
    IReadOnlyList<VolumeInfo> Fixed();
}

public sealed record SpacePoint(DateTime TakenUtc, long FreeBytes);

public enum ForecastKind { NotEnoughData, NotShrinking, Shrinking }

// Days until free space reaches zero. Low and High are an 80 % interval of the trend line's slope.
public sealed record FillForecast(ForecastKind Kind, double? Days, double? LowDays, double? HighDays, double SlopeBytesPerDay)
{
    public const int MinimumPoints = 5;
    public const double MinimumSpanDays = 7;
    public const int WindowDays = 90;

    // Ordinary least squares over the last 90 days (spec §5.4). Too few points or too short a span gives no forecast.
    public static FillForecast From(IReadOnlyList<SpacePoint> points, DateTime nowUtc)
    {
        SpacePoint[] window = [.. points.Where(p => p.TakenUtc >= nowUtc.AddDays(-WindowDays)).OrderBy(p => p.TakenUtc)];
        if (window.Length < MinimumPoints || (window[^1].TakenUtc - window[0].TakenUtc).TotalDays < MinimumSpanDays)
            return new(ForecastKind.NotEnoughData, null, null, null, 0);

        double[] x = [.. window.Select(p => (p.TakenUtc - window[0].TakenUtc).TotalDays)];
        double[] y = [.. window.Select(p => (double)p.FreeBytes)];
        double meanX = x.Average(), meanY = y.Average();
        double sxx = x.Sum(v => (v - meanX) * (v - meanX));
        if (sxx <= 0) return new(ForecastKind.NotEnoughData, null, null, null, 0);
        double slope = x.Zip(y, (a, b) => (a - meanX) * (b - meanY)).Sum() / sxx;
        double intercept = meanY - slope * meanX;
        double residual = x.Zip(y, (a, b) => b - (intercept + slope * a)).Sum(r => r * r);
        double standardError = Math.Sqrt(residual / (window.Length - 2) / sxx);

        double free = window[^1].FreeBytes;
        if (slope >= 0) return new(ForecastKind.NotShrinking, null, null, null, slope);

        const double z80 = 1.2816;
        double fastest = slope - z80 * standardError, slowest = slope + z80 * standardError;
        double days = free / -slope;
        double low = free / -fastest;
        double? high = slowest < 0 ? free / -slowest : null;
        return new(ForecastKind.Shrinking, days, low, high, slope);
    }
}
