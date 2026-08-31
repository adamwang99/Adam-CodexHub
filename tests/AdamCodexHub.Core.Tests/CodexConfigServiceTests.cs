using AdamCodexHub.Codex;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class CodexConfigServiceTests
{
    [Fact]
    public async Task ActivateGatewayPreservesLegacyProvidersAndCapturesAccountProfile()
    {
        await using var fixture = new ConfigFixture();
        const string original = """
            model = "gpt-account"
            model_provider = "legacy"
            custom_setting = true

            [model_providers.legacy]
            name = "Legacy Provider"
            base_url = "https://legacy.example.test/v1"
            wire_api = "responses"
            """;
        await fixture.WriteConfigAsync(original);

        await fixture.Service.ActivateGatewayAsync("remote-model", 18771);

        var current = Toml.ToModel(await fixture.ReadConfigAsync());
        Assert.Equal("remote-model", current["model"]);
        Assert.Equal("adam_codexhub", current["model_provider"]);
        Assert.Equal(true, current["custom_setting"]);

        var providers = Assert.IsType<TomlTable>(current["model_providers"]);
        Assert.True(providers.ContainsKey("legacy"));
        var gateway = Assert.IsType<TomlTable>(providers["adam_codexhub"]);
        Assert.Equal("http://127.0.0.1:18771/v1", gateway["base_url"]);

        Assert.True(await fixture.Service.HasAccountProfileAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.AccountPath));
    }

    [Fact]
    public async Task RestoreLastKnownGoodRestoresExactPreSwitchConfig()
    {
        await using var fixture = new ConfigFixture();
        const string original = "model = \"gpt-account\"\nmodel_provider = \"openai\"\n";
        await fixture.WriteConfigAsync(original);
        await fixture.Service.ActivateGatewayAsync("remote-model", 18771);
        await fixture.WriteConfigAsync("model = \"manually-changed\"\n");

        await fixture.Service.RestoreLastKnownGoodAsync();

        Assert.Equal(original, await fixture.ReadConfigAsync());
    }

    [Fact]
    public async Task InvalidExistingTomlIsNotOverwrittenOrCapturedAsAccountProfile()
    {
        await using var fixture = new ConfigFixture();
        const string invalid = "model = [\n";
        await fixture.WriteConfigAsync(invalid);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.ActivateGatewayAsync("remote-model", 18771));

        Assert.Equal(invalid, await fixture.ReadConfigAsync());
        Assert.False(await fixture.Service.HasAccountProfileAsync());
    }

    private sealed class ConfigFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AdamCodexHub.Tests",
            Guid.NewGuid().ToString("N"));

        public ConfigFixture()
        {
            Service = CodexConfigService.ForHome(_root);
        }

        public CodexConfigService Service { get; }
        public string AccountPath => Path.Combine(_root, "config-ACCOUNT.toml");
        private string ConfigPath => Path.Combine(_root, "config.toml");

        public Task WriteConfigAsync(string contents) => File.WriteAllTextAsync(ConfigPath, contents);
        public Task<string> ReadConfigAsync() => File.ReadAllTextAsync(ConfigPath);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
