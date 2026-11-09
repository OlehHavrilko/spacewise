using Spacewise.Inference;

namespace Spacewise.Tests.Unit;
public sealed class AssistantProviderTests
{
    [Fact]
    public async Task Disabled_or_unknown_provider_never_needs_a_model()
    {
        var providers = new AssistantProviderRegistry([]);
        Assert.False(providers.Get("disabled").IsAvailable);
        Assert.False(providers.Get("unknown").IsAvailable);
        AssistantResponse response = await providers.Get("disabled").ExplainAsync("hello", [], TestContext.Current.CancellationToken);
        Assert.Empty(response.Suggestions);
    }
    [Fact]
    public void Profiles_accept_the_pinned_model_and_reject_traversal_or_unverified_transport()
    {
        ModelProfiles.Validate(ModelCatalog.Default);
        Assert.Throws<InvalidDataException>(() => ModelProfiles.Validate(ModelCatalog.Default with { FileName = "../bad.gguf" }));
        Assert.Throws<InvalidDataException>(() => ModelProfiles.Validate(ModelCatalog.Default with { DownloadUri = new Uri("http://example.test/model") }));
        Assert.Throws<InvalidDataException>(() => ModelProfiles.Validate(ModelCatalog.Default with { Sha256 = "" }));
    }
}
