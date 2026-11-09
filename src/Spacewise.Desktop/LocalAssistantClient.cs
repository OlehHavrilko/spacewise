using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Spacewise.Inference;

namespace Spacewise.Desktop;

public sealed class LocalAssistantClient(PinnedModelInstaller model) : IAssistantProvider
{
    public string Id => model.Model.Id;
    public bool IsAvailable => model.IsInstalled;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 8,
    };

    public async Task<AssistantResponse> ExplainAsync(string question, IReadOnlyList<AssistantFact> facts, CancellationToken cancellation)
    {
        if (!model.IsInstalled) throw new InvalidOperationException(Strings.Get("AI.ModelNotInstalled"));
        if (facts.Count is 0 or > 32) throw new ArgumentOutOfRangeException(nameof(facts));
        AssistantResponseValidator.ValidateFacts(facts);
        string worker = Path.Combine(AppContext.BaseDirectory, "Inference", "Spacewise.Inference.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException(Strings.Get("AI.ProcessMissing"), worker);

        string safeQuestion = AssistantResponseValidator.RedactPaths(question);
        safeQuestion = new(safeQuestion.Where(c => !char.IsControl(c)).Take(500).ToArray());
        var request = new InferenceRequest(model.ModelPath, safeQuestion, facts, model.Model);
        var start = new ProcessStartInfo(worker)
        {
            Arguments = "--worker",
            WorkingDirectory = Path.GetDirectoryName(worker)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException(Strings.Get("AI.ProcessStartFailed"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout;
            string error = await stderr;
            if (process.ExitCode != 0)
            {
                string diagnostic = string.Join(" ", error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
                diagnostic = AssistantResponseValidator.RedactPaths(diagnostic);
                throw new InvalidOperationException(Strings.Get("AI.ProcessFailed") + diagnostic[..Math.Min(diagnostic.Length, 500)]);
            }
            return AssistantResponseValidator.Parse(output.Trim(), facts);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(Strings.Get("AI.Timeout"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
