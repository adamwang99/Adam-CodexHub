using System.Text;
using System.Text.RegularExpressions;
using AdamCodexHub.Core.Interfaces;
using Tomlyn;
using Tomlyn.Model;

namespace AdamCodexHub.Codex;

public sealed class CodexConfigService : ICodexConfigService
{
    private const string ManagedProviderId = "adam_codexhub";
    private readonly string _configPath;
    private readonly string _accountPath;
    private readonly string _backupDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CodexConfigService()
        : this(DefaultCodexHome(), DefaultRuntimeHomesRoot())
    {
    }

    private CodexConfigService(string codexHome, string runtimeHomesRoot)
    {
        CodexHome = Path.GetFullPath(codexHome);
        RuntimeHomesRoot = Path.GetFullPath(runtimeHomesRoot);
        _configPath = Path.Combine(CodexHome, "config.toml");
        _accountPath = Path.Combine(CodexHome, "config-ACCOUNT.toml");
        _backupDirectory = Path.Combine(CodexHome, "adam-codexhub-backups");

        Directory.CreateDirectory(CodexHome);
        Directory.CreateDirectory(_backupDirectory);
    }

    public string CodexHome { get; }

    /// <summary>
    /// Root folder holding one private, sandboxed Codex home per managed provider
    /// (<c>&lt;RuntimeHomesRoot&gt;/&lt;providerId&gt;/config.toml</c>). Gateway activations are
    /// written here so the user's real <see cref="CodexHome"/> configuration stays untouched.
    /// </summary>
    public string RuntimeHomesRoot { get; }

    public static CodexConfigService ForHome(string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        // Test/embedded homes keep their sandboxed provider homes inside the same folder so the
        // whole tree stays self-contained (e.g. for temp-dir test fixtures).
        return new CodexConfigService(
            codexHome,
            Path.Combine(Path.GetFullPath(codexHome), "codex-homes"));
    }

    private static string DefaultCodexHome() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex");

    private static string DefaultRuntimeHomesRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdamCodexHub",
        "data",
        "codex-homes");

    public Task<bool> HasAccountProfileAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(_accountPath));

    public async Task ActivateAccountAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_accountPath))
            {
                throw new FileNotFoundException(
                    "Codex account profile was not found.",
                    _accountPath);
            }

            var account = await File.ReadAllTextAsync(_accountPath, cancellationToken);
            ValidateToml(account);
            var backup = await BackupCurrentCoreAsync(cancellationToken);

            try
            {
                // Self-heal: a profile captured while a stale gateway overlay was present can
                // itself carry the gateway block (older versions copied the raw current config).
                // Strip it here so restoring "Codex Account" always returns to the native
                // sign-in — otherwise the overlay survives every restore and the route check
                // refuses to open Codex.
                if (TryStripGatewayOverlay(account, out var clean))
                {
                    account = clean;
                    await AtomicWriteFileAsync(_accountPath, clean, cancellationToken);
                }

                await AtomicWriteConfigAsync(account, cancellationToken);
            }
            catch
            {
                await RollBackToAsync(backup, cancellationToken);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ActivateGatewayAsync(
        string modelId,
        string gatewayProviderName,
        int gatewayPort,
        string gatewayToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayToken);
        if (gatewayPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(gatewayPort));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = File.Exists(_configPath)
                ? await File.ReadAllTextAsync(_configPath, cancellationToken)
                : string.Empty;
            var model = string.IsNullOrWhiteSpace(current)
                ? new TomlTable()
                : ParseToml(current);

            var candidate = BuildGatewayCandidate(
                model,
                modelId.Trim(),
                gatewayProviderName,
                gatewayPort,
                gatewayToken.Trim());
            ValidateGatewayCandidate(
                candidate,
                modelId.Trim(),
                gatewayPort,
                gatewayToken.Trim());

            var backup = await BackupCurrentCoreAsync(cancellationToken);
            try
            {
                // Refresh the saved account profile with the CURRENT config before overlaying, so
                // restoring later returns to exactly what the user had (plugins, marketplaces,
                // manual provider blocks…), not a stale first-ever snapshot.
                //
                // A config that still carries the gateway overlay is deliberately NOT captured:
                // its "model" line is one WE wrote (BuildGatewayCandidate overwrote the account's
                // own model), so snapshotting it makes every later "restore Codex Account" hand an
                // upstream model id back to the ChatGPT sign-in — the app then comes up showing
                // the gateway's model list. The existing profile already holds the last native
                // config, which is the right thing to restore.
                if (!string.IsNullOrWhiteSpace(current) && !HasGatewayOverlay(current))
                {
                    await AtomicWriteFileAsync(_accountPath, current, cancellationToken);
                }

                await AtomicWriteConfigAsync(candidate, cancellationToken);
            }
            catch
            {
                await RollBackToAsync(backup, cancellationToken);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> BackupCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await BackupCurrentCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasGatewayOverlayAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_configPath))
            {
                return false;
            }

            var text = await File.ReadAllTextAsync(_configPath, cancellationToken);
            return HasGatewayOverlay(text);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Cheap textual probe for the managed gateway route/provider. Used to tell a native config
    /// (safe to snapshot as the account profile) from a live overlay (must never be snapshotted).
    /// </summary>
    private static bool HasGatewayOverlay(string config) =>
        config.Contains(ManagedProviderId, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> RestoreAccountIfGatewayOverlayAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await HasGatewayOverlayAsync(cancellationToken))
        {
            return false;
        }

        if (!File.Exists(_accountPath))
        {
            // Overlay exists but no account profile was saved — nothing safe to restore to.
            return false;
        }

        await ActivateAccountAsync(cancellationToken);
        return true;
    }

    public async Task<string?> GetCurrentModelAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(_configPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var table = ParseToml(text);
        var model = table.TryGetValue("model", out var value) ? value as string : null;
        return string.IsNullOrWhiteSpace(model) ? null : model.Trim();
    }

    public async Task RestoreLastKnownGoodAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var latest = Directory.EnumerateFiles(_backupDirectory, "config-*.toml")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null)
            {
                throw new InvalidOperationException("No Adam CodexHub config backup exists.");
            }

            var text = await File.ReadAllTextAsync(latest, cancellationToken);
            ValidateToml(text);
            await AtomicWriteConfigAsync(text, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> PrepareGatewayHomeAsync(
        string providerId,
        string modelId,
        int gatewayPort,
        string gatewayToken,
        string? gatewayProviderName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayToken);
        if (gatewayPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(gatewayPort));
        }

        var runtimeHome = GetGatewayHomePath(providerId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(runtimeHome);

            // The real ~/.codex/config.toml is only READ as the base so the user keeps their
            // shared settings; the gateway keys are applied to a private copy below.
            var current = File.Exists(_configPath)
                ? await File.ReadAllTextAsync(_configPath, cancellationToken)
                : string.Empty;
            var model = string.IsNullOrWhiteSpace(current)
                ? new TomlTable()
                : ParseToml(current);

            var candidate = BuildGatewayCandidate(
                model,
                modelId.Trim(),
                gatewayProviderName,
                gatewayPort,
                gatewayToken.Trim());
            ValidateGatewayCandidate(
                candidate,
                modelId.Trim(),
                gatewayPort,
                gatewayToken.Trim());

            var destination = Path.Combine(runtimeHome, "config.toml");
            await AtomicWriteFileAsync(destination, candidate, cancellationToken);

            var written = await File.ReadAllTextAsync(destination, cancellationToken);
            ValidateToml(written);
            return runtimeHome;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string GetGatewayHomePath(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return Path.Combine(RuntimeHomesRoot, ToSafeDirectoryName(providerId));
    }

    private static string ToSafeDirectoryName(string providerId)
    {
        var safe = new string(providerId
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')
            .ToArray());
        return safe.Length == 0 ? "provider" : safe;
    }

    /// <summary>
    /// Removes any <c>adam_codexhub</c> gateway remnant from a Codex configuration snapshot.
    /// Older builds copied the raw (already overlaid) config as the account profile, which made
    /// every later "restore Codex Account" write the gateway block straight back — keeping the
    /// overlay alive forever. Returns false when the input is already clean.
    /// </summary>
    private static bool TryStripGatewayOverlay(string input, out string sanitized)
    {
        var model = ParseToml(input);
        var providers = model.TryGetValue("model_providers", out var value)
            ? value as TomlTable
            : null;
        var hasGatewayEntry = providers?.ContainsKey(ManagedProviderId) == true;
        var isGatewayRoute = string.Equals(
            model.TryGetValue("model_provider", out var route) ? route as string : null,
            ManagedProviderId,
            StringComparison.Ordinal);

        if (!hasGatewayEntry && !isGatewayRoute)
        {
            sanitized = input;
            return false;
        }

        providers?.Remove(ManagedProviderId);
        if (providers is { Count: 0 })
        {
            model.Remove("model_providers");
        }

        if (isGatewayRoute)
        {
            model.Remove("model_provider");
            // The overlay also overwrote "model" with one of the gateway's upstream ids. Leaving
            // that behind is what made a native ChatGPT sign-in come back up showing the gateway's
            // catalogue (its model names do not exist on the account). Dropping it lets Codex fall
            // back to the account's own default again.
            model.Remove("model");
        }

        sanitized = TomlSerializer.Serialize(model);
        return true;
    }

    private static string BuildGatewayCandidate(
        TomlTable model,
        string modelId,
        string? gatewayProviderName,
        int gatewayPort,
        string gatewayToken)
    {
        model["model"] = modelId;
        model["model_provider"] = ManagedProviderId;
        model.TryAdd("model_reasoning_effort", "high");

        if (!model.TryGetValue("model_providers", out var providersValue) ||
            providersValue is not TomlTable providers)
        {
            providers = new TomlTable();
            model["model_providers"] = providers;
        }

        // The provider name in this block is what Codex Desktop/CLI display as the model
        // source, so it must name the actual provider (DeepSeek, TTMAPI…) rather than the
        // generic gateway label.
        providers[ManagedProviderId] = new TomlTable
        {
            ["name"] = string.IsNullOrWhiteSpace(gatewayProviderName)
                ? "Adam CodexHub Local Gateway"
                : gatewayProviderName,
            ["base_url"] = $"http://127.0.0.1:{gatewayPort}/v1",
            ["wire_api"] = "responses",
            // The hub gateway authenticates via its own bearer token below — it is NOT an
            // OpenAI-account provider. Without this flag Codex Desktop treats it as such and
            // pre-flights auth against api.openai.com, which 401s ("Missing bearer...") and
            // also breaks the model list fetch.
            ["requires_openai_auth"] = false,
            ["experimental_bearer_token"] = gatewayToken
        };

        return TomlSerializer.Serialize(model);
    }

    private static void ValidateGatewayCandidate(
        string candidate,
        string modelId,
        int gatewayPort,
        string gatewayToken)
    {
        var model = ParseToml(candidate);
        if (!string.Equals(model["model"] as string, modelId, StringComparison.Ordinal) ||
            !string.Equals(model["model_provider"] as string, ManagedProviderId, StringComparison.Ordinal) ||
            model["model_providers"] is not TomlTable providers ||
            providers[ManagedProviderId] is not TomlTable gateway ||
            !string.Equals(
                gateway["base_url"] as string,
                $"http://127.0.0.1:{gatewayPort}/v1",
                StringComparison.Ordinal) ||
            !string.Equals(
                gateway["experimental_bearer_token"] as string,
                gatewayToken,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Candidate Codex gateway configuration failed validation.");
        }
    }

    private async Task<string?> BackupCurrentCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath))
        {
            return null;
        }

        var backup = Path.Combine(
            _backupDirectory,
            $"config-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.toml");

        await using var input = File.OpenRead(_configPath);
        await using var output = new FileStream(
            backup,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);

        // A backup is written before every activation and none was ever removed: measured 2026-09-11
        // this folder held 231 files totalling 91 MB on Adam's machine. Ten is far more history than a
        // rollback needs (RestoreLatest only reads the newest), and the newest are the ones kept.
        Core.Maintenance.Housekeeping.PruneBackups(_backupDirectory, "config-*.toml");

        return backup;
    }

    private async Task AtomicWriteConfigAsync(
        string contents,
        CancellationToken cancellationToken)
    {
        ValidateToml(contents);
        await AtomicWriteFileAsync(_configPath, contents, cancellationToken);

        var written = await File.ReadAllTextAsync(_configPath, cancellationToken);
        ValidateToml(written);
    }

    private static async Task AtomicWriteFileAsync(
        string destination,
        string contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination directory is unavailable.");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        var rollback = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.rollback");
        var replaced = false;

        try
        {
            await File.WriteAllTextAsync(
                temp,
                contents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            if (File.Exists(destination))
            {
                File.Replace(temp, destination, rollback, ignoreMetadataErrors: true);
                replaced = true;
            }
            else
            {
                File.Move(temp, destination);
            }
        }
        catch
        {
            if (replaced && File.Exists(rollback))
            {
                File.Replace(rollback, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }

            throw;
        }
        finally
        {
            TryDelete(temp);
            TryDelete(rollback);
        }
    }

    private async Task RollBackToAsync(
        string? backup,
        CancellationToken cancellationToken)
    {
        if (backup is not null && File.Exists(backup))
        {
            var previous = await File.ReadAllTextAsync(backup, cancellationToken);
            await AtomicWriteConfigAsync(previous, cancellationToken);
        }
    }

    private static TomlTable ParseToml(string contents)
    {
        try
        {
            // Codex Desktop has emitted a split table header such as "[\\nplugins ]".
            // It is accepted by Codex itself but rejected by strict TOML parsers. Normalize
            // only table-header whitespace before parsing; values and comments are untouched.
            var normalized = Regex.Replace(
                contents,
                @"(?m)^\[\s*\r?\n\s*([A-Za-z0-9_.-]+)\s*\]",
                "[$1]");

            return TomlSerializer.Deserialize<TomlTable>(normalized)
                ?? throw new InvalidDataException("Codex configuration is not valid TOML.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("Codex configuration is not valid TOML.", ex);
        }
    }

    private static void ValidateToml(string contents)
    {
        if (string.IsNullOrWhiteSpace(contents))
        {
            throw new InvalidDataException("Candidate Codex configuration is empty.");
        }

        _ = ParseToml(contents);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
