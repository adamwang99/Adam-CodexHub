using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

/// <summary>
/// Reads the model the newest Codex session runs with, straight out of Codex's own state
/// (<c>~/.codex/state_5.sqlite</c>). This is the only way the hub can follow a model the user
/// picked inside Codex — that picker never rewrites <c>~/.codex/config.toml</c>.
/// </summary>
public interface ICodexSessionModelReader
{
    /// <summary>
    /// Newest non-archived Codex session and its model, or <c>null</c> when Codex has no session
    /// yet / its state file cannot be read. Must never throw.
    /// </summary>
    CodexSessionModel? Read();
}
