using System.Collections.ObjectModel;
using System.Windows.Input;
using AdamCodexHub.App.Mvvm;
using AdamCodexHub.App.Services;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int ProviderDisclosureVersion = 1;
    private readonly IProviderManager _providerManager;
    private readonly IModelStore _modelStore;
    private readonly IProviderActivationService _activation;
    private readonly IGatewayService _gateway;
    private readonly IAppSettingsService _settings;
    private readonly IUserDialogService _dialogs;
    private readonly SessionsViewModel _sessions;
    private PageViewModel _currentPage;
    private ProviderProfile? _selectedProvider;
    private ModelDescriptor? _selectedModel;
    private string _activeProvider = "Loading...";
    private string _activeModel = "Not selected";
    private string _gatewayStatus = "Stopped";
    private string _operationMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isBusy;

    // Cache used to re-localize title-bar text instantly when the UI language switches.
    private bool _noProviderShown;
    private string? _modelFallbackKey;

    public MainViewModel(
        IProviderManager providerManager,
        IModelStore modelStore,
        IProviderActivationService activation,
        IGatewayService gateway,
        IAppSettingsService appSettings,
        IUserDialogService dialogs,
        HomeViewModel home,
        ProviderSetupViewModel providers,
        SessionsViewModel sessions,
        DiagnosticsViewModel diagnostics,
        SettingsViewModel settings)
    {
        // Startup placeholders already reflect the persisted UI language (the async refresh
        // that replaces them runs right after the main window appears).
        _activeProvider = L10n.T("L10n_Main_Loading");
        _gatewayStatus = L10n.T("L10n_Main_GatewayStopped");
        _modelFallbackKey = null;
        _noProviderShown = false;
        _providerManager = providerManager;
        _modelStore = modelStore;
        _activation = activation;
        _gateway = gateway;
        _settings = appSettings;
        _dialogs = dialogs;
        _sessions = sessions;

        Pages = new ObservableCollection<PageViewModel>
        {
            home,
            providers,
            sessions,
            diagnostics,
            settings
        };
        _currentPage = home;

        NavigateCommand = new AsyncRelayCommand(NavigateAsync);
        ActivateCommand = new AsyncRelayCommand(ActivateAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

        L10n.LanguageChanged += ReapplyTitleBarText;
    }

    /// <summary>Re-localizes the fallback title-bar labels (no provider / no model / gateway).</summary>
    private void ReapplyTitleBarText()
    {
        if (_noProviderShown)
        {
            ActiveProvider = L10n.T("L10n_Main_NoProvider");
        }

        if (_modelFallbackKey is not null)
        {
            ActiveModel = L10n.T(_modelFallbackKey);
        }

        ApplyGatewayStatus();
    }

    public ObservableCollection<PageViewModel> Pages { get; }
    public ObservableCollection<ProviderProfile> Providers { get; } = new();
    public ObservableCollection<ModelDescriptor> EnabledModels { get; } = new();

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                foreach (var page in Pages)
                {
                    page.IsSelected = ReferenceEquals(page, value);
                }
            }
        }
    }

    public ProviderProfile? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                _ = RunAsync(() => LoadModelsAsync(value));
            }
        }
    }

    public ModelDescriptor? SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    public string ActiveProvider
    {
        get => _activeProvider;
        private set => SetProperty(ref _activeProvider, value);
    }

    public string ActiveModel
    {
        get => _activeModel;
        private set => SetProperty(ref _activeModel, value);
    }

    public string GatewayStatus
    {
        get => _gatewayStatus;
        private set => SetProperty(ref _gatewayStatus, value);
    }

    public string OperationMessage
    {
        get => _operationMessage;
        private set => SetProperty(ref _operationMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ICommand NavigateCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand RefreshCommand { get; }

    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            await _providerManager.InitializeAsync();
            await RefreshCoreAsync();
            await CurrentPage.InitializeAsync();
        });
    }

    private async Task NavigateAsync(object? parameter)
    {
        if (parameter is not PageViewModel page)
        {
            return;
        }

        CurrentPage = page;
        await page.InitializeAsync();
    }

    private Task RefreshAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        var selectedId = SelectedProvider?.Id;
        var active = await _providerManager.GetActiveAsync();
        Replace(Providers, await _providerManager.GetAllAsync());
        SelectedProvider = Providers.FirstOrDefault(x => x.Id == selectedId) ??
                           Providers.FirstOrDefault(x => x.Id == active?.Id) ??
                           Providers.FirstOrDefault();
        await LoadModelsAsync(SelectedProvider);

        _noProviderShown = active is null;
        ActiveProvider = active?.Name ?? L10n.T("L10n_Main_NoProvider");
        if (active is null || active.Id == "codex-account")
        {
            _modelFallbackKey = "L10n_Main_CodexManaged";
            ActiveModel = L10n.T(_modelFallbackKey);
        }
        else
        {
            var activeModels = await _modelStore.GetAllAsync(active.Id);
            var enabled = activeModels.FirstOrDefault(x => x.Enabled);
            if (enabled is null)
            {
                _modelFallbackKey = "L10n_Main_NoEnabledModel";
                ActiveModel = L10n.T(_modelFallbackKey);
            }
            else
            {
                _modelFallbackKey = null;
                ActiveModel = enabled.DisplayName;
            }
        }

        ApplyGatewayStatus();
    }

    private void ApplyGatewayStatus()
    {
        GatewayStatus = _gateway.IsRunning
            ? L10n.F("L10n_Main_GatewayPort", _gateway.Port)
            : L10n.T("L10n_Main_GatewayStopped");
    }

    private Task ActivateAsync() => RunAsync(async () =>
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));

        if (UsesRemoteEndpoint(provider) &&
            !await _settings.HasAcknowledgedProviderDisclosureAsync(
                provider.Id,
                ProviderDisclosureVersion))
        {
            var confirmed = _dialogs.Confirm(
                L10n.T("L10n_Msg_RemoteTitle"),
                L10n.F("L10n_Msg_DisclosureBody", provider.Name),
                L10n.F("L10n_Msg_ContinueAction", provider.Name));
            if (!confirmed)
            {
                OperationMessage = L10n.T("L10n_Msg_ActivationCanceled");
                return;
            }

            await _settings.AcknowledgeProviderDisclosureAsync(
                provider.Id,
                ProviderDisclosureVersion);
        }

        var result = await _activation.ActivateAsync(
            provider.Id,
            provider.Id == "codex-account" ? null : SelectedModel?.RemoteId,
            _sessions.ProjectPath);

        _noProviderShown = false;
        ActiveProvider = result.Provider.Name;
        _modelFallbackKey = result.Model is null ? "L10n_Main_CodexManaged" : null;
        ActiveModel = result.Model?.DisplayName ?? L10n.T("L10n_Main_CodexManaged");
        OperationMessage = result.Message;
        ApplyGatewayStatus();

        if (result.SessionPlan is not null)
        {
            _sessions.ContinuationInstruction = result.SessionPlan.ContinuationInstruction;
        }

        if (Pages.OfType<HomeViewModel>().FirstOrDefault() is { } home)
        {
            await home.InitializeAsync();
        }
    });

    private static bool UsesRemoteEndpoint(ProviderProfile provider)
    {
        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isHttpOrHttps =
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        return isHttpOrHttps && !uri.IsLoopback;
    }

    private async Task LoadModelsAsync(ProviderProfile? provider)
    {
        EnabledModels.Clear();
        if (provider is null || provider.Id == "codex-account")
        {
            SelectedModel = null;
            return;
        }

        var selectedId = SelectedModel?.RemoteId;
        foreach (var model in (await _modelStore.GetAllAsync(provider.Id))
                     .Where(x => x.Enabled && x.State == ModelLifecycleState.Enabled))
        {
            EnabledModels.Add(model);
        }

        SelectedModel = EnabledModels.FirstOrDefault(x => x.RemoteId == selectedId) ??
                        EnabledModels.FirstOrDefault();
    }

    private async Task RunAsync(Func<Task> action)
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
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
