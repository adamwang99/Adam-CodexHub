namespace AdamCodexHub.Core.Interfaces;

public interface IGatewayService : IAsyncDisposable
{
    /// <summary>Request-level lines (what Codex asks the gateway for, and what it got). Wired to the
    /// app log so "Codex shows its own model list" can be told apart from "Codex never asked us".</summary>
    event Action<string>? LogMessage;

    /// <summary>
    /// Raised when a turn is continued with a different model than the one Codex asked for (its
    /// unfinished turn keeps the model it started with, so the model the user just picked would
    /// otherwise never run). Arguments: the model Codex asked for, then the model that served it.
    /// </summary>
    event Action<string, string>? TurnContinued;

    bool IsRunning { get; }
    int Port { get; }
    string LocalToken { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
