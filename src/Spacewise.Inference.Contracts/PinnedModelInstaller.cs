using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Spacewise.Inference;

public sealed record ModelDescriptor(string Id, string FileName, Uri DownloadUri, long SizeBytes, string Sha256, string License, Uri LicenseUri);
public sealed record ModelDownloadProgress(long DownloadedBytes, long TotalBytes);

public static class ModelCatalog
{
    public static readonly ModelDescriptor Default = new(
        "qwen3.5-2b-q4_k_m",
        "Qwen3.5-2B-Q4_K_M.gguf",
        new("https://huggingface.co/lmstudio-community/Qwen3.5-2B-GGUF/resolve/bb84e11355a036e28f080c7793fa6d22b7c4e344/Qwen3.5-2B-Q4_K_M.gguf?download=true"),
        1_270_808_032,
        "0bfe35afc9f05b7fac3fa04925e051ac7939a42a8a17ea11afc99701bea826cc",
        "Apache-2.0",
        new("https://huggingface.co/Qwen/Qwen3.5-2B"));
}

public sealed class PinnedModelInstaller : IDisposable
{
    private readonly string directory;
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public PinnedModelInstaller(string directory, HttpClient? client = null, ModelDescriptor? model = null)
    {
        this.directory = directory;
        this.client = client ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        ownsClient = client is null;
        Model = model ?? ModelCatalog.Default;
    }

    public ModelDescriptor Model { get; }
    public string ModelPath => Path.Combine(directory, Model.FileName);
    public bool IsInstalled => File.Exists(ModelPath)
        && new FileInfo(ModelPath).Length == Model.SizeBytes
        && File.Exists(ModelPath + ".sha256")
        && string.Equals(File.ReadAllText(ModelPath + ".sha256").Trim(), Model.Sha256, StringComparison.OrdinalIgnoreCase);

    public async Task InstallAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(directory);
        string partial = ModelPath + ".partial";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > Model.SizeBytes)
        {
            File.Delete(partial);
            offset = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Model.DownloadUri);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent
            || response.Content.Headers.ContentRange?.From != offset))
        {
            response.Dispose();
            offset = 0;
            using var restart = new HttpRequestMessage(HttpMethod.Get, Model.DownloadUri);
            using HttpResponseMessage restarted = await client.SendAsync(restart, HttpCompletionOption.ResponseHeadersRead, cancellation);
            restarted.EnsureSuccessStatusCode();
            await DownloadAsync(restarted, partial, 0, progress, cancellation);
            await FinalizeAsync(partial, cancellation);
            return;
        }
        response.EnsureSuccessStatusCode();
        await DownloadAsync(response, partial, offset, progress, cancellation);
        await FinalizeAsync(partial, cancellation);
    }

    private async Task DownloadAsync(HttpResponseMessage response, string partial, long offset,
        IProgress<ModelDownloadProgress>? progress, CancellationToken cancellation)
    {
        long downloaded = offset;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastReported = 0;
        await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellation);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            downloaded = checked(downloaded + read);
            if (downloaded > Model.SizeBytes) throw new InvalidDataException("Загружаемая модель больше закреплённого размера.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
            if (downloaded == Model.SizeBytes || clock.ElapsedMilliseconds - lastReported >= 250)
            {
                progress?.Report(new(downloaded, Model.SizeBytes));
                lastReported = clock.ElapsedMilliseconds;
            }
        }
        await output.FlushAsync(cancellation);
        if (downloaded != Model.SizeBytes) throw new EndOfStreamException("Загрузка модели завершилась раньше времени; её можно продолжить.");
    }

    private async Task FinalizeAsync(string partial, CancellationToken cancellation)
    {
        await using var input = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        byte[] actual = await SHA256.HashDataAsync(input, cancellation);
        byte[] expected = Convert.FromHexString(Model.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            input.Close();
            File.Delete(partial);
            throw new InvalidDataException("SHA-256 модели не совпал с закреплённым значением.");
        }

        input.Close();
        File.Move(partial, ModelPath, overwrite: true);
        await File.WriteAllTextAsync(ModelPath + ".sha256", Model.Sha256 + Environment.NewLine, cancellation);
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }
}
