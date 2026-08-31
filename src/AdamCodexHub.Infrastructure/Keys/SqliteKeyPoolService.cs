using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Infrastructure.Keys;

public sealed class SqliteKeyPoolService : IKeyPoolService
{
    private readonly SqliteDatabase _database;
    private readonly IKeyVault _vault;

    public SqliteKeyPoolService(SqliteDatabase database, IKeyVault vault)
    {
        _database = database;
        _vault = vault;
    }

    public async Task<ProviderKeyInfo> AddAsync(
        string providerId,
        string label,
        string secret,
        int priority = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        if (priority < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        await _database.InitializeAsync(cancellationToken);

        var id = Guid.NewGuid().ToString("N");
        var reference = await _vault.StoreAsync(providerId, secret, cancellationToken);
        var masked = secret.Length <= 4 ? "••••" : $"••••{secret[^4..]}";

        var info = new ProviderKeyInfo
        {
            Id = id,
            ProviderId = providerId.Trim().ToLowerInvariant(),
            Label = label.Trim(),
            SecretReference = reference,
            Priority = priority,
            Enabled = true,
            Health = KeyHealth.Unknown,
            MaskedDisplay = masked
        };

        try
        {
            await using var connection = new SqliteConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO provider_keys
(id, provider_id, label, secret_reference, priority, enabled, health, masked_display)
VALUES
($id, $providerId, $label, $secretReference, $priority, 1, $health, $masked);
";

            command.Parameters.AddWithValue("$id", info.Id);
            command.Parameters.AddWithValue("$providerId", info.ProviderId);
            command.Parameters.AddWithValue("$label", info.Label);
            command.Parameters.AddWithValue("$secretReference", info.SecretReference);
            command.Parameters.AddWithValue("$priority", info.Priority);
            command.Parameters.AddWithValue("$health", info.Health.ToString());
            command.Parameters.AddWithValue("$masked", info.MaskedDisplay);

            await command.ExecuteNonQueryAsync(cancellationToken);
            return info;
        }
        catch
        {
            await _vault.DeleteAsync(reference, CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProviderKeyInfo>> ListAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken);

        var result = new List<ProviderKeyInfo>();
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, provider_id, label, secret_reference, priority, enabled,
       health, cooldown_until, last_test_at, last_success_at,
       last_failure_at, failure_reason, masked_display
FROM provider_keys
WHERE provider_id = $providerId
ORDER BY priority ASC, label ASC;
";
        command.Parameters.AddWithValue("$providerId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async Task<string?> GetActiveSecretAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        return (await GetActiveAsync(providerId, cancellationToken: cancellationToken))?.Secret;
    }

    public async Task<ProviderKeySelection?> GetActiveAsync(
        string providerId,
        IReadOnlySet<string>? excludedKeyIds = null,
        CancellationToken cancellationToken = default)
    {
        var keys = await ListAsync(providerId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var key in keys)
        {
            if (!key.Enabled ||
                key.Health is KeyHealth.Disabled or KeyHealth.Unauthorized or KeyHealth.QuotaEmpty ||
                (key.Health == KeyHealth.Offline && !key.CooldownUntil.HasValue) ||
                (key.CooldownUntil.HasValue && key.CooldownUntil > now) ||
                (excludedKeyIds?.Contains(key.Id) ?? false))
            {
                continue;
            }

            var secret = await _vault.RetrieveAsync(key.SecretReference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                return new ProviderKeySelection(key, secret);
            }

            await MarkFailureAsync(
                key.Id,
                KeyHealth.Offline,
                "The protected secret could not be retrieved.",
                cancellationToken: cancellationToken);
        }

        return null;
    }

    public async Task SetEnabledAsync(
        string keyId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await _database.InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE provider_keys
SET enabled = $enabled,
    health = CASE
        WHEN $enabled = 0 THEN $disabledHealth
        WHEN health = $disabledHealth THEN $unknownHealth
        ELSE health
    END,
    cooldown_until = CASE WHEN $enabled = 0 THEN NULL ELSE cooldown_until END
WHERE id = $id;
";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$disabledHealth", KeyHealth.Disabled.ToString());
        command.Parameters.AddWithValue("$unknownHealth", KeyHealth.Unknown.ToString());
        command.Parameters.AddWithValue("$id", keyId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException($"API key '{keyId}' was not found.");
        }
    }

    public async Task ReorderAsync(
        string providerId,
        IReadOnlyList<string> orderedKeyIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(orderedKeyIds);

        var current = await ListAsync(providerId, cancellationToken);
        var expected = current.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supplied = orderedKeyIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (orderedKeyIds.Count != supplied.Count || !expected.SetEquals(supplied))
        {
            throw new ArgumentException(
                "The ordered key list must contain every provider key exactly once.",
                nameof(orderedKeyIds));
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        for (var index = 0; index < orderedKeyIds.Count; index++)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE provider_keys
SET priority = $priority
WHERE id = $id AND provider_id = $providerId;
";
            command.Parameters.AddWithValue("$priority", index + 1);
            command.Parameters.AddWithValue("$id", orderedKeyIds[index]);
            command.Parameters.AddWithValue("$providerId", providerId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkSuccessAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await _database.InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE provider_keys
SET health = $health,
    last_test_at = $now,
    last_success_at = $now,
    cooldown_until = NULL,
    failure_reason = NULL
WHERE id = $id;
";
        command.Parameters.AddWithValue("$health", KeyHealth.Healthy.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", keyId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException($"API key '{keyId}' was not found.");
        }
    }

    public async Task MarkFailureAsync(
        string keyId,
        KeyHealth health,
        string? reason,
        TimeSpan? cooldown = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (health is KeyHealth.Healthy or KeyHealth.Unknown)
        {
            throw new ArgumentException("Failure health must represent an unusable or degraded key.", nameof(health));
        }

        await _database.InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE provider_keys
SET health = $health,
    last_test_at = $lastFailureAt,
    last_failure_at = $lastFailureAt,
    failure_reason = $reason,
    cooldown_until = $cooldownUntil
WHERE id = $id;
";

        command.Parameters.AddWithValue("$health", health.ToString());
        command.Parameters.AddWithValue("$lastFailureAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$cooldownUntil",
            cooldown.HasValue
                ? DateTimeOffset.UtcNow.Add(cooldown.Value).ToString("O")
                : DBNull.Value);
        command.Parameters.AddWithValue("$id", keyId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException($"API key '{keyId}' was not found.");
        }
    }

    public async Task<bool> DeleteAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await _database.InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var select = connection.CreateCommand();
        select.CommandText = "SELECT secret_reference FROM provider_keys WHERE id = $id LIMIT 1;";
        select.Parameters.AddWithValue("$id", keyId);
        var secretReference = await select.ExecuteScalarAsync(cancellationToken) as string;
        if (secretReference is null)
        {
            return false;
        }

        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM provider_keys WHERE id = $id;";
        delete.Parameters.AddWithValue("$id", keyId);
        var deleted = await delete.ExecuteNonQueryAsync(cancellationToken) > 0;

        if (deleted)
        {
            await _vault.DeleteAsync(secretReference, cancellationToken);
        }

        return deleted;
    }

    private static ProviderKeyInfo Read(SqliteDataReader reader)
    {
        static DateTimeOffset? ReadDate(SqliteDataReader r, int ordinal) =>
            r.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(r.GetString(ordinal));

        return new ProviderKeyInfo
        {
            Id = reader.GetString(0),
            ProviderId = reader.GetString(1),
            Label = reader.GetString(2),
            SecretReference = reader.GetString(3),
            Priority = reader.GetInt32(4),
            Enabled = reader.GetInt32(5) != 0,
            Health = Enum.TryParse<KeyHealth>(reader.GetString(6), out var health)
                ? health
                : KeyHealth.Unknown,
            CooldownUntil = ReadDate(reader, 7),
            LastTestAt = ReadDate(reader, 8),
            LastSuccessAt = ReadDate(reader, 9),
            LastFailureAt = ReadDate(reader, 10),
            FailureReason = reader.IsDBNull(11) ? null : reader.GetString(11),
            MaskedDisplay = reader.GetString(12)
        };
    }
}
