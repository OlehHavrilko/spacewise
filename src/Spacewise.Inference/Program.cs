namespace Spacewise.Inference;

internal static class Program
{
    public static Task<int> Main(string[] args) => InferenceWorker.RunAsync(args);
}
