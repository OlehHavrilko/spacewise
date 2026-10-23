namespace Spacewise.Core;

// Application-level scan entry point: one scan at a time, state kept in ScanSession.
public interface IScanService
{
    ScanState State { get; }
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation);
}

public sealed class ScanService(IScanner scanner) : IScanService
{
    private readonly ScanSession session = new();
    private readonly Lock gate = new();

    public ScanState State { get { lock (gate) return session.State; } }

    public async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        lock (gate) session.Start();
        ScanState final = ScanState.Failed;
        try
        {
            ScanResult result = await scanner.ScanAsync(request, progress, cancellation);
            final = result.State;
            return result;
        }
        finally { lock (gate) session.Finish(final); }
    }
}
