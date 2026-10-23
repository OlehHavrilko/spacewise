using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class VolumeForecastTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewiseVol-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1L << 30;

    public VolumeForecastTests() => Directory.CreateDirectory(dir);

    // Only files the tests created in their own directory; no recursion.
    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        Directory.Delete(dir);
    }

    private static SpacePoint[] Series(int days, long startFree, long dailyDrop, Func<int, long>? noise = null) =>
        [.. Enumerable.Range(0, days).Select(d => new SpacePoint(Now.AddDays(d - days + 1), startFree - d * dailyDrop + (noise?.Invoke(d) ?? 0)))];

    [Fact]
    public void Steady_loss_gives_days_until_full_with_a_tight_interval()
    {
        FillForecast forecast = FillForecast.From(Series(30, 100 * Gb, Gb), Now);   // 1 GB a day, 71 GB left
        Assert.Equal(ForecastKind.Shrinking, forecast.Kind);
        Assert.InRange(forecast.Days!.Value, 70.5, 71.5);
        Assert.InRange(forecast.LowDays!.Value, 70, 71.5);
        Assert.InRange(forecast.HighDays!.Value, 70.5, 72);
    }

    [Fact]
    public void Noisy_series_widens_the_interval_around_the_estimate()
    {
        FillForecast forecast = FillForecast.From(Series(40, 200 * Gb, Gb, d => (d % 3 - 1) * 8 * Gb), Now);
        Assert.Equal(ForecastKind.Shrinking, forecast.Kind);
        Assert.True(forecast.LowDays < forecast.Days && forecast.Days < forecast.HighDays);
    }

    [Fact]
    public void Growing_or_flat_free_space_is_not_shrinking()
    {
        Assert.Equal(ForecastKind.NotShrinking, FillForecast.From(Series(20, 50 * Gb, -Gb), Now).Kind);
        Assert.Equal(ForecastKind.NotShrinking, FillForecast.From(Series(20, 50 * Gb, 0), Now).Kind);
    }

    [Fact]
    public void Few_points_or_a_short_span_give_no_forecast()
    {
        Assert.Equal(ForecastKind.NotEnoughData, FillForecast.From(Series(4, 50 * Gb, Gb), Now).Kind);
        Assert.Equal(ForecastKind.NotEnoughData, FillForecast.From(Series(6, 50 * Gb, Gb), Now).Kind);
        Assert.Equal(ForecastKind.NotEnoughData, FillForecast.From([], Now).Kind);
    }

    [Fact]
    public void Only_the_last_ninety_days_count()
    {
        SpacePoint[] old = [.. Enumerable.Range(0, 60).Select(d => new SpacePoint(Now.AddDays(-200 + d), 500 * Gb - d * 5 * Gb))];
        SpacePoint[] recent = Series(20, 100 * Gb, Gb);
        FillForecast forecast = FillForecast.From([.. old, .. recent], Now);
        Assert.InRange(forecast.SlopeBytesPerDay, -1.01 * Gb, -0.99 * Gb);
    }

    private sealed class Keys : IDatabaseKeySource
    {
        public byte[] GetKey(string database) => Enumerable.Repeat((byte)4, 32).ToArray();
        public void Reset(string database) { }
    }

    [Fact]
    public async Task Snapshots_are_stored_per_volume_skipped_when_too_close_and_erasable()
    {
        await using var writer = new DatabaseWriter(SqliteDatabase.Open(Path.Combine(dir, "cache.db"), Schemas.Cache, new Keys()).Connection);
        var history = new VolumeHistory(writer);
        VolumeInfo c = new(@"C:\", @"\\?\Volume{aaa}\", "OS", "NTFS", 500 * Gb, 100 * Gb), d = new(@"D:\", @"\\?\Volume{bbb}\", "Data", "NTFS", 1000 * Gb, 400 * Gb);
        var ct = TestContext.Current.CancellationToken;

        await history.RecordAsync([c, d], Now, cancellation: ct);
        await history.RecordAsync([c with { FreeBytes = 99 * Gb }], Now.AddMinutes(10), cancellation: ct);
        await history.RecordAsync([c with { FreeBytes = 98 * Gb }], Now.AddDays(1), cancellation: ct);

        Assert.Equal([100 * Gb, 98 * Gb], (await history.PointsAsync(c.VolumeGuid, ct)).Select(p => p.FreeBytes));
        Assert.Equal([400 * Gb], (await history.PointsAsync(d.VolumeGuid, ct)).Select(p => p.FreeBytes));
        Assert.Equal(3, await history.EraseAsync(ct));
        Assert.Empty(await history.PointsAsync(c.VolumeGuid, ct));
    }
}
