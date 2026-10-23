using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public class ScanServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ScanRequest Request = new(@"C:\Data", []);
    private static ScanResult Result(ScanState state) => new(Guid.NewGuid(), @"C:\Data", state, 0, 0, 0, 0, [], []);

    private sealed class GatedScanner(Task<ScanResult> result) : IScanner
    {
        public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation) => result;
    }

    private sealed class ThrowingScanner : IScanner
    {
        public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation) =>
            Task.FromException<ScanResult>(new ArgumentException("bad root"));
    }

    [Fact]
    public void Session_rejects_parallel_start_and_invalid_transitions()
    {
        var session = new ScanSession();
        Assert.Throws<InvalidOperationException>(() => session.Finish(ScanState.Completed));
        session.Start();
        Assert.Throws<InvalidOperationException>(session.Start);
        Assert.Throws<InvalidOperationException>(() => session.Finish(ScanState.Scanning));
        session.Finish(ScanState.RootNotFound);
        Assert.Equal(ScanState.RootNotFound, session.State);
        session.Start();
    }

    [Fact]
    public async Task Service_rejects_second_concurrent_scan_and_records_final_state()
    {
        var gate = new TaskCompletionSource<ScanResult>();
        var service = new ScanService(new GatedScanner(gate.Task));
        Task<ScanResult> running = service.ScanAsync(Request, null, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(Request, null, Ct));
        Assert.Equal(ScanState.Scanning, service.State);
        gate.SetResult(Result(ScanState.Partial));
        Assert.Equal(ScanState.Partial, (await running).State);
        Assert.Equal(ScanState.Partial, service.State);
    }

    [Fact]
    public async Task Service_marks_failure_and_accepts_next_scan()
    {
        var service = new ScanService(new ThrowingScanner());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ScanAsync(Request, null, Ct));
        Assert.Equal(ScanState.Failed, service.State);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ScanAsync(Request, null, Ct));
    }
}
