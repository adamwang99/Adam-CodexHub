using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Infrastructure.Models;

public sealed class SqliteModelStore : IModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;

    public SqliteModelStore(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _database.InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<ModelDescriptor>> GetAllAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await InitializeAsync(cancellationToken);

        var models = new List<ModelDescriptor>();
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE provider_id = $providerId ORDER BY display_name COLLATE NOCASE, remote_id COLLATE NOCASE;";
        command.Parameters.AddWithValue("$providerId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            models.Add(ReadModel(reader));
        }

        return models;
    }

    public async Task<ModelDescriptor?> GetAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE provider_id = $providerId AND remote_id = $modelId LIMIT 1;";
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue("$modelId", modelId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadModel(reader) : null;
    }

    public async Task UpsertAsync(
        ModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.RemoteId);
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO models
(provider_id, remote_id, display_name, state, enabled, input_modalities_json,
 capabilities_json, context_window, last_seen_at, last_verified_at, compatibility_score)
VALUES
($providerId, $remoteId, $displayName, $state, $enabled, $inputModalitiesJson,
 $capabilitiesJson, $contextWindow, $lastSeenAt, $lastVerifiedAt, $compatibilityScore)
ON CONFLICT(provider_id, remote_id) DO UPDATE SET
    display_name = excluded.display_name,
    state = excluded.state,
    enabled = excluded.enabled,
    input_modalities_json = excluded.input_modalities_json,
    capabilities_json = excluded.capabilities_json,
    context_window = excluded.context_window,
    last_seen_at = excluded.last_seen_at,
    last_verified_at = excluded.last_verified_at,
    compatibility_score = excluded.compatibility_score;
";
        AddModelParameters(command, model);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkUnavailableExceptAsync(
        string providerId,
        IReadOnlySet<string> seenModelIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(seenModelIds);
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();

        var conditions = new List<string>();
        var index = 0;
        foreach (var modelId in seenModelIds)
        {
            var parameterName = $"$seen{index++}";
            conditions.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, modelId);
        }

        command.CommandText = $@"
UPDATE models
SET state = $unavailable,
    enabled = 0
WHERE provider_id = $providerId
{(conditions.Count == 0 ? string.Empty : $"AND remote_id NOT IN ({string.Join(", ", conditions)})")};
";
        command.Parameters.AddWithValue("$unavailable", ModelLifecycleState.Unavailable.ToString());
        command.Parameters.AddWithValue("$providerId", providerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(
        string providerId,
        string modelId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var model = await GetAsync(providerId, modelId, cancellationToken)
            ?? throw new InvalidOperationException($"Model '{modelId}' was not found for provider '{providerId}'.");

        if (enabled && (!model.LastVerifiedAt.HasValue || model.CompatibilityScore is null or <= 0))
        {
            throw new InvalidOperationException("A model must pass compatibility verification before it can be enabled.");
        }

        if (model.State == ModelLifecycleState.Unavailable && enabled)
        {
            throw new InvalidOperationException("An unavailable model cannot be enabled.");
        }

        await UpsertAsync(
            model with
            {
                Enabled = enabled,
                State = enabled ? ModelLifecycleState.Enabled : ModelLifecycleState.Disabled
            },
            cancellationToken);
    }

    public async Task SaveCompatibilityAsync(
        CompatibilityResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var model = await GetAsync(result.ProviderId, result.ModelId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Model '{result.ModelId}' was not found for provider '{result.ProviderId}'.");

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"
INSERT INTO compatibility_results
(provider_id, model_id, verified_at, text_supported, responses_supported,
 chat_completions_supported, streaming_supported, tool_calling_supported,
 structured_json_supported, vision_supported, score, notes)
VALUES
($providerId, $modelId, $verifiedAt, $text, $responses,
 $chatCompletions, $streaming, $toolCalling,
 $structuredJson, $vision, $score, $notes);
";
        AddCompatibilityParameters(insert, result);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = @"
UPDATE models
SET state = $state,
    enabled = CASE WHEN $score <= 0 THEN 0 ELSE enabled END,
    last_verified_at = $verifiedAt,
    compatibility_score = $score
WHERE provider_id = $providerId AND remote_id = $modelId;
";
        update.Parameters.AddWithValue(
            "$state",
            result.Score > 0
                ? model.Enabled
                    ? ModelLifecycleState.Enabled.ToString()
                    : ModelLifecycleState.Verified.ToString()
                : ModelLifecycleState.Failed.ToString());
        update.Parameters.AddWithValue("$score", result.Score);
        update.Parameters.AddWithValue("$verifiedAt", result.VerifiedAt.ToString("O"));
        update.Parameters.AddWithValue("$providerId", result.ProviderId);
        update.Parameters.AddWithValue("$modelId", result.ModelId);
        await update.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CompatibilityResult?> GetLatestCompatibilityAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
SELECT provider_id, model_id, verified_at, text_supported, responses_supported,
       chat_completions_supported, streaming_supported, tool_calling_supported,
       structured_json_supported, vision_supported, score, notes
FROM compatibility_results
WHERE provider_id = $providerId AND model_id = $modelId
ORDER BY verified_at DESC
LIMIT 1;
";
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue("$modelId", modelId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CompatibilityResult
        {
            ProviderId = reader.GetString(0),
            ModelId = reader.GetString(1),
            VerifiedAt = DateTimeOffset.Parse(reader.GetString(2)),
            Text = reader.GetInt32(3) != 0,
            Responses = reader.GetInt32(4) != 0,
            ChatCompletions = reader.GetInt32(5) != 0,
            Streaming = reader.GetInt32(6) != 0,
            ToolCalling = reader.GetInt32(7) != 0,
            StructuredJson = reader.GetInt32(8) != 0,
            Vision = reader.GetInt32(9) != 0,
            Score = reader.GetInt32(10),
            Notes = reader.IsDBNull(11) ? null : reader.GetString(11)
        };
    }

    private const string SelectColumns = @"
SELECT provider_id, remote_id, display_name, state, enabled,
       input_modalities_json, capabilities_json, context_window,
       last_seen_at, last_verified_at, compatibility_score
FROM models";

    private static void AddModelParameters(SqliteCommand command, ModelDescriptor model)
    {
        command.Parameters.AddWithValue("$providerId", model.ProviderId);
        command.Parameters.AddWithValue("$remoteId", model.RemoteId);
        command.Parameters.AddWithValue(
            "$displayName",
            string.IsNullOrWhiteSpace(model.DisplayName) ? model.RemoteId : model.DisplayName);
        command.Parameters.AddWithValue("$state", model.State.ToString());
        command.Parameters.AddWithValue("$enabled", model.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$inputModalitiesJson", JsonSerializer.Serialize(model.InputModalities, JsonOptions));
        command.Parameters.AddWithValue("$capabilitiesJson", JsonSerializer.Serialize(model.Capabilities, JsonOptions));
        command.Parameters.AddWithValue("$contextWindow", (object?)model.ContextWindow ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastSeenAt", ToDb(model.LastSeenAt));
        command.Parameters.AddWithValue("$lastVerifiedAt", ToDb(model.LastVerifiedAt));
        command.Parameters.AddWithValue("$compatibilityScore", (object?)model.CompatibilityScore ?? DBNull.Value);
    }

    private static void AddCompatibilityParameters(SqliteCommand command, CompatibilityResult result)
    {
        command.Parameters.AddWithValue("$providerId", result.ProviderId);
        command.Parameters.AddWithValue("$modelId", result.ModelId);
        command.Parameters.AddWithValue("$verifiedAt", result.VerifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$text", result.Text ? 1 : 0);
        command.Parameters.AddWithValue("$responses", result.Responses ? 1 : 0);
        command.Parameters.AddWithValue("$chatCompletions", result.ChatCompletions ? 1 : 0);
        command.Parameters.AddWithValue("$streaming", result.Streaming ? 1 : 0);
        command.Parameters.AddWithValue("$toolCalling", result.ToolCalling ? 1 : 0);
        command.Parameters.AddWithValue("$structuredJson", result.StructuredJson ? 1 : 0);
        command.Parameters.AddWithValue("$vision", result.Vision ? 1 : 0);
        command.Parameters.AddWithValue("$score", Math.Clamp(result.Score, 0, 100));
        command.Parameters.AddWithValue("$notes", (object?)result.Notes ?? DBNull.Value);
    }

    private static ModelDescriptor ReadModel(SqliteDataReader reader)
    {
        return new ModelDescriptor
        {
            ProviderId = reader.GetString(0),
            RemoteId = reader.GetString(1),
            DisplayName = reader.GetString(2),
            State = Enum.TryParse<ModelLifecycleState>(reader.GetString(3), true, out var state)
                ? state
                : ModelLifecycleState.Discovered,
            Enabled = reader.GetInt32(4) != 0,
            InputModalities = DeserializeList(reader.GetString(5), new[] { "text" }),
            Capabilities = DeserializeList(reader.GetString(6), Array.Empty<string>()),
            ContextWindow = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            LastSeenAt = ReadDate(reader, 8),
            LastVerifiedAt = ReadDate(reader, 9),
            CompatibilityScore = reader.IsDBNull(10) ? null : reader.GetInt32(10)
        };
    }

    private static IReadOnlyList<string> DeserializeList(string json, IReadOnlyList<string> fallback) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? fallback;

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static object ToDb(DateTimeOffset? value) =>
        value.HasValue ? value.Value.ToString("O") : DBNull.Value;
}
