using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Infrastructure.Providers;

public sealed class SqliteProviderStore : IProviderStore
{
    private const string ActiveProviderKey = "active_provider_id";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;

    public SqliteProviderStore(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _database.InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderProfile>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var providers = new List<ProviderProfile>();
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, name, adapter, base_url, trust_level, enabled, health,
       auth_type, auth_header_name, models_endpoint, responses_endpoint,
       chat_completions_endpoint, extra_headers_json, declared_capabilities_json
FROM providers
ORDER BY name COLLATE NOCASE, id COLLATE NOCASE;
";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            providers.Add(ReadProvider(reader));
        }

        return providers;
    }

    public async Task UpsertAsync(
        ProviderProfile provider,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO providers
(id, name, adapter, base_url, trust_level, enabled, health, auth_type,
 auth_header_name, models_endpoint, responses_endpoint, chat_completions_endpoint,
 extra_headers_json, declared_capabilities_json, created_at, updated_at)
VALUES
($id, $name, $adapter, $baseUrl, $trustLevel, $enabled, $health, $authType,
 $authHeaderName, $modelsEndpoint, $responsesEndpoint, $chatCompletionsEndpoint,
 $extraHeadersJson, $declaredCapabilitiesJson, $now, $now)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    adapter = excluded.adapter,
    base_url = excluded.base_url,
    trust_level = excluded.trust_level,
    enabled = excluded.enabled,
    health = excluded.health,
    auth_type = excluded.auth_type,
    auth_header_name = excluded.auth_header_name,
    models_endpoint = excluded.models_endpoint,
    responses_endpoint = excluded.responses_endpoint,
    chat_completions_endpoint = excluded.chat_completions_endpoint,
    extra_headers_json = excluded.extra_headers_json,
    declared_capabilities_json = excluded.declared_capabilities_json,
    updated_at = excluded.updated_at;
";

        var now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$id", provider.Id);
        command.Parameters.AddWithValue("$name", provider.Name);
        command.Parameters.AddWithValue("$adapter", provider.Adapter);
        command.Parameters.AddWithValue("$baseUrl", provider.BaseUrl);
        command.Parameters.AddWithValue("$trustLevel", provider.TrustLevel.ToString());
        command.Parameters.AddWithValue("$enabled", provider.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$health", provider.Health.ToString());
        command.Parameters.AddWithValue("$authType", provider.AuthType);
        command.Parameters.AddWithValue("$authHeaderName", (object?)provider.AuthHeaderName ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelsEndpoint", (object?)provider.ModelsEndpoint ?? DBNull.Value);
        command.Parameters.AddWithValue("$responsesEndpoint", (object?)provider.ResponsesEndpoint ?? DBNull.Value);
        command.Parameters.AddWithValue("$chatCompletionsEndpoint", (object?)provider.ChatCompletionsEndpoint ?? DBNull.Value);
        command.Parameters.AddWithValue("$extraHeadersJson", JsonSerializer.Serialize(provider.ExtraHeaders, JsonOptions));
        command.Parameters.AddWithValue("$declaredCapabilitiesJson", JsonSerializer.Serialize(provider.DeclaredCapabilities, JsonOptions));
        command.Parameters.AddWithValue("$now", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM providers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", providerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<string?> GetActiveProviderIdAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", ActiveProviderKey);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetActiveProviderIdAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO app_state (key, value, updated_at)
VALUES ($key, $value, $now)
ON CONFLICT(key) DO UPDATE SET
    value = excluded.value,
    updated_at = excluded.updated_at;
";
        command.Parameters.AddWithValue("$key", ActiveProviderKey);
        command.Parameters.AddWithValue("$value", providerId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProviderProfile ReadProvider(SqliteDataReader reader)
    {
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(
                reader.GetString(12),
                JsonOptions)
            ?? new Dictionary<string, string>();
        var capabilities = JsonSerializer.Deserialize<List<string>>(
                reader.GetString(13),
                JsonOptions)
            ?? new List<string>();

        return new ProviderProfile
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Adapter = reader.GetString(2),
            BaseUrl = reader.GetString(3),
            TrustLevel = ParseEnum(reader.GetString(4), ProviderTrustLevel.Custom),
            Enabled = reader.GetInt32(5) != 0,
            Health = ParseEnum(reader.GetString(6), ProviderHealth.Unknown),
            AuthType = reader.GetString(7),
            AuthHeaderName = reader.IsDBNull(8) ? null : reader.GetString(8),
            ModelsEndpoint = reader.IsDBNull(9) ? null : reader.GetString(9),
            ResponsesEndpoint = reader.IsDBNull(10) ? null : reader.GetString(10),
            ChatCompletionsEndpoint = reader.IsDBNull(11) ? null : reader.GetString(11),
            ExtraHeaders = headers,
            DeclaredCapabilities = capabilities
        };
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
}
