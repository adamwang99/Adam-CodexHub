using System;
using System.IO;
using AdamCodexHub.Codex;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Covers the Settings switches that drive the provider-switch hand-off: both default on, both
/// persist, and auto-submit can never fire without a fresh chat.
/// </summary>
public sealed class CodexHandoffPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AdamCodexHub.HandoffTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsOpenAFreshChatAndSubmitTheRecap()
    {
        var preferences = CodexHandoffPreferences.Load(_root);

        Assert.True(preferences.OpenFreshChat);
        Assert.True(preferences.AutoSubmit);
        Assert.True(preferences.ShouldSubmitRecap);
    }

    [Fact]
    public void RoundTripsBothSwitches()
    {
        new CodexHandoffPreferences { OpenFreshChat = true, AutoSubmit = false }.Save(_root);

        var loaded = CodexHandoffPreferences.Load(_root);

        Assert.True(loaded.OpenFreshChat);
        Assert.False(loaded.AutoSubmit);
        Assert.False(loaded.ShouldSubmitRecap);
    }

    [Fact]
    public void SubmitStaysOffWhenTheFreshChatIsOff()
    {
        // Auto-submit without a fresh chat would type the recap into whatever chat is on screen.
        var preferences = new CodexHandoffPreferences { OpenFreshChat = false, AutoSubmit = true };

        Assert.False(preferences.ShouldSubmitRecap);
    }

    [Fact]
    public void UnreadableSettingsFallBackToTheDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CodexHandoffPreferences.SettingsPath(_root))!);
        File.WriteAllText(CodexHandoffPreferences.SettingsPath(_root), "{ not json");

        var preferences = CodexHandoffPreferences.Load(_root);

        Assert.True(preferences.OpenFreshChat);
        Assert.True(preferences.AutoSubmit);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort: a locked temp folder must not fail the run.
        }
    }
}
