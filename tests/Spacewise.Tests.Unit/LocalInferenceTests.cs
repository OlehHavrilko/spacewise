using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Spacewise.Inference;

namespace Spacewise.Tests.Unit;

public sealed class LocalInferenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SpacewiseModel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Assistant_output_must_reference_known_categories_and_cannot_contain_paths()
    {
        const string valid = """{"summaryRu":"Нашёл кэш.","suggestions":[{"factId":"F001","reasonCode":"regenerated","explanationRu":"Он создаётся заново."}]}""";
        AssistantResponse response = AssistantResponseValidator.Parse(valid, [Fact("F001")]);
        Assert.Equal("F001", Assert.Single(response.Suggestions).FactId);

        const string unknownId = """{"summaryRu":"Итог.","suggestions":[{"factId":"F999","reasonCode":"regenerated","explanationRu":"Кэш."}]}""";
        const string path = """{"summaryRu":"Удалите C:\\Temp\\a.tmp.","suggestions":[]}""";
        Assert.Throws<InvalidDataException>(() => AssistantResponseValidator.Parse(unknownId, [Fact("F001")]));
        Assert.Throws<InvalidDataException>(() => AssistantResponseValidator.Parse(path, [Fact("F001")]));
        const string wrongConsequence = """{"summaryRu":"Итог.","suggestions":[{"factId":"F001","reasonCode":"permanent","explanationRu":"Кэш."}]}""";
        Assert.Throws<InvalidDataException>(() => AssistantResponseValidator.Parse(wrongConsequence, [Fact("F001")]));
        Assert.Equal("Очистите [path hidden]", AssistantResponseValidator.RedactPaths(@"Очистите C:\Users\me\Temp осторожно."));
    }

    [Fact]
    public void Facts_reject_untrusted_ids_paths_and_negative_sizes()
    {
        Assert.Throws<InvalidDataException>(() => AssistantResponseValidator.ValidateFacts(
            [new("F001", @"C:\Users\me", "regenerated", "safe", 1, 10)]));
        Assert.Throws<InvalidDataException>(() => AssistantResponseValidator.ValidateFacts(
            [new("F1", "Кэш", "regenerated", "safe", 1, 10)]));
        Assert.Throws<InvalidDataException>(() => AssistantResponseValidator.ValidateFacts(
            [new("F001", "Кэш", "regenerated", "safe", 1, -1)]));
    }

    [Fact]
    public async Task Installer_verifies_hash_before_activating_model()
    {
        byte[] content = [1, 2, 3, 4, 5, 6];
        string hash = Convert.ToHexString(SHA256.HashData(content));
        var model = Descriptor(content.Length, hash);
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        using var installer = new PinnedModelInstaller(directory, client, model);
        var progress = new ImmediateProgress<ModelDownloadProgress>();

        await installer.InstallAsync(progress, TestContext.Current.CancellationToken);

        Assert.True(installer.IsInstalled);
        Assert.Equal(content, await File.ReadAllBytesAsync(installer.ModelPath, TestContext.Current.CancellationToken));
        Assert.Contains(progress.Values, p => p.DownloadedBytes == content.Length && p.TotalBytes == content.Length);
    }

    [Fact]
    public async Task Installer_resumes_partial_download_and_rejects_bad_hash()
    {
        byte[] content = [1, 2, 3, 4, 5, 6];
        string hash = Convert.ToHexString(SHA256.HashData(content));
        var model = Descriptor(content.Length, hash);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, model.FileName + ".partial"), content[..3], TestContext.Current.CancellationToken);
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(3, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[3..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, content.Length - 1, content.Length);
            return response;
        }));
        using var installer = new PinnedModelInstaller(directory, client, model);

        await installer.InstallAsync(cancellation: TestContext.Current.CancellationToken);

        Assert.True(installer.IsInstalled);
        Assert.Equal(content, await File.ReadAllBytesAsync(installer.ModelPath, TestContext.Current.CancellationToken));

        var badModel = Descriptor(content.Length, new string('0', 64)) with { FileName = "bad.gguf" };
        using var badClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        using var badInstaller = new PinnedModelInstaller(directory, badClient, badModel);
        await Assert.ThrowsAsync<InvalidDataException>(() => badInstaller.InstallAsync(cancellation: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(badInstaller.ModelPath));
        Assert.False(File.Exists(badInstaller.ModelPath + ".partial"));
    }

    private static ModelDescriptor Descriptor(int size, string hash) => new(
        "test-model", "test.gguf", new Uri("https://example.test/model.gguf"), size, hash, "Apache-2.0",
        new Uri("https://example.test/license"));

    private static AssistantFact Fact(string id) => new(id, "Кэш", "regenerated", "Создаётся заново", 1, 10);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
