using System.ComponentModel;
using System.Globalization;
using System.IO;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;

namespace AdamCodexHub.App.Services;

/// <summary>What we know about one model right now, for the badge and its tooltip.</summary>
public sealed record ModelStatusSnapshot(
    Callability Callability,
    DateTimeOffset? VerifiedAt,
    int? FirstByteMs,
    int? TotalMs,
    int? Score);

/// <summary>
/// ONE shared source of truth for "is this model callable / slow / skipped?" — read by every
/// model picker (the tray "Mô hình" submenu, the Home + per-card dropdowns and the provider
/// page's model list). It is a static singleton with a change notification, exactly like
/// <see cref="HandoffState"/>: the background auto-ping writes here, the UI binds here, so no
/// two views can disagree about a model's health.
///
/// <see cref="Revision"/> is what the XAML converters watch — WPF cannot observe a per-item
/// dictionary lookup, so each badge binding includes this counter and re-evaluates when it moves.
/// </summary>
public sealed class ModelStatusState : INotifyPropertyChanged
{
    /// <summary>App-wide instance every view binds to.</summary>
    public static ModelStatusState Current { get; } = new();

    private readonly Dictionary<string, ModelStatusSnapshot> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CodexReadiness> _readiness = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly object _readinessGate = new();
    private int _revision;
    private bool _showAllModels;
    private DateTimeOffset? _lastRefreshedAt;
    private string? _lastRefreshedProviderName;
    private CodexSessionModel? _codexSession;

    private ModelStatusState()
    {
        _showAllModels = UiSettingsStore.LoadShowAllModels(AppDataRoot());
    }

    /// <summary>Bumped on every status update; the badge converters bind to it so a badge
    /// re-renders when the auto-ping refreshes its model.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>When the last background refresh finished, and for which provider.</summary>
    public DateTimeOffset? LastRefreshedAt
    {
        get => _lastRefreshedAt;
        private set
        {
            _lastRefreshedAt = value;
            Raise(nameof(LastRefreshedAt));
            Raise(nameof(LastRefreshedText));
        }
    }

    public string? LastRefreshedProviderName
    {
        get => _lastRefreshedProviderName;
        private set
        {
            _lastRefreshedProviderName = value;
            Raise(nameof(LastRefreshedProviderName));
            Raise(nameof(LastRefreshedText));
        }
    }

    /// <summary>Localized "last auto-check …" line for the tray submenu.</summary>
    public string LastRefreshedText => _lastRefreshedAt is null
        ? L10n.T("L10n_Callability_NeverChecked")
        : L10n.F(
            "L10n_Callability_LastChecked",
            _lastRefreshedAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
            _lastRefreshedProviderName ?? string.Empty);

    /// <summary>
    /// The model Codex itself is set to right now (read back from Codex's session state — the
    /// in-app "Select model" picker never rewrites ~/.codex/config.toml, so this is the only way
    /// the hub can follow a switch made inside Codex). Null until the first read.
    /// </summary>
    public CodexSessionModel? CodexSession
    {
        get => _codexSession;
        private set
        {
            var same = _codexSession is null || value is null
                ? _codexSession is null && value is null
                : string.Equals(_codexSession.ModelId, value.ModelId, StringComparison.Ordinal) &&
                  string.Equals(_codexSession.Source, value.Source, StringComparison.Ordinal);
            if (same)
            {
                return;
            }

            _codexSession = value;
            Raise(nameof(CodexSession));
            Raise(nameof(CodexSessionText));
            Raise(nameof(HasCodexSession));
        }
    }

    /// <summary>True when Codex's current model is known.</summary>
    public bool HasCodexSession => _codexSession is not null;

    /// <summary>Localized "Codex is using: &lt;model&gt;" line for the tray submenu header.</summary>
    public string CodexSessionText => _codexSession is null
        ? L10n.T("L10n_Codex_NoSessionModel")
        : L10n.F("L10n_Codex_SessionModel", _codexSession.ModelId);

    /// <summary>Publishes the model Codex is on (called by the session-model watcher).</summary>
    public void ApplyCodexSession(CodexSessionModel? session) => CodexSession = session;

    /// <summary>
    /// "Show all" for the model pickers: when false (default) the tray submenu hides models
    /// classified <see cref="Callability.Skip"/>. Persisted so the choice survives a restart.
    /// </summary>
    public bool ShowAllModels
    {
        get => _showAllModels;
        set
        {
            if (_showAllModels == value)
            {
                return;
            }

            _showAllModels = value;
            Raise(nameof(ShowAllModels));
            UiSettingsStore.SaveShowAllModels(AppDataRoot(), value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Applies one refreshed compatibility result (auto-ping tick or manual test).</summary>
    public void Apply(CompatibilityResult result) =>
        Apply(result, DateTimeOffset.UtcNow, raise: true);

    /// <summary>Applies a whole auto-ping tick: stamps the refresh time and stores every result.</summary>
    public void Apply(ModelAutoPingTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        foreach (var result in tick.Results)
        {
            Apply(result, tick.CompletedAt, raise: false);
        }

        LastRefreshedProviderName = tick.ProviderName;
        LastRefreshedAt = tick.CompletedAt;
        Raise(nameof(Revision));
    }

    /// <summary>Reads every model's latest stored result and seeds the state from it.</summary>
    public async Task PopulateFromStoreAsync(
        IModelStore store,
        string providerId,
        IEnumerable<ModelDescriptor> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (models is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var model in models)
        {
            try
            {
                var latest = await store
                    .GetLatestCompatibilityAsync(providerId, model.RemoteId, cancellationToken)
                    .ConfigureAwait(true);
                if (latest is null)
                {
                    continue;
                }

                Apply(latest, now, raise: false);
                changed = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A single unreadable row must not break the model list.
            }
        }

        if (changed)
        {
            Raise(nameof(Revision));
        }
    }

    public Callability GetCallability(string? providerId, string? modelId)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
        {
            return Callability.Unknown;
        }

        lock (_sync)
        {
            return _status.TryGetValue(Key(providerId, modelId), out var snapshot)
                ? snapshot.Callability
                : Callability.Unknown;
        }
    }

    public ModelStatusSnapshot? GetSnapshot(string? providerId, string? modelId)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        lock (_sync)
        {
            return _status.TryGetValue(Key(providerId, modelId), out var snapshot) ? snapshot : null;
        }
    }

    /// <summary>Localized one-liner for a badge tooltip:
    /// "Kiểm tra lúc 14:52 · byte đầu 37,7s", plus the Codex-readiness note when the model has a
    /// verdict from the Codex-shaped probe.</summary>
    public string DescribeTooltip(string? providerId, string? modelId)
    {
        var text = DescribeCallabilityTooltip(providerId, modelId);
        var note = DescribeCodexReadiness(providerId, modelId);
        return string.IsNullOrEmpty(note) ? text : $"{text} · {note}";
    }

    /// <summary>Stores the latest Codex-readiness tick so tooltips and the tray can show why a
    /// model is not offered to Codex.</summary>
    public void ApplyReadiness(CodexReadinessTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        lock (_readinessGate)
        {
            foreach (var verdict in tick.Verdicts)
            {
                _readiness[ReadinessKey(verdict.ProviderId, verdict.ModelId)] = verdict;
            }
        }
    }

    /// <summary>Latest Codex-readiness verdict for a model, or null when it was never probed.</summary>
    public CodexReadiness? GetCodexReadiness(string? providerId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        lock (_readinessGate)
        {
            return _readiness.TryGetValue(ReadinessKey(providerId, modelId), out var verdict)
                ? verdict
                : null;
        }
    }

    /// <summary>True when the model answered the last Codex-shaped probe (fresh verdict, tool call).</summary>
    public bool IsCodexReady(string? providerId, string? modelId)
    {
        var verdict = GetCodexReadiness(providerId, modelId);
        return verdict is null || (verdict.Ready && verdict.IsFresh(DateTimeOffset.UtcNow));
    }

    /// <summary>Why Codex will not offer this model — empty when it works.</summary>
    private string DescribeCodexReadiness(string? providerId, string? modelId)
    {
        var verdict = GetCodexReadiness(providerId, modelId);
        if (verdict is null || (verdict.Ready && verdict.IsFresh(DateTimeOffset.UtcNow)))
        {
            return string.Empty;
        }

        var detail = string.IsNullOrWhiteSpace(verdict.Detail) ? string.Empty : $" ({verdict.Detail})";
        return L10n.T("L10n_Callability_CodexNotReady") + detail;
    }

    private static string ReadinessKey(string providerId, string modelId) => $"{providerId}\u001f{modelId}";

    private string DescribeCallabilityTooltip(string? providerId, string? modelId)
    {
        var callability = GetCallability(providerId, modelId);
        var label = L10n.T(LabelKey(callability));
        var snapshot = GetSnapshot(providerId, modelId);
        if (snapshot?.VerifiedAt is null)
        {
            return L10n.F("L10n_Callability_TooltipNoResult", label);
        }

        var time = snapshot.VerifiedAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        if (snapshot.FirstByteMs is null && snapshot.TotalMs is null)
        {
            return L10n.F("L10n_Callability_TooltipNoLatency", label, time);
        }

        return L10n.F(
            "L10n_Callability_Tooltip",
            label,
            time,
            FormatSeconds(snapshot.FirstByteMs),
            FormatSeconds(snapshot.TotalMs));
    }

    public static string LabelKey(Callability callability) => callability switch
    {
        Callability.Callable => "L10n_Callability_Callable",
        Callability.Slow => "L10n_Callability_Slow",
        Callability.Skip => "L10n_Callability_Skip",
        _ => "L10n_Callability_Unknown"
    };

    private static string FormatSeconds(int? milliseconds) =>
        milliseconds is null
            ? "-"
            : (milliseconds.Value / 1000.0).ToString("0.0", CultureInfo.CurrentCulture);

    private void Apply(CompatibilityResult result, DateTimeOffset now, bool raise)
    {
        var snapshot = new ModelStatusSnapshot(
            ModelCallability.Classify(result, now),
            result.VerifiedAt,
            result.FirstByteMs,
            result.TotalMs,
            result.Score);

        lock (_sync)
        {
            _status[Key(result.ProviderId, result.ModelId)] = snapshot;
        }

        if (raise)
        {
            Raise(nameof(Revision));
        }
    }

    private static string Key(string providerId, string modelId) => $"{providerId}\u001F{modelId}";

    private static string AppDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdamCodexHub");

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
