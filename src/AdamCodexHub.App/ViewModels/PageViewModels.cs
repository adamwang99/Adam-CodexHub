using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AdamCodexHub.App.Mvvm;
using AdamCodexHub.App.Services;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

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
    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly IKeyPoolService _keys;
    private readonly IGatewayService _gateway;
    private string _providerSummary = "Not initialized";
    private string _modelSummary = "No model selected";
    private string _keySummary = "No API key required";
    private string _gatewaySummary = "Stopped";

    public HomeViewModel(
        IProviderManager providers,
        IModelStore models,
        IKeyPoolService keys,
        IGatewayService gateway)
        : base("Overview", "Active provider, model readiness and local gateway health.")
    {
        _providers = providers;
        _models = models;
        _keys = keys;
        _gateway = gateway;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(RefreshCoreAsync));
    }

    public string ProviderSummary
    {
        get => _providerSummary;
        private set => SetProperty(ref _providerSummary, value);
    }

    public string ModelSummary
    {
        get => _modelSummary;
        private set => SetProperty(ref _modelSummary, value);
    }

    public string KeySummary
    {
        get => _keySummary;
        private set => SetProperty(ref _keySummary, value);
    }

    public string GatewaySummary
    {
        get => _gatewaySummary;
        private set => SetProperty(ref _gatewaySummary, value);
    }

    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        var provider = await _providers.GetActiveAsync();
        ProviderSummary = provider is null
            ? "No active provider"
            : $"{provider.Name} · {provider.Health}";

        if (provider is null || provider.Id == "codex-account")
        {
            ModelSummary = "Managed by Codex Account";
            KeySummary = "Native account authentication";
        }
        else
        {
            var enabled = (await _models.GetAllAsync(provider.Id))
                .Where(x => x.Enabled)
                .ToArray();
            ModelSummary = enabled.Length == 0
                ? "No enabled model"
                : $"{enabled.Length} enabled · {enabled[0].DisplayName}";

            var keys = await _keys.ListAsync(provider.Id);
            var activeKey = keys.FirstOrDefault(x =>
                x.Enabled && x.Health is not KeyHealth.Disabled and not KeyHealth.Unauthorized and not KeyHealth.QuotaEmpty);
            KeySummary = activeKey is null
                ? "No usable key"
                : $"{activeKey.Label} · {activeKey.MaskedDisplay} · {activeKey.Health}";
        }

        GatewaySummary = _gateway.IsRunning
            ? $"Healthy · 127.0.0.1:{_gateway.Port}"
            : "Stopped";
        StatusMessage = $"Updated {DateTime.Now:t}.";
    }
}

public sealed class ProvidersViewModel : PageViewModel
{
    private readonly IProviderManager _providers;
    private readonly IUserDialogService _dialogs;
    private ProviderProfile? _selectedProvider;
    private string _providerId = string.Empty;
    private string _providerName = string.Empty;
    private string _baseUrl = string.Empty;
    private string _adapter = "openai-compatible";

    public ProvidersViewModel(IProviderManager providers, IUserDialogService dialogs)
        : base("Providers", "Manage built-in presets and custom compatible providers.")
    {
        _providers = providers;
        _dialogs = dialogs;
        NewCommand = new RelayCommand(ClearEditor);
        SaveCommand = new AsyncRelayCommand(() => RunAsync(SaveCoreAsync));
        ToggleEnabledCommand = new AsyncRelayCommand(() => RunAsync(ToggleEnabledCoreAsync));
        DeleteCommand = new AsyncRelayCommand(() => RunAsync(DeleteCoreAsync));
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync));
    }

    public ObservableCollection<ProviderProfile> Items { get; } = new();

    public ProviderProfile? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!SetProperty(ref _selectedProvider, value) || value is null)
            {
                return;
            }

            ProviderId = value.Id;
            ProviderName = value.Name;
            BaseUrl = value.BaseUrl;
            Adapter = value.Adapter;
        }
    }

    public string ProviderId
    {
        get => _providerId;
        set => SetProperty(ref _providerId, value);
    }

    public string ProviderName
    {
        get => _providerName;
        set => SetProperty(ref _providerName, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string Adapter
    {
        get => _adapter;
        set => SetProperty(ref _adapter, value);
    }

    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        var selectedId = SelectedProvider?.Id;
        Replace(Items, await _providers.GetAllAsync());
        SelectedProvider = Items.FirstOrDefault(x => x.Id == selectedId) ?? Items.FirstOrDefault();
        StatusMessage = $"{Items.Count} provider profiles loaded.";
    }

    private async Task SaveCoreAsync()
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
        SelectedProvider = Items.First(x => x.Id == ProviderId.Trim().ToLowerInvariant());
        StatusMessage = $"Provider '{SelectedProvider.Name}' saved.";
    }

    private async Task ToggleEnabledCoreAsync()
    {
        var selected = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        await _providers.SetEnabledAsync(selected.Id, !selected.Enabled);
        await LoadAsync();
        StatusMessage = selected.Enabled
            ? $"Provider '{selected.Name}' disabled."
            : $"Provider '{selected.Name}' enabled.";
    }

    private async Task DeleteCoreAsync()
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
}

public sealed class ApiKeysViewModel : PageViewModel
{
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IKeyTestService _tester;
    private readonly IUserDialogService _dialogs;
    private ProviderProfile? _selectedProvider;
    private ProviderKeyInfo? _selectedKey;
    private string _newLabel = string.Empty;
    private string _newSecret = string.Empty;
    private int _newPriority = 100;

    public ApiKeysViewModel(
        IProviderManager providers,
        IKeyPoolService keys,
        IKeyTestService tester,
        IUserDialogService dialogs)
        : base("API Keys", "Secure key pools, priority, health and bounded failover.")
    {
        _providers = providers;
        _keys = keys;
        _tester = tester;
        _dialogs = dialogs;
        AddCommand = new AsyncRelayCommand(() => RunAsync(AddCoreAsync));
        TestCommand = new AsyncRelayCommand(() => RunAsync(TestCoreAsync));
        ToggleEnabledCommand = new AsyncRelayCommand(() => RunAsync(ToggleEnabledCoreAsync));
        MoveUpCommand = new AsyncRelayCommand(() => RunAsync(() => MoveCoreAsync(-1)));
        MoveDownCommand = new AsyncRelayCommand(() => RunAsync(() => MoveCoreAsync(1)));
        DeleteCommand = new AsyncRelayCommand(() => RunAsync(DeleteCoreAsync));
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadKeysAsync));
    }

    public ObservableCollection<ProviderProfile> Providers { get; } = new();
    public ObservableCollection<ProviderKeyInfo> Keys { get; } = new();

    public ProviderProfile? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                _ = RunAsync(LoadKeysAsync);
            }
        }
    }

    public ProviderKeyInfo? SelectedKey
    {
        get => _selectedKey;
        set => SetProperty(ref _selectedKey, value);
    }

    public string NewLabel
    {
        get => _newLabel;
        set => SetProperty(ref _newLabel, value);
    }

    public string NewSecret
    {
        get => _newSecret;
        set => SetProperty(ref _newSecret, value);
    }

    public int NewPriority
    {
        get => _newPriority;
        set => SetProperty(ref _newPriority, value);
    }

    public ICommand AddCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync() => RunAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        var selectedId = SelectedProvider?.Id;
        Replace(
            Providers,
            (await _providers.GetAllAsync()).Where(x => x.Id != "codex-account"));
        SelectedProvider = Providers.FirstOrDefault(x => x.Id == selectedId) ?? Providers.FirstOrDefault();
        await LoadKeysAsync();
    }

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
        StatusMessage = $"{Keys.Count} key(s) stored for {SelectedProvider.Name}.";
    }

    private async Task AddCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        await _keys.AddAsync(provider.Id, NewLabel, NewSecret, NewPriority);
        NewLabel = string.Empty;
        NewSecret = string.Empty;
        await LoadKeysAsync();
        StatusMessage = "API key encrypted and saved.";
    }

    private async Task TestCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var key = SelectedKey
            ?? throw new InvalidOperationException("Select an API key first.");
        var result = await _tester.TestAsync(provider.Id, key.Id);
        await LoadKeysAsync();
        StatusMessage = result.Summary;
    }

    private async Task ToggleEnabledCoreAsync()
    {
        var key = SelectedKey
            ?? throw new InvalidOperationException("Select an API key first.");
        await _keys.SetEnabledAsync(key.Id, !key.Enabled);
        await LoadKeysAsync();
    }

    private async Task MoveCoreAsync(int offset)
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

    private async Task DeleteCoreAsync()
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
}

public sealed class ModelsViewModel : PageViewModel
{
    private readonly IProviderManager _providers;
    private readonly IModelDiscoveryService _discovery;
    private readonly ICompatibilityService _compatibility;
    private readonly IModelStore _models;
    private ProviderProfile? _selectedProvider;
    private ModelDescriptor? _selectedModel;

    public ModelsViewModel(
        IProviderManager providers,
        IModelDiscoveryService discovery,
        ICompatibilityService compatibility,
        IModelStore models)
        : base("Models", "Discover, verify and explicitly enable models for Codex.")
    {
        _providers = providers;
        _discovery = discovery;
        _compatibility = compatibility;
        _models = models;
        ScanCommand = new AsyncRelayCommand(() => RunAsync(ScanCoreAsync));
        TestCommand = new AsyncRelayCommand(() => RunAsync(TestCoreAsync));
        ToggleEnabledCommand = new AsyncRelayCommand(() => RunAsync(ToggleEnabledCoreAsync));
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadModelsAsync));
    }

    public ObservableCollection<ProviderProfile> Providers { get; } = new();
    public ObservableCollection<ModelDescriptor> Models { get; } = new();

    public ProviderProfile? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                _ = RunAsync(LoadModelsAsync);
            }
        }
    }

    public ModelDescriptor? SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    public ICommand ScanCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync() => RunAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        var selectedId = SelectedProvider?.Id;
        Replace(
            Providers,
            (await _providers.GetAllAsync()).Where(x => x.Id != "codex-account"));
        SelectedProvider = Providers.FirstOrDefault(x => x.Id == selectedId) ?? Providers.FirstOrDefault();
        await LoadModelsAsync();
    }

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
        StatusMessage = $"{Models.Count} model(s) known for {SelectedProvider.Name}.";
    }

    private async Task ScanCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        await _discovery.ScanAsync(provider.Id);
        await LoadModelsAsync();
        StatusMessage = $"Model catalog refreshed for {provider.Name}. New models remain disabled.";
    }

    private async Task TestCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var model = SelectedModel
            ?? throw new InvalidOperationException("Select a model first.");
        var result = await _compatibility.TestAsync(provider.Id, model.RemoteId);
        await LoadModelsAsync();
        SelectedModel = Models.First(x => x.RemoteId == model.RemoteId);
        StatusMessage = $"Compatibility {result.Score}/100 · Text {YesNo(result.Text)} · Streaming {YesNo(result.Streaming)} · Tools {YesNo(result.ToolCalling)}";
    }

    private async Task ToggleEnabledCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var model = SelectedModel
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
                : $"Revision {state.Revision} · {state.ChangedFiles.Count} changed file(s) · {state.UpdatedAt.LocalDateTime:g}";
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
        RevisionSummary = $"Revision {state.Revision} · {state.ChangedFiles.Count} changed file(s) · current";
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
        RevisionSummary = $"Revision {plan.ProjectState.Revision} · {plan.RecommendedSyncLevel} sync · {(plan.RequiresNewSession ? "new session" : "resume session")}";
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
    private string _gatewayStatus = "Stopped";
    private string _codexStatus = "Not checked";

    public DiagnosticsViewModel(IGatewayService gateway, ICodexConfigService config)
        : base("Diagnostics", "Inspect gateway and Codex configuration recovery state.")
    {
        _gateway = gateway;
        _config = config;
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

    public ICommand ToggleGatewayCommand { get; }
    public ICommand RestoreConfigCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync() => RunAsync(RefreshCoreAsync);

    private Task RefreshCoreAsync()
    {
        GatewayStatus = _gateway.IsRunning
            ? $"Listening on 127.0.0.1:{_gateway.Port}"
            : "Stopped";
        return RefreshCodexStatusAsync();
    }

    private async Task RefreshCodexStatusAsync()
    {
        CodexStatus = await _config.HasAccountProfileAsync()
            ? $"Account profile protected · {_config.CodexHome}"
            : $"Account profile not captured · {_config.CodexHome}";
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
