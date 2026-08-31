using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Codex;

public sealed class SessionContinuityService : ISessionContinuityService
{
    private readonly IProjectStateService _projectState;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public SessionContinuityService(IProjectStateService projectState)
    {
        _projectState = projectState;
    }

    public async Task<SessionSwitchPlan> PrepareSwitchAsync(
        string projectPath,
        string sourceProviderId,
        string targetProviderId,
        CancellationToken cancellationToken = default)
    {
        var state = await _projectState.RefreshAsync(
            projectPath,
            SyncLevel.Normal,
            providerId: sourceProviderId,
            cancellationToken: cancellationToken);

        var target = await FindLatestAsync(projectPath, targetProviderId, cancellationToken);

        var staleBy = target is null
            ? state.Revision
            : Math.Max(0, state.Revision - target.LastSeenProjectRevision);

        var level = staleBy switch
        {
            0 => SyncLevel.Light,
            <= 5 => SyncLevel.Normal,
            _ => SyncLevel.Full
        };

        var instruction =
            "Adam CodexHub synchronized this project before provider switching.\n\n" +
            "IMPORTANT:\n" +
            "- Current filesystem, Git state and .adam-codexhub/CURRENT_STATE.md are the source of truth.\n" +
            "- Some assumptions from older chat history may be stale.\n" +
            "- Read .adam-codexhub/CURRENT_STATE.md before continuing.\n" +
            "- Inspect current changed files before editing.\n" +
            "- Do not revert valid work merely because it differs from older conversation context.\n\n" +
            $"Project revision: {state.Revision}\n" +
            $"Previous provider: {sourceProviderId}\n" +
            $"Target provider: {targetProviderId}\n" +
            $"Recommended refresh: {level}\n";

        return new SessionSwitchPlan
        {
            SourceProviderId = sourceProviderId,
            TargetProviderId = targetProviderId,
            ProjectState = state,
            ExistingTargetSession = target,
            RequiresSync = staleBy > 0,
            RecommendedSyncLevel = level,
            ContinuationInstruction = instruction
        };
    }

    public async Task RegisterSessionAsync(
        SessionBinding session,
        CancellationToken cancellationToken = default)
    {
        var list = await ReadSessionsAsync(session.ProjectPath, cancellationToken);
        list.RemoveAll(x => x.Id == session.Id);
        list.Add(session);
        await WriteSessionsAsync(session.ProjectPath, list, cancellationToken);
    }

    public async Task<SessionBinding?> FindLatestAsync(
        string projectPath,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var sessions = await ReadSessionsAsync(projectPath, cancellationToken);
        return sessions
            .Where(x => x.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastUsedAt)
            .FirstOrDefault();
    }

    private static async Task<List<SessionBinding>> ReadSessionsAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var path = GetIndexPath(projectPath);
        if (!File.Exists(path))
        {
            return new List<SessionBinding>();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<SessionBinding>>(
                   stream,
                   Json,
                   cancellationToken)
               ?? new List<SessionBinding>();
    }

    private static async Task WriteSessionsAsync(
        string projectPath,
        List<SessionBinding> sessions,
        CancellationToken cancellationToken)
    {
        var path = GetIndexPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(
            temp,
            JsonSerializer.Serialize(sessions, Json),
            cancellationToken);

        File.Move(temp, path, overwrite: true);
    }

    private static string GetIndexPath(string projectPath) =>
        Path.Combine(
            Path.GetFullPath(projectPath),
            ".adam-codexhub",
            "session-index.json");
}
