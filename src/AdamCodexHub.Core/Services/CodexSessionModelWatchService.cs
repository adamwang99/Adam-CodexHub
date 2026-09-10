using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Core.Services;

/// <summary>
/// Keeps "which model is Codex actually using?" up to date by polling Codex's own session state
/// through <see cref="ICodexSessionModelReader"/>. Started by the app; the tray menu, the Home
/// card dropdown and the status chips all read the value it publishes.
///
/// Two rules make the picture match what the user sees:
/// <list type="bullet">
/// <item>A model the hub itself just activated (tray click, card activation) wins until Codex
/// reflects it — otherwise the stale session row would immediately undo the switch on screen.
/// That pending value is dropped as soon as Codex's own state shows the same model, or a newer
/// session with a different one (i.e. the user switched again inside Codex).</item>
/// <item>Anything read from Codex always wins over <c>~/.codex/config.toml</c>, which the in-app
/// picker never rewrites.</item>
/// </list>
/// </summary>
public sealed class CodexSessionModelWatchService : IDisposable
{
    /// <summary>How often Codex's state database is re-read (one indexed read, a few ms).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    private readonly ICodexSessionModelReader _reader;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();

    private Timer? _timer;
    private CodexSessionModel? _current;
    private (string ModelId, DateTimeOffset At)? _pending;
    private bool _disposed;

    public CodexSessionModelWatchService(
        ICodexSessionModelReader reader,
        TimeSpan? interval = null)
    {
        _reader = reader;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>The model Codex is on right now (per rules above); null before the first read.</summary>
    public CodexSessionModel? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised whenever the effective model changes — on a background thread.</summary>
    public event EventHandler<CodexSessionModel?>? Changed;

    public TimeSpan Interval => _interval;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = new Timer(_ => SafePoll(), null, TimeSpan.Zero, _interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// Records a model the hub just pushed to Codex (activation or tray switch) so the UI shows
    /// the user's own action until Codex reports back.
    /// </summary>
    public void NoteHubActivation(string? modelId, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        lock (_gate)
        {
            _pending = (modelId.Trim(), at ?? DateTimeOffset.Now);
        }

        SafePoll();
    }

    /// <summary>
    /// One read + diff cycle. Public so tests can drive the watch without a timer.
    /// </summary>
    public CodexSessionModel? Poll()
    {
        var live = _reader.Read();
        CodexSessionModel? effective;
        var changed = false;

        lock (_gate)
        {
            if (_pending is { } pending)
            {
                var caughtUp = live is not null &&
                    (string.Equals(live.ModelId, pending.ModelId, StringComparison.Ordinal) ||
                     (live.ObservedAt is { } observed && observed > pending.At));
                if (caughtUp)
                {
                    _pending = null;
                }
            }

            effective = _pending is { } still
                ? new CodexSessionModel(
                    still.ModelId,
                    ProviderId: null,
                    ObservedAt: still.At,
                    Source: CodexSessionModel.HubActivationSource)
                : live;

            if (!Same(_current, effective))
            {
                _current = effective;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, effective);
        }

        return effective;
    }

    private void SafePoll()
    {
        try
        {
            Poll();
        }
        catch
        {
            // A watch tick must never take the app down; the next tick retries.
        }
    }

    private static bool Same(CodexSessionModel? left, CodexSessionModel? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal) &&
               string.Equals(left.Source, right.Source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
