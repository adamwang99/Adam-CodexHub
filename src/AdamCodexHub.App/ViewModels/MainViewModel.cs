using System.Collections.ObjectModel;
using System.Windows.Input;
using AdamCodexHub.App.Mvvm;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IProviderManager _providerManager;
    private readonly IModelStore _modelStore;
    private readonly IProviderActivationService _activation;
    private readonly IGatewayService _gateway;
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

    public MainViewModel(
        IProviderManager providerManager,
        IModelStore modelStore,
        IProviderActivationService activation,
        IGatewayService gateway,
        HomeViewModel home,
        ProvidersViewModel providersPage,
        ApiKeysViewModel apiKeys,
        ModelsViewModel models,
        SessionsViewModel sessions,
        DiagnosticsViewModel diagnostics,
        SettingsViewModel settings)
    {
        _providerManager = providerManager;
        _modelStore = modelStore;
        _activation = activation;
        _gateway = gateway;
        _sessions = sessions;

        Pages = new ObservableCollection<PageViewModel>
        {
            home,
            providersPage,
            apiKeys,
            models,
            sessions,
            diagnostics,
            settings
        };
        _currentPage = home;

        NavigateCommand = new AsyncRelayCommand(NavigateAsync);
        ActivateCommand = new AsyncRelayCommand(ActivateAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<PageViewModel> Pages { get; }
    public ObservableCollection<ProviderProfile> Providers { get; } = new();
    public ObservableCollection<ModelDescriptor> EnabledModels { get; } = new();

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
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

        ActiveProvider = active?.Name ?? "No provider";
        if (active is null || active.Id == "codex-account")
        {
            ActiveModel = "Codex managed";
        }
        else
        {
            var activeModels = await _modelStore.GetAllAsync(active.Id);
            ActiveModel = activeModels.FirstOrDefault(x => x.Enabled)?.DisplayName ?? "No enabled model";
        }

        GatewayStatus = _gateway.IsRunning
            ? $"Gateway :{_gateway.Port}"
            : "Gateway stopped";
    }

    private Task ActivateAsync() => RunAsync(async () =>
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException("Select a provider first.");
        var result = await _activation.ActivateAsync(
            provider.Id,
            provider.Id == "codex-account" ? null : SelectedModel?.RemoteId,
            _sessions.ProjectPath);

        ActiveProvider = result.Provider.Name;
        ActiveModel = result.Model?.DisplayName ?? "Codex managed";
        OperationMessage = result.Message;
        GatewayStatus = _gateway.IsRunning
            ? $"Gateway :{_gateway.Port}"
            : "Gateway stopped";

        if (result.SessionPlan is not null)
        {
            _sessions.ContinuationInstruction = result.SessionPlan.ContinuationInstruction;
        }

        if (Pages.OfType<HomeViewModel>().FirstOrDefault() is { } home)
        {
            await home.InitializeAsync();
        }
    });

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
