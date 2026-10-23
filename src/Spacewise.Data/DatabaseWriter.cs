using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Spacewise.Data;

// FR-DATA-001: one writer per database. Work runs in order on one background loop that owns the connection.
public sealed class DatabaseWriter : IAsyncDisposable, IDisposable
{
    private readonly Channel<Action> queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SqliteConnection connection;
    private readonly Task loop;

    public DatabaseWriter(SqliteConnection connection)
    {
        this.connection = connection;
        loop = Task.Run(async () =>
        {
            await foreach (Action work in queue.Reader.ReadAllAsync()) work();
        });
    }

    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellation = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            if (cancellation.IsCancellationRequested) { done.TrySetCanceled(cancellation); return; }
            try { done.TrySetResult(work(connection)); }
            catch (Exception e) { done.TrySetException(e); }
        }
        if (!queue.Writer.TryWrite(Run)) throw new ObjectDisposedException(nameof(DatabaseWriter));
        return done.Task;
    }

    // Finishes queued work, then closes the connection.
    public async ValueTask DisposeAsync()
    {
        if (queue.Writer.TryComplete()) await loop;
        await connection.DisposeAsync();
    }

    // For synchronous owners such as a disposed ServiceProvider; the loop runs on the thread pool, so this cannot deadlock.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
