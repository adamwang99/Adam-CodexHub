using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using AdamCodexHub.App.Mvvm;
using AdamCodexHub.App.Services;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Providers;

namespace AdamCodexHub.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;

    protected PageViewModel(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public string Subtitle { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public virtual Task InitializeAsync() => Task.CompletedTask;

    protected async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

public sealed class HomeViewModel : PageViewModel
{
    private const int ProviderDisclosureVersion = 1;
    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly IKeyPoolService _keys;
    private readonly IGatewayService _gateway;
    private readonly IProviderActivationService _activation;
    private readonly IAppSettingsService _settings;
    private readonly IUserDialogService _dialogs;

    public HomeViewModel(
        IProviderManager providers,
        IModelStore models,
        IKeyPoolService keys,
        IGatewayService gateway,
        IProviderActivationService activation,
        IAppSettingsService settings,
        IUserDialogService dialogs)
        : base("Choose Provider", "Select an AI provider, then activate it to start coding with Codex.")
    {
        _providers = providers;
        _models = models;
        _keys = keys;
        _gateway = gateway;
        _activation = activation;
        _settings = settings;
        _dialogs = dialogs;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(RefreshCoreAsync));
        ActivateCommand = new AsyncRelayCommand(ActivateAsync);
        DoubleClickCommand = new AsyncRelayCommand(p => DoubleClickAsync(p as ProviderCard));
        RestoreAccountCommand = new AsyncRelayCommand(RestoreAccountAsync);
    }

    public ObservableCollection<ProviderCard> Providers { get; } = new();

    public bool HasProviders => Providers.Count > 0;

    private bool _showAllProviders;
    public bool ShowAllProviders
    {
        get => _showAllProviders;
        set
        {
            if (SetProperty(ref _showAllProviders, value))
            {
                if (value)
                {
                    _ = RunAsync(RefreshCoreAsync);
                }
            }
        }
    }

    /// <summary>Launch the Codex Desktop app (instead of CLI) when restoring the Codex Account.</summary>
    private bool _useDesktopForAccount;
    public bool UseDesktopForAccount
    {
        get => _useDesktopForAccount;
        set => SetProperty(ref _useDesktopForAccount, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand DoubleClickCommand { get; }
    public ICommand RestoreAccountCommand { get; }

    public override Task InitializeAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        var active = await _providers.GetActiveAsync();
        var all = await _providers.GetAllAsync();

        var selectedId = SelectedCard?.Id;

        Providers.Clear();

        // Build every card, marking which providers are actually set up (have a usable key
        // or an enabled model, or are Codex Account) so we can hide the unused ones.
        var built = new List<ProviderCard>();
        foreach (var provider in all)
        {
            var enabledModels = (await _models.GetAllAsync(provider.Id))
                .Where(x => x.Enabled && x.State == ModelLifecycleState.Enabled)
                .Count();

            var hasUsableKey = provider.Id == "codex-account" ||
                (await _keys.ListAsync(provider.Id)).Any(key =>
                    key.Enabled &&
                    key.Health is not KeyHealth.Disabled and
                        not KeyHealth.Unauthorized and
                        not KeyHealth.QuotaEmpty and
                        not KeyHealth.Offline);

            built.Add(new ProviderCard
            {
                Id = provider.Id,
                Name = provider.Name,
                BaseUrl = provider.BaseUrl,
                Health = provider.Health,
                Enabled = provider.Enabled,
                EnabledModelCount = enabledModels,
                HasUsableKey = hasUsableKey,
                IsActive = string.Equals(provider.Id, active?.Id, StringComparison.OrdinalIgnoreCase)
            });
        }

        // Show only configured/used providers (always Codex Account). Active provider first,
        // then those already configured (key/model), then the rest. "Show all" bypasses the filter.
        foreach (var card in built
                     .Where(x => ShowAllProviders || x.IsActive || x.IsConfigured || x.Id == ProviderManager.CodexAccountProviderId)
                     .OrderByDescending(x => x.IsActive)
                     .ThenByDescending(x => x.IsConfigured)
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            Providers.Add(card);
        }

        SelectedCard = Providers.FirstOrDefault(x => x.Id == selectedId) ??
                       Providers.FirstOrDefault(x => x.Id == active?.Id) ??
                       Providers.FirstOrDefault();

        OnPropertyChanged(nameof(HasProviders));
        StatusMessage = $"{Providers.Count} provider(s) shown Â· Updated {DateTime.Now:t}.";
    }

    private async Task ActivateAsync()
    {
        try
        {
            if (SelectedCard is not { Enabled: true })
            {
                return;
            }

            if (!SelectedCard.IsValid)
            {
                StatusMessage = SelectedCard.Id == "codex-account"
                    ? "Codex Account is ready to activate."
                    : $"{SelectedCard.Name} is not ready. Add a valid API key and enable a verified model first.";
                return;
            }

            if (SelectedCard.Id != "codex-account")
            {
                var enabledModel = (await _models.GetAllAsync(SelectedCard.Id))
                    .FirstOrDefault(x => x.Enabled && x.State == ModelLifecycleState.Enabled);
                if (enabledModel is null)
                {
                    StatusMessage = $"No enabled model for {SelectedCard.Name}. Open Models, scan and enable one first.";
                    return;
                }

                SelectedModelRemoteId = enabledModel.RemoteId;
            }

            if (UsesRemoteEndpoint(SelectedCard) &&
                !await _settings.HasAcknowledgedProviderDisclosureAsync(
                    SelectedCard.Id,
                    ProviderDisclosureVersion))
            {
                var confirmed = _dialogs.Confirm(
                    "Remote provider data and cost notice",
                    $"Using {SelectedCard.Name} may send prompts, source code, files, outputs and metadata to that provider. Compatibility tests, retries and failover make real API requests and may incur charges. The provider's terms, privacy policy, retention rules and regional restrictions apply.",
                    $"Continue with {SelectedCard.Name}");
                if (!confirmed)
                {
                    StatusMessage = "Provider activation canceled.";
                    return;
                }

                await _settings.AcknowledgeProviderDisclosureAsync(
                    SelectedCard.Id,
                    ProviderDisclosureVersion);
            }

            await _activation.ActivateAsync(
                SelectedCard.Id,
                SelectedCard.Id == "codex-account" ? null : SelectedModelRemoteId);

            await RefreshCoreAsync();
            StatusMessage = SelectedCard is null
                ? "Activation completed."
                : $"Activated {SelectedCard.Name}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Activation failed: {ex.Message}";
            LogError("Activate failed", ex);
        }
    }

    private async Task DoubleClickAsync(ProviderCard? card)
    {
        if (card is null)
        {
            return;
        }

        SelectedCard = card;

        try
        {
            await ActivateAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Activation failed: {ex.Message}";
            LogError("DoubleClick activate failed", ex);
            return;
        }

        // Only launch Codex when activation actually succeeded (no early return/error).
        if (StatusMessage == $"Activated {card.Name}.")
        {
            await LaunchCodexAsync(card.Id == "codex-account" && UseDesktopForAccount);
        }
    }

    private static void LogError(string stage, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AdamCodexHub", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "ui.log"),
                $"{DateTimeOffset.Now:O} | {stage} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private async Task RestoreAccountAsync()
    {
        try
        {
            await _activation.ActivateAsync("codex-account", null);
            await RefreshCoreAsync();
            StatusMessage = "Codex Account restored. Per-provider API keys and models are preserved.";
            await LaunchCodexAsync(UseDesktopForAccount);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not restore Codex Account: {ex.Message}";
            LogError("Restore account failed", ex);
        }
    }

    private static Task LaunchCodexAsync(bool desktop)
    {
        if (desktop)
        {
            // Open the Codex Desktop app registered in the Start Menu (falls back to ChatGPT).
            const string appId = "OpenAI.Codex_2p2nqsd0c76g0!App";
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                    Arguments = $"\"shell:AppsFolder\\{appId}\""
                });
            }
            catch
            {
                // best-effort
            }

            return Task.CompletedTask;
        }

        // Codex CLI
        var binRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");

        if (!Directory.Exists(binRoot))
        {
            return Task.CompletedTask;
        }

        var codexPath = Directory.GetDirectories(binRoot)
            .OrderByDescending(Directory.GetLastWriteTime)
            .Select(d => Path.Combine(d, "codex.exe"))
            .FirstOrDefault(File.Exists);

        if (codexPath is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo(codexPath)
            {
                WorkingDirectory = Path.GetDirectoryName(codexPath),
                UseShellExecute = true
            });
        }
        catch
        {
            // Launching Codex is best-effort; never fail activation on this.
        }

        return Task.CompletedTask;
    }

    private ProviderCard? _selectedCard;
    public ProviderCard? SelectedCard
    {
        get => _selectedCard;
        set => SetProperty(ref _selectedCard, value);
    }

    private string? _selectedModelRemoteId;
    public string? SelectedModelRemoteId
    {
        get => _selectedModelRemoteId;
        set => SetProperty(ref _selectedModelRemoteId, value);
    }

    private static bool UsesRemoteEndpoint(ProviderCard card) =>
        Uri.TryCreate(card.BaseUrl, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
        !uri.IsLoopback;
}

public sealed class ProviderCard : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public ProviderHealth Health { get; init; }
    public bool Enabled { get; init; }
    public int EnabledModelCount { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Whether a usable API key is present for this provider.</summary>
    public bool HasUsableKey { get; init; }

    /// <summary>True when the provider is set up (has a key or an enabled model) beyond Codex Account.</summary>
    public bool IsConfigured => Id == "codex-account" || HasUsableKey || EnabledModelCount > 0;

    /// <summary>True when the provider is ready to be activated.</summary>
    public bool IsValid =>
        Id == "codex-account" || (HasUsableKey && EnabledModelCount > 0);

    public string StatusLabel => IsValid ? "READY" : "SETUP";

    public string KeyLabel => HasUsableKey ? "API key ✓" : "API key needed";

    public string TooltipDescription =>
        Id == "codex-account"
            ? "Uses your native Codex sign-in. Double-click to restore the default Codex account."
            : HasUsableKey && EnabledModelCount > 0
                ? "Ready to use. Double-click to activate this provider with Codex."
                : "Needs a valid API key and an enabled model before it can be activated.";

    public string LogoSource =>
        $"pack://application:,,,/AdamCodexHub.App;component/Assets/providers/{Id}.png";
}

public sealed class ProviderSetupViewModel : PageViewModel
{
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IKeyTestService _tester;
    private readonly IModelDiscoveryService _discovery;
    private readonly ICompatibilityService _compatibility;
    private readonly IModelStore _models;
    private readonly IUserDialogService _dialogs;

    private ProviderProfile? _selectedProvider;
    private ModelDescriptor? _selectedModel;
    private ProviderKeyInfo? _selectedKey;
    private string _providerId = string.Empty;
    private string _providerName = string.Empty;
    private string _baseUrl = string.Empty;
    private string _adapter = "openai-compatible";
    private string _newKey = string.Empty;

    public ProviderSetupViewModel(
        IProviderManager providers,
        IKeyPoolService keys,
        IKeyTestService tester,
        IModelDiscoveryService discovery,
        ICompatibilityService compatibility,
        IModelStore models,
        IUserDialogService dialogs)
        : base("Providers", "Configure a provider: profile, API keys and verified models in one place.")
    {
        _providers = providers;
        _keys = keys;
        _tester = tester;
        _discovery = discovery;
        _compatibility = compatibility;
        _models = models;
        _dialogs = dialogs;

        NewCommand = new RelayCommand(ClearEditor);
        SaveProviderCommand = new AsyncRelayCommand(() => RunAsync(SaveProviderCoreAsync));
        ToggleProviderCommand = new AsyncRelayCommand(() => RunAsync(ToggleProviderCoreAsync));
        DeleteProviderCommand = new AsyncRelayCommand(() => RunAsync(DeleteProviderCoreAsync));
        AddKeyCommand = new AsyncRelayCommand(() => RunAsync(AddKeyCoreAsync));
        TestKeyCommand = new AsyncRelayCommand(() => RunAsync(TestKeyCoreAsync));
        TestAllKeysCommand = new AsyncRelayCommand(() => RunAsync(TestAllKeysCoreAsync));
        ToggleKeyCommand = new AsyncRelayCommand(() => RunAsync(ToggleKeyCoreAsync));
        MoveUpKeyCommand = new AsyncRelayCommand(() => RunAsync(() => MoveKeyCoreAsync(-1)));
        MoveDownKeyCommand = new AsyncRelayCommand(() => RunAsync(() => MoveKeyCoreAsync(1)));
        DeleteKeyCommand = new AsyncRelayCommand(() => RunAsync(DeleteKeyCoreAsync));
        ScanModelsCommand = new AsyncRelayCommand(() => RunAsync(ScanModelsCoreAsync));
        TestAllModelsCommand = new AsyncRelayCommand(() => RunAsync(TestAllModelsCoreAsync));
        TestModelCommand = new AsyncRelayCommand(p => RunAsync(() => TestModelCoreAsync(p as ModelDescriptor)));
        ToggleModelCommand = new AsyncRelayCommand(p => RunAsync(() => ToggleModelCoreAsync(p as ModelDescriptor)));
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync));
    }

    public ObservableCollection<ProviderProfile> Providers { get; } = new();
    public ObservableCollection<ProviderKeyInfo> Keys { get; } = new();
    public ObservableCollection<ModelDescriptor> Models { get; } = new();

    // ---- Provider selection -------------------------------------------------
    public ProviderProfile? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                SyncEditor();
            }
        }
    }

    public string ProviderId { get => _providerId; set => SetProperty(ref _providerId, value); }
    public string ProviderName { get => _providerName; set => SetProperty(ref _providerName, value); }
    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value); }
    public string Adapter { get => _adapter; set => SetProperty(ref _adapter, value); }
    public string NewKey { get => _newKey; set => SetProperty(ref _newKey, value); }

    public ProviderKeyInfo? SelectedKey
    {
        get => _selectedKey;
        set => SetProperty(ref _selectedKey, value);
    }

    public ModelDescriptor? SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    public ICommand NewCommand { get; }
    public ICommand SaveProviderCommand { get; }
    public ICommand ToggleProviderCommand { get; }
    public ICommand DeleteProviderCommand { get; }
    public ICommand AddKeyCommand { get; }
    public ICommand TestKeyCommand { get; }
    public ICommand TestAllKeysCommand { get; }
    public ICommand ToggleKeyCommand { get; }
    public ICommand MoveUpKeyCommand { get; }
    public ICommand MoveDownKeyCommand { get; }
    public ICommand DeleteKeyCommand { get; }
    public ICommand ScanModelsCommand { get; }
    public ICommand TestAllModelsCommand { get; }
    public ICommand TestModelCommand { get; }
    public ICommand ToggleModelCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        var selectedId = SelectedProvider?.Id;
        var all = (await _providers.GetAllAsync())
            .Where(x => x.Id != "codex-account")
            .ToArray();

        // Rank configured providers first (those with usable keys or enabled models),
        // so the user sees what they have set up at the top.
        var ranked = new List<ProviderProfile>();
        foreach (var p in all)
        {
            var modelCount = (await _models.GetAllAsync(p.Id))
                .Count(x => x.Enabled && x.State == ModelLifecycleState.Enabled);
            var hasKey = (await _keys.ListAsync(p.Id)).Any(k =>
                k.Enabled &&
                k.Health is not KeyHealth.Disabled and
                    not KeyHealth.Unauthorized and
                    not KeyHealth.QuotaEmpty and
                    not KeyHealth.Offline);
            ranked.Add(p);
            _providerRank[p.Id] = (hasKey, modelCount);
        }

        Replace(Providers, ranked
            .OrderByDescending(p => _providerRank.TryGetValue(p.Id, out var r) ? (r.HasKey ? 1 : 0) + (r.ModelCount > 0 ? 1 : 0) : 0)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase));

        SelectedProvider = Providers.FirstOrDefault(x => x.Id == selectedId) ?? Providers.FirstOrDefault();
        await Task.WhenAll(LoadKeysAsync(), LoadModelsAsync());
        StatusMessage = $"{Providers.Count} provider(s). Select one to configure.";
    }

    private readonly Dictionary<string, (bool HasKey, int ModelCount)> _providerRank = new(StringComparer.OrdinalIgnoreCase);

    private void SyncEditor()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        ProviderId = SelectedProvider.Id;
        ProviderName = SelectedProvider.Name;
        BaseUrl = SelectedProvider.BaseUrl;
        Adapter = SelectedProvider.Adapter;
        _ = RunAsync(LoadKeysAsync);
        _ = RunAsync(LoadModelsAsync);
    }

    // ---- Profile -------------------------------------------------------------
    private async Task SaveProviderCoreAsync()
    {
        var existing = await _providers.GetAsync(ProviderId);
        await _providers.SaveAsync(new ProviderProfile
        {
            Id = ProviderId,
            Name = ProviderName,
            Adapter = Adapter,
            BaseUrl = BaseUrl,
            TrustLevel = existing?.TrustLevel ?? ProviderTrustLevel.Custom,
            Enabled = existing?.Enabled ?? true,
            Health = existing?.Health ?? ProviderHealth.Unknown,
            AuthType = existing?.AuthType ?? "bearer",
            AuthHeaderName = existing?.AuthHeaderName ?? "Authorization",
            ModelsEndpoint = existing?.ModelsEndpoint ?? "/models",
            ResponsesEndpoint = existing?.ResponsesEndpoint ?? "/responses",
            ChatCompletionsEndpoint = existing?.ChatCompletionsEndpoint ?? "/chat/completions",
            ExtraHeaders = existing?.ExtraHeaders ?? new Dictionary<string, string>(),
            DeclaredCapabilities = existing?.DeclaredCapabilities ?? Array.Empty<string>()
        });

        await LoadAsync();
        SelectedProvider = Providers.First(x => x.Id == ProviderId.Trim().ToLowerInvariant());
        StatusMessage = $"Provider '{SelectedProvider.Name}' saved.";
    }

    private async Task ToggleProviderCoreAsync()
    {
        var selected = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        await _providers.SetEnabledAsync(selected.Id, !selected.Enabled);
        await LoadAsync();
        StatusMessage = selected.Enabled
            ? $"Provider '{selected.Name}' disabled."
            : $"Provider '{selected.Name}' enabled.";
    }

    private async Task DeleteProviderCoreAsync()
    {
        var selected = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        if (!_dialogs.Confirm(
                "Delete provider",
                $"Delete custom provider '{selected.Name}'? API keys should be removed separately.",
                $"Delete {selected.Name}"))
        {
            return;
        }

        await _providers.DeleteAsync(selected.Id);
        await LoadAsync();
        StatusMessage = $"Provider '{selected.Name}' deleted.";
    }

    private void ClearEditor()
    {
        SelectedProvider = null;
        ProviderId = string.Empty;
        ProviderName = string.Empty;
        BaseUrl = string.Empty;
        Adapter = "openai-compatible";
        StatusMessage = "Enter a custom provider profile.";
    }

    // ---- API keys ------------------------------------------------------------
    private async Task LoadKeysAsync()
    {
        if (SelectedProvider is null)
        {
            Keys.Clear();
            return;
        }

        var selectedId = SelectedKey?.Id;
        Replace(Keys, await _keys.ListAsync(SelectedProvider.Id));
        SelectedKey = Keys.FirstOrDefault(x => x.Id == selectedId) ?? Keys.FirstOrDefault();
    }

    private async Task AddKeyCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        if (string.IsNullOrWhiteSpace(NewKey))
        {
            StatusMessage = "Paste an API key to add.";
            return;
        }

        await _keys.AddAsync(provider.Id, "default", NewKey.Trim());
        NewKey = string.Empty;
        await LoadKeysAsync();
        StatusMessage = $"API key encrypted and stored for '{provider.Name}'.";
    }

    private async Task TestKeyCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var key = SelectedKey
            ?? throw new InvalidOperationException("Select an API key first.");
        var result = await _tester.TestAsync(provider.Id, key.Id);
        await LoadKeysAsync();
        StatusMessage = result.Summary;
    }

    private async Task TestAllKeysCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var keys = Keys.ToArray();
        if (keys.Length == 0)
        {
            StatusMessage = $"No API keys for {provider.Name}. Add one first.";
            return;
        }

        int ok = 0, fail = 0;
        var notes = new List<string>();
        foreach (var key in keys)
        {
            var result = await _tester.TestAsync(provider.Id, key.Id);
            if (result.Success)
            {
                ok++;
            }
            else
            {
                fail++;
                notes.Add($"{key.Label}: {result.Summary}");
            }
        }

        await LoadKeysAsync();
        StatusMessage = $"{provider.Name}: {ok} valid, {fail} failed of {keys.Length} key(s).";
        if (notes.Count > 0 && fail > 0)
        {
            StatusMessage += $" Next: {notes[0]}";
        }
    }

    private async Task ToggleKeyCoreAsync()
    {
        var key = SelectedKey
            ?? throw new InvalidOperationException("Select an API key first.");
        await _keys.SetEnabledAsync(key.Id, !key.Enabled);
        await LoadKeysAsync();
    }

    private async Task MoveKeyCoreAsync(int offset)
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var key = SelectedKey
            ?? throw new InvalidOperationException("Select an API key first.");
        var ordered = Keys.Select(x => x.Id).ToList();
        var current = ordered.IndexOf(key.Id);
        var target = current + offset;
        if (current < 0 || target < 0 || target >= ordered.Count)
        {
            return;
        }

        (ordered[current], ordered[target]) = (ordered[target], ordered[current]);
        await _keys.ReorderAsync(provider.Id, ordered);
        await LoadKeysAsync();
        SelectedKey = Keys.First(x => x.Id == key.Id);
    }

    private async Task DeleteKeyCoreAsync()
    {
        var key = SelectedKey
            ?? throw new InvalidOperationException("Select an API key first.");
        if (!_dialogs.Confirm(
                "Delete API key",
                $"Delete '{key.Label}' and its protected secret?",
                $"Delete {key.Label}"))
        {
            return;
        }

        await _keys.DeleteAsync(key.Id);
        await LoadKeysAsync();
        StatusMessage = $"API key '{key.Label}' deleted.";
    }

    // ---- Models --------------------------------------------------------------
    private async Task LoadModelsAsync()
    {
        if (SelectedProvider is null)
        {
            Models.Clear();
            return;
        }

        var selectedId = SelectedModel?.RemoteId;
        Replace(Models, await _models.GetAllAsync(SelectedProvider.Id));
        SelectedModel = Models.FirstOrDefault(x => x.RemoteId == selectedId) ?? Models.FirstOrDefault();
    }

    private async Task ScanModelsCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        try
        {
            await _discovery.ScanAsync(provider.Id);
            await LoadModelsAsync();
            StatusMessage = $"Model catalog refreshed for {provider.Name}. New models remain disabled.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase)
                ? "Unauthorized (401). The stored API key may be invalid or expired. Use 'Test selected' / 'Test all keys' to verify, then update the key."
                : $"Scan failed for {provider.Name}: {ex.Message}";
        }
    }

    private async Task TestAllModelsCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var models = Models.ToArray();
        if (models.Length == 0)
        {
            StatusMessage = $"No models for {provider.Name}. Scan models first.";
            return;
        }

        int verified = 0;
        var notes = new List<string>();
        foreach (var model in models)
        {
            try
            {
                var result = await _compatibility.TestAsync(provider.Id, model.RemoteId);
                verified++;
                if (!result.Text && !result.Streaming && !result.ToolCalling)
                {
                    notes.Add($"{model.DisplayName}: score {result.Score}");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"{model.DisplayName}: {ex.Message}");
            }
        }

        await LoadModelsAsync();
        StatusMessage = $"{provider.Name}: tested {verified}/{models.Length} model(s).";
        if (notes.Count > 0)
        {
            StatusMessage += $" First issue: {notes[0]}";
        }
    }

    private async Task TestModelCoreAsync(ModelDescriptor? model)
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        model ??= SelectedModel
            ?? throw new InvalidOperationException("Select a model first.");
        var result = await _compatibility.TestAsync(provider.Id, model.RemoteId);
        await LoadModelsAsync();
        SelectedModel = Models.First(x => x.RemoteId == model.RemoteId);
        StatusMessage = $"Compatibility {result.Score}/100 · Text {YesNo(result.Text)} · Streaming {YesNo(result.Streaming)} · Tools {YesNo(result.ToolCalling)}";
    }

    private async Task ToggleModelCoreAsync(ModelDescriptor? model)
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        model ??= SelectedModel
            ?? throw new InvalidOperationException("Select a model first.");
        await _models.SetEnabledAsync(provider.Id, model.RemoteId, !model.Enabled);
        await LoadModelsAsync();
        SelectedModel = Models.First(x => x.RemoteId == model.RemoteId);
        StatusMessage = model.Enabled
            ? $"Model '{model.DisplayName}' disabled."
            : $"Model '{model.DisplayName}' enabled for Codex.";
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";
}


public sealed class SessionsViewModel : PageViewModel
{
    private readonly IProviderManager _providers;
    private readonly IProjectStateService _projectState;
    private readonly ISessionContinuityService _sessions;
    private ProviderProfile? _selectedTargetProvider;
    private string _projectPath = Environment.CurrentDirectory;
    private string _revisionSummary = "Not synchronized";
    private string _continuationInstruction = string.Empty;

    public SessionsViewModel(
        IProviderManager providers,
        IProjectStateService projectState,
        ISessionContinuityService sessions)
        : base("Sessions", "Prepare provider-safe project state and continuation handoffs.")
    {
        _providers = providers;
        _projectState = projectState;
        _sessions = sessions;
        RefreshProjectCommand = new AsyncRelayCommand(() => RunAsync(RefreshProjectCoreAsync));
        PrepareSwitchCommand = new AsyncRelayCommand(() => RunAsync(PrepareSwitchCoreAsync));
    }

    public ObservableCollection<ProviderProfile> Providers { get; } = new();

    public ProviderProfile? SelectedTargetProvider
    {
        get => _selectedTargetProvider;
        set => SetProperty(ref _selectedTargetProvider, value);
    }

    public string ProjectPath
    {
        get => _projectPath;
        set => SetProperty(ref _projectPath, value);
    }

    public string RevisionSummary
    {
        get => _revisionSummary;
        private set => SetProperty(ref _revisionSummary, value);
    }

    public string ContinuationInstruction
    {
        get => _continuationInstruction;
        set => SetProperty(ref _continuationInstruction, value);
    }

    public ICommand RefreshProjectCommand { get; }
    public ICommand PrepareSwitchCommand { get; }

    public override Task InitializeAsync() => RunAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        Replace(Providers, await _providers.GetAllAsync());
        var active = await _providers.GetActiveAsync();
        SelectedTargetProvider = Providers.FirstOrDefault(x => x.Id != active?.Id) ?? Providers.FirstOrDefault();

        if (Directory.Exists(ProjectPath))
        {
            var state = await _projectState.ReadAsync(ProjectPath);
            RevisionSummary = state is null
                ? "Project has not been synchronized yet."
                : $"Revision {state.Revision} Â· {state.ChangedFiles.Count} changed file(s) Â· {state.UpdatedAt.LocalDateTime:g}";
        }
    }

    private async Task RefreshProjectCoreAsync()
    {
        EnsureProjectPath();
        var active = await _providers.GetActiveAsync();
        var state = await _projectState.RefreshAsync(
            ProjectPath,
            SyncLevel.Normal,
            active?.Id);
        RevisionSummary = $"Revision {state.Revision} Â· {state.ChangedFiles.Count} changed file(s) Â· current";
        StatusMessage = "CURRENT_STATE.md and project-state.json refreshed.";
    }

    private async Task PrepareSwitchCoreAsync()
    {
        EnsureProjectPath();
        var source = await _providers.GetActiveAsync()
            ?? throw new InvalidOperationException("No active provider exists.");
        var target = SelectedTargetProvider
            ?? throw new InvalidOperationException("Select a target provider.");
        if (source.Id == target.Id)
        {
            throw new InvalidOperationException("Choose a different target provider.");
        }

        var plan = await _sessions.PrepareSwitchAsync(ProjectPath, source.Id, target.Id);
        ContinuationInstruction = plan.ContinuationInstruction;
        RevisionSummary = $"Revision {plan.ProjectState.Revision} Â· {plan.RecommendedSyncLevel} sync Â· {(plan.RequiresNewSession ? "new session" : "resume session")}";
        StatusMessage = "Provider-safe handoff prepared. Current filesystem remains the source of truth.";
    }

    private void EnsureProjectPath()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath))
        {
            throw new DirectoryNotFoundException("Enter an existing project directory.");
        }
    }
}

public sealed class DiagnosticsViewModel : PageViewModel
{
    private readonly IGatewayService _gateway;
    private readonly ICodexConfigService _config;
    private readonly IProviderManager _providers;
    private string _gatewayStatus = "Stopped";
    private string _codexStatus = "Not checked";
    private string _providerWarnings = string.Empty;

    public DiagnosticsViewModel(
        IGatewayService gateway,
        ICodexConfigService config,
        IProviderManager providers)
        : base("Diagnostics", "Inspect gateway, provider load and Codex configuration recovery state.")
    {
        _gateway = gateway;
        _config = config;
        _providers = providers;
        ToggleGatewayCommand = new AsyncRelayCommand(() => RunAsync(ToggleGatewayCoreAsync));
        RestoreConfigCommand = new AsyncRelayCommand(() => RunAsync(RestoreConfigCoreAsync));
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(RefreshCoreAsync));
    }

    public string GatewayStatus
    {
        get => _gatewayStatus;
        private set => SetProperty(ref _gatewayStatus, value);
    }

    public string CodexStatus
    {
        get => _codexStatus;
        private set => SetProperty(ref _codexStatus, value);
    }

    public string ProviderWarnings
    {
        get => _providerWarnings;
        private set => SetProperty(ref _providerWarnings, value);
    }

    public ICommand ToggleGatewayCommand { get; }
    public ICommand RestoreConfigCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        GatewayStatus = _gateway.IsRunning
            ? $"Listening on 127.0.0.1:{_gateway.Port}"
            : "Stopped";
        await RefreshProviderWarningsAsync();
        await RefreshCodexStatusAsync();
    }

    private async Task RefreshProviderWarningsAsync()
    {
        try
        {
            await _providers.InitializeAsync();
            var warnings = _providers.StartupWarnings;
            ProviderWarnings = warnings.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, warnings);
        }
        catch (Exception ex)
        {
            ProviderWarnings = $"Provider load check failed: {ex.Message}";
        }
    }

    private async Task RefreshCodexStatusAsync()
    {
        CodexStatus = await _config.HasAccountProfileAsync()
            ? $"Account profile protected Â· {_config.CodexHome}"
            : $"Account profile not captured Â· {_config.CodexHome}";
    }

    private async Task ToggleGatewayCoreAsync()
    {
        if (_gateway.IsRunning)
        {
            await _gateway.StopAsync();
        }
        else
        {
            await _gateway.StartAsync();
        }

        await RefreshCoreAsync();
        StatusMessage = _gateway.IsRunning ? "Gateway started." : "Gateway stopped.";
    }

    private async Task RestoreConfigCoreAsync()
    {
        await _config.RestoreLastKnownGoodAsync();
        await RefreshCoreAsync();
        StatusMessage = "Last known good Codex configuration restored.";
    }
}

public sealed class SettingsViewModel : PageViewModel
{
    public SettingsViewModel()
        : base("Settings", "Security and session defaults applied by Adam CodexHub.")
    {
        StatusMessage = "Loopback gateway, automatic key failover, stale-session sync and prompt logging off are enforced defaults.";
    }
}
