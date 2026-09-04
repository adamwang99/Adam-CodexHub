using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Infrastructure.Settings;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AdamCodexHub.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SessionAndProviderAcknowledgementsPersistByVersion()
    {
        var paths = AppPaths.ForRoot(_root);
        var settings = new AppSettingsService(paths);

        await settings.AcknowledgeSessionMechanismAsync(2);
        await settings.AcknowledgeProviderDisclosureAsync("openrouter", 1);

        var restarted = new AppSettingsService(AppPaths.ForRoot(_root));
        Assert.True(await restarted.HasAcknowledgedSessionMechanismAsync(2));
        Assert.False(await restarted.HasAcknowledgedSessionMechanismAsync(3));
        Assert.True(await restarted.HasAcknowledgedProviderDisclosureAsync("openrouter", 1));
        Assert.False(await restarted.HasAcknowledgedProviderDisclosureAsync("openrouter", 2));
        Assert.False(await restarted.HasAcknowledgedProviderDisclosureAsync("deepseek", 1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
