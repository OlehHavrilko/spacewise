using System.Windows.Threading;

namespace Spacewise.Desktop;

// Coalesce on the producer side: a busy worker cannot fill the dispatcher queue.
internal sealed class UiProgress<T> : IProgress<T>, IDisposable
{
    private readonly Lock gate = new();
    private readonly DispatcherTimer timer;
    private readonly Action<T> apply;
    private T? latest;
    private bool pending, disposed;

    public UiProgress(Action<T> apply)
    {
        this.apply = apply;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += Tick;
        timer.Start();
    }

    public void Report(T value)
    {
        lock (gate)
        {
            if (disposed) return;
            latest = value;
            pending = true;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        T value;
        lock (gate)
        {
            if (!pending || disposed) return;
            value = latest!;
            pending = false;
        }
        apply(value);
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; pending = false; latest = default; }
        timer.Stop();
        timer.Tick -= Tick;
    }
}
