using System.Text.Json;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Paths;

namespace AdamCodexHub.Infrastructure.Settings;

public sealed class AppSettingsService : IAppSettingsService
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AppSettingsService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<bool> HasAcknowledgedSessionMechanismAsync(
        int requiredVersion,
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadAsync(cancellationToken);
        return settings.SessionMechanismAcknowledged &&
               settings.SessionMechanismAckVersion >= requiredVersion;
    }

    public async Task AcknowledgeSessionMechanismAsync(
        int version,
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadAsync(cancellationToken);
        settings.SessionMechanismAcknowledged = true;
        settings.SessionMechanismAckVersion = version;
        settings.SessionMechanismAcknowledgedAt = DateTimeOffset.UtcNow;
        await WriteAsync(settings, cancellationToken);
    }

    private async Task<AppSettingsDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new AppSettingsDocument();
        }

        await using var stream = File.OpenRead(_paths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<AppSettingsDocument>(
                   stream,
                   _json,
                   cancellationToken)
               ?? new AppSettingsDocument();
    }

    private async Task WriteAsync(
        AppSettingsDocument settings,
        CancellationToken cancellationToken)
    {
        var temp = _paths.SettingsFile + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, settings, _json, cancellationToken);
        }

        File.Move(temp, _paths.SettingsFile, overwrite: true);
    }

    private sealed class AppSettingsDocument
    {
        public bool SessionMechanismAcknowledged { get; set; }
        public int SessionMechanismAckVersion { get; set; }
        public DateTimeOffset? SessionMechanismAcknowledgedAt { get; set; }
    }
}
