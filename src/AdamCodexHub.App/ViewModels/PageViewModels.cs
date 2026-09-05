using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AdamCodexHub.App.Mvvm;
using AdamCodexHub.App.Services;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Providers;

namespace AdamCodexHub.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;

    protected PageViewModel(string titleKey, string subtitleKey)
    {
        TitleKey = titleKey;
        SubtitleKey = subtitleKey;
        // Page VMs are app-lifetime singletons; the subscription lives for the whole app.
        L10n.LanguageChanged += NotifyLanguageChanged;
    }

    public string TitleKey { get; }
    public string SubtitleKey { get; }

    /// <summary>Localized page title, refreshed live when the UI language changes.</summary>
    public string Title => L10n.T(TitleKey);

    /// <summary>Localized page subtitle, refreshed live when the UI language changes.</summary>
    public string Subtitle => L10n.T(SubtitleKey);

    /// <summary>
    /// Raised on every language switch: re-raise the localized properties this page owns.
    /// Transient status/error messages keep the text they were composed with until the next
    /// operation refreshes them; steady-state labels are re-composed here.
    /// </summary>
    protected virtual void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    private bool _isSelected;
    /// <summary>True while this page is the one shown in the main window (drives the nav pill).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

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
            StatusMessage = L10n.T("L10n_Msg_Cancelled");
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

    /// <summary>Official Codex download page — offered when the Codex CLI is not installed.</summary>
    private const string CodexCliDownloadUrl = "https://openai.com/codex";

    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly IKeyPoolService _keys;
    private readonly IGatewayService _gateway;
    private readonly IProviderActivationService _activation;
    private readonly ICodexConfigService _config;
    private readonly IAppSettingsService _settings;
    private readonly IUserDialogService _dialogs;
    private readonly AppPaths _paths;

    public HomeViewModel(
        IProviderManager providers,
        IModelStore models,
        IKeyPoolService keys,
        IGatewayService gateway,
        IProviderActivationService activation,
        ICodexConfigService config,
        IAppSettingsService settings,
        IUserDialogService dialogs,
        AppPaths paths)
        : base("L10n_Home_Title", "L10n_Home_Subtitle")
    {
        _providers = providers;
        _models = models;
        _keys = keys;
        _gateway = gateway;
        _activation = activation;
        _config = config;
        _settings = settings;
        _dialogs = dialogs;
        _paths = paths;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(RefreshCoreAsync));
        ActivateCommand = new AsyncRelayCommand(ActivateAsync);
        DoubleClickCommand = new AsyncRelayCommand(p => DoubleClickAsync(p as ProviderCard));
        RestoreAccountCommand = new AsyncRelayCommand(RestoreAccountAsync);

        // Re-localize every card's computed text (READY/SETUP badge, key label, tooltips, model count).
        L10n.LanguageChanged += () =>
        {
            foreach (var card in Providers)
            {
                card.NotifyLocalizedText();
            }

            UpdateShowAllTooltip();
        };
    }

    /// <summary>Tooltip of the "Show all providers" switch, depends on the current toggle state.</summary>
    public string ShowAllTooltip => L10n.T(
        ShowAllProviders ? "L10n_Home_ShowAllOnTip" : "L10n_Home_ShowAllOffTip");

    private void UpdateShowAllTooltip() => OnPropertyChanged(nameof(ShowAllTooltip));

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
                UpdateShowAllTooltip();
                _ = RunAsync(RefreshCoreAsync);
            }
        }
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
        var selectedTarget = SelectedCard?.Target ?? CodexTarget.Windows;

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

            ProviderCard NewCard(CodexTarget target)
            {
                var (hasLogo, logoSource) = ResolveLogo(provider.Id);
                return new ProviderCard
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    BaseUrl = provider.BaseUrl,
                    Health = provider.Health,
                    Enabled = provider.Enabled,
                    EnabledModelCount = enabledModels,
                    HasUsableKey = hasUsableKey,
                    Target = target,
                    HasLogo = hasLogo,
                    LogoSource = logoSource,
                    IsActive = string.Equals(provider.Id, active?.Id, StringComparison.OrdinalIgnoreCase)
                };
            }

            if (provider.Id == ProviderManager.CodexAccountProviderId)
            {
                // The native account is a single card.
                built.Add(NewCard(CodexTarget.Windows));
            }
            else if (hasUsableKey)
            {
                // A keyed provider offers two cards: Windows Desktop and CLI.
                built.Add(NewCard(CodexTarget.Windows));
                built.Add(NewCard(CodexTarget.Cli));
            }
            else
            {
                // Not configured yet — a single card so the user can set it up.
                built.Add(NewCard(CodexTarget.Windows));
            }
        }

        // Show only configured/used providers (always Codex Account). Active provider first,
        // then those already configured (key/model), then the rest. "Show all" bypasses the filter.
        foreach (var card in built
                     .Where(x => ShowAllProviders || x.IsActive || x.IsConfigured || x.Id == ProviderManager.CodexAccountProviderId)
                     .OrderByDescending(x => x.IsActive)
                     .ThenByDescending(x => x.IsConfigured)
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Target))
        {
            Providers.Add(card);
        }

        SelectedCard = Providers.FirstOrDefault(x => x.Id == selectedId && x.Target == selectedTarget) ??
                       Providers.FirstOrDefault(x => x.Id == active?.Id) ??
                       Providers.FirstOrDefault();

        OnPropertyChanged(nameof(HasProviders));
        StatusMessage = L10n.F("L10n_Home_StatusShown", Providers.Count, DateTime.Now);
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
                    ? L10n.T("L10n_Home_ReadyToActivate")
                    : L10n.F("L10n_Home_NotReady", SelectedCard.Name);
                return;
            }

            if (SelectedCard.Id != "codex-account")
            {
                var enabledModel = (await _models.GetAllAsync(SelectedCard.Id))
                    .FirstOrDefault(x => x.Enabled && x.State == ModelLifecycleState.Enabled);
                if (enabledModel is null)
                {
                    StatusMessage = L10n.F("L10n_Home_NoEnabledModel", SelectedCard.Name);
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
                    L10n.T("L10n_Msg_RemoteTitle"),
                    L10n.F("L10n_Msg_DisclosureBody", SelectedCard.Name),
                    L10n.F("L10n_Msg_ContinueAction", SelectedCard.Name));
                if (!confirmed)
                {
                    StatusMessage = L10n.T("L10n_Msg_ActivationCanceled");
                    return;
                }

                await _settings.AcknowledgeProviderDisclosureAsync(
                    SelectedCard.Id,
                    ProviderDisclosureVersion);
            }

            if (SelectedCard.Id == "codex-account")
            {
                await _activation.ActivateAsync(SelectedCard.Id, null);
            }
            else if (SelectedCard.Target == CodexTarget.Windows)
            {
                // Windows card of a keyed provider: also overlay ~/.codex/config.toml with the
                // gateway so the Codex Desktop app runs on the provider's API key.
                await _activation.ActivateDesktopAsync(
                    SelectedCard.Id,
                    SelectedModelRemoteId);
            }
            else
            {
                await _activation.ActivateAsync(
                    SelectedCard.Id,
                    SelectedModelRemoteId);
            }

            await RefreshCoreAsync();
            StatusMessage = SelectedCard is null
                ? L10n.T("L10n_Home_ActivationDone")
                : L10n.F("L10n_Home_Activated", SelectedCard.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = L10n.F("L10n_Home_ActivationFailed", ex.Message);
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
            StatusMessage = L10n.F("L10n_Home_ActivationFailed", ex.Message);
            LogError("DoubleClick activate failed", ex);
            return;
        }

        // Only launch Codex when activation actually succeeded (no early return/error).
        if (StatusMessage == L10n.F("L10n_Home_Activated", card.Name))
        {
            // Windows (W) cards: the Codex DESKTOP app is launched. For keyed providers the
            // config overlay (ActivateDesktopAsync) points the Desktop app at our gateway so it
            // runs on the provider's API key — never the ChatGPT account quota. CLI cards run in
            // the sandboxed CODEX_HOME. Codex Account always opens the Desktop app.
            var launchDesktop = card.Target == CodexTarget.Windows;

            // Managed providers use either the config overlay (Desktop) or a private sandboxed
            // home (CLI). The native Codex Account keeps the real ~/.codex with no override.
            var codexHome = card.Id == ProviderManager.CodexAccountProviderId
                ? null
                : _config.GetGatewayHomePath(card.Id);
            await LaunchCodexAsync(launchDesktop, codexHome);
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
            StatusMessage = L10n.T("L10n_Home_AccountRestored");
            await LaunchCodexAsync(desktop: true, codexHome: null);
        }
        catch (Exception ex)
        {
            StatusMessage = L10n.F("L10n_Home_RestoreFailed", ex.Message);
            LogError("Restore account failed", ex);
        }
    }

    /// <summary>
    /// Launches Codex after a successful activation. Managed third-party providers run against a
    /// private sandboxed home passed through CODEX_HOME; the native Codex Account is launched with
    /// no override so it keeps using the real ~/.codex untouched.
    /// </summary>
    private Task LaunchCodexAsync(bool desktop, string? codexHome)
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
            // No Codex CLI on this machine (the app never bundles one): tell the user and offer
            // to open the official download page instead of silently doing nothing.
            try
            {
                StatusMessage = L10n.T("L10n_Msg_CliMissingStatus");
                var openDownload = _dialogs.Confirm(
                    L10n.T("L10n_Msg_CliMissingTitle"),
                    L10n.T("L10n_Msg_CliMissingBody"),
                    L10n.T("L10n_Msg_OpenDownload"));
                if (openDownload)
                {
                    Process.Start(new ProcessStartInfo(CodexCliDownloadUrl)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // Best-effort notification only; never throw from a launch helper.
            }

            return Task.CompletedTask;
        }

        try
        {
            // UseShellExecute must be false so the CODEX_HOME environment variable can be set for
            // managed providers; CreateNoWindow=false gives the console Codex CLI its own window.
            var startInfo = new ProcessStartInfo(codexPath)
            {
                WorkingDirectory = Path.GetDirectoryName(codexPath),
                UseShellExecute = false,
                CreateNoWindow = false
            };

            if (!string.IsNullOrWhiteSpace(codexHome))
            {
                startInfo.Environment["CODEX_HOME"] = codexHome;
            }

            Process.Start(startInfo);
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

    /// <summary>
    /// Resolves the logo for a provider: a user-provided image in the logos folder
    /// takes priority, then a bundled resource. Returns the availability flag and
    /// the image URI to bind to.
    /// </summary>
    private (bool HasLogo, string Source) ResolveLogo(string providerId)
    {
        // 1. User-provided logo (any supported extension) in the logos folder.
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var candidate = Path.Combine(_paths.Logos, $"{providerId}{extension}");
            if (File.Exists(candidate))
            {
                return (true, new Uri(candidate, UriKind.Absolute).AbsoluteUri);
            }
        }

        // 2. Bundled resource.
        var packUri = new Uri(
            $"pack://application:,,,/AdamCodexHub.App;component/Assets/providers/{providerId}.png",
            UriKind.Absolute);
        try
        {
            if (Application.GetResourceStream(packUri) is not null)
            {
                return (true, packUri.AbsoluteUri);
            }
        }
        catch (IOException)
        {
        }

        // 3. No image — the card falls back to the two-letter abbreviation.
        return (false, string.Empty);
    }
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

    /// <summary>Which Codex client this card activates: the Windows Desktop app or the CLI.</summary>
    public CodexTarget Target { get; init; } = CodexTarget.Windows;

    /// <summary>Whether a usable API key is present for this provider.</summary>
    public bool HasUsableKey { get; init; }

    /// <summary>True when the provider is set up (has a key or an enabled model) beyond Codex Account.</summary>
    public bool IsConfigured => Id == "codex-account" || HasUsableKey || EnabledModelCount > 0;

    /// <summary>True when the provider is ready to be activated.</summary>
    public bool IsValid =>
        Id == "codex-account" || (HasUsableKey && EnabledModelCount > 0);

    public string StatusLabel => IsValid
        ? L10n.T("L10n_Card_Ready")
        : L10n.T("L10n_Card_Setup");

    /// <summary>Small corner badge: "W" for Windows Desktop, "CLI" for the terminal.</summary>
    public string TargetLabel => Target == CodexTarget.Cli ? "CLI" : "W";

    /// <summary>Hover explanation for the W/CLI corner badge.</summary>
    public string TargetTooltip =>
        Target == CodexTarget.Cli
            ? L10n.T("L10n_Card_Tip_Cli")
            : Id == ProviderManager.CodexAccountProviderId
                ? L10n.T("L10n_Card_Tip_Win")
                : L10n.T("L10n_Card_Tip_WinKeyed");

    /// <summary>Enabled-model counter line under the logo (localized "{0} model(s)").</summary>
    public string EnabledModelLabel => L10n.F("L10n_Card_ModelCount", EnabledModelCount);

    /// <summary>Two-letter abbreviation shown when the provider has no logo image.</summary>
    public string Initials
    {
        get
        {
            var words = Name.Split(
                new[] { ' ', '-', '_', '.' },
                StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
            {
                return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
            }

            var letters = new string(Name
                .Where(char.IsLetterOrDigit)
                .Take(2)
                .ToArray()).ToUpperInvariant();
            return letters.Length > 0 ? letters : "?";
        }
    }

    /// <summary>True when a bundled or user-provided logo image exists for this provider.</summary>
    public bool HasLogo { get; init; } = true;

    /// <summary>URI of the logo image (bundled pack URI or a user-provided file).</summary>
    public string LogoSource { get; init; } = string.Empty;

    public string KeyLabel => HasUsableKey
        ? L10n.T("L10n_Card_KeyPresent")
        : L10n.T("L10n_Card_KeyNeeded");

    public string TooltipDescription =>
        Id == ProviderManager.CodexAccountProviderId
            ? L10n.T("L10n_Card_Desc_Account")
            : HasUsableKey && EnabledModelCount > 0
                ? Target == CodexTarget.Cli
                    ? L10n.T("L10n_Card_Desc_CliReady")
                    : L10n.T("L10n_Card_Desc_WinKeyedReady")
                : L10n.T("L10n_Card_Desc_SetupNeeded");

    /// <summary>
    /// Re-raises every language-sensitive computed property so bound cards repaint after a
    /// UI-language switch (invoked by HomeViewModel when L10n.LanguageChanged fires).
    /// </summary>
    public void NotifyLocalizedText()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(TargetTooltip));
        OnPropertyChanged(nameof(EnabledModelLabel));
        OnPropertyChanged(nameof(KeyLabel));
        OnPropertyChanged(nameof(TooltipDescription));
    }
}

public enum CodexTarget
{
    Windows,
    Cli
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
        : base("L10n_Setup_Title", "L10n_Setup_Subtitle")
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
        ChooseLogoCommand = new AsyncRelayCommand(() => RunAsync(ChooseLogoCoreAsync));
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
    public ICommand ChooseLogoCommand { get; }
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
        StatusMessage = L10n.F("L10n_Setup_CountMsg", Providers.Count);
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
        StatusMessage = L10n.F("L10n_Setup_SavedMsg", SelectedProvider.Name);
    }

    private async Task ToggleProviderCoreAsync()
    {
        var selected = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        await _providers.SetEnabledAsync(selected.Id, !selected.Enabled);
        await LoadAsync();
        StatusMessage = selected.Enabled
            ? L10n.F("L10n_Setup_ProviderDisabledMsg", selected.Name)
            : L10n.F("L10n_Setup_ProviderEnabledMsg", selected.Name);
    }

    private async Task DeleteProviderCoreAsync()
    {
        var selected = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        if (!_dialogs.Confirm(
                L10n.T("L10n_Setup_DeleteTitle"),
                L10n.F("L10n_Setup_DeleteBody", selected.Name),
                L10n.F("L10n_Setup_DeleteAction", selected.Name)))
        {
            return;
        }

        await _providers.DeleteAsync(selected.Id);
        await LoadAsync();
        StatusMessage = L10n.F("L10n_Setup_DeletedMsg", selected.Name);
    }

    private void ClearEditor()
    {
        SelectedProvider = null;
        ProviderId = string.Empty;
        ProviderName = string.Empty;
        BaseUrl = string.Empty;
        Adapter = "openai-compatible";
        StatusMessage = L10n.T("L10n_Setup_NewProfileMsg");
    }

    private async Task ChooseLogoCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));

        var saved = _dialogs.PickProviderLogo(provider.Id);
        if (saved is null)
        {
            StatusMessage = L10n.T("L10n_Setup_LogoCanceled");
            return;
        }

        StatusMessage = L10n.F("L10n_Setup_LogoUpdated", provider.Name);
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
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        if (string.IsNullOrWhiteSpace(NewKey))
        {
            StatusMessage = L10n.T("L10n_Setup_NeedPaste");
            return;
        }

        await _keys.AddAsync(provider.Id, "default", NewKey.Trim());
        NewKey = string.Empty;
        await LoadKeysAsync();
        StatusMessage = L10n.F("L10n_Setup_KeyStored", provider.Name);
    }

    private async Task TestKeyCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        var key = SelectedKey
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_SelectKeyFirst"));
        var result = await _tester.TestAsync(provider.Id, key.Id);
        await LoadKeysAsync();
        StatusMessage = result.Summary;
    }

    private async Task TestAllKeysCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        var keys = Keys.ToArray();
        if (keys.Length == 0)
        {
            StatusMessage = L10n.F("L10n_Setup_NoKeys", provider.Name);
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

        // KeyTestService updates provider health behind the scenes — reload the provider
        // rows so the status dot / health no longer shows a stale "Unknown".
        await RefreshProviderRowsAsync();
        await LoadKeysAsync();
        StatusMessage = L10n.F("L10n_Setup_KeySummary", provider.Name, ok, fail, keys.Length);
        if (notes.Count > 0 && fail > 0)
        {
            StatusMessage += " " + L10n.F("L10n_Setup_NextNote", notes[0]);
        }
    }

    /// <summary>
    /// Re-reads provider profiles (health may have been updated by key/model tests) and
    /// refreshes the provider list while keeping the current selection.
    /// </summary>
    private async Task RefreshProviderRowsAsync()
    {
        var selectedId = SelectedProvider?.Id;
        var fresh = (await _providers.GetAllAsync())
            .Where(x => x.Id != "codex-account")
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Replace(Providers, fresh);
        SelectedProvider = Providers.FirstOrDefault(x => x.Id == selectedId) ?? Providers.FirstOrDefault();
    }

    private async Task ToggleKeyCoreAsync()
    {
        var key = SelectedKey
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_SelectKeyFirst"));
        await _keys.SetEnabledAsync(key.Id, !key.Enabled);
        await LoadKeysAsync();
    }

    private async Task MoveKeyCoreAsync(int offset)
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        var key = SelectedKey
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_SelectKeyFirst"));
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
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_SelectKeyFirst"));
        if (!_dialogs.Confirm(
                L10n.T("L10n_Setup_DeleteKeyTitle"),
                L10n.F("L10n_Setup_DeleteKeyBody", key.Label),
                L10n.F("L10n_Setup_DeleteKeyAction", key.Label)))
        {
            return;
        }

        await _keys.DeleteAsync(key.Id);
        await LoadKeysAsync();
        StatusMessage = L10n.F("L10n_Setup_KeyDeletedMsg", key.Label);
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
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        try
        {
            await _discovery.ScanAsync(provider.Id);
            await LoadModelsAsync();
            StatusMessage = L10n.F("L10n_Setup_Scanned", provider.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase)
                ? L10n.T("L10n_Setup_Unauthorized")
                : L10n.F("L10n_Setup_ScanFailed", provider.Name, ex.Message);
        }
    }

    private async Task TestAllModelsCoreAsync()
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        var models = Models.ToArray();
        if (models.Length == 0)
        {
            StatusMessage = L10n.F("L10n_Setup_NoModels", provider.Name);
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

        // CompatibilityService updates provider health — reload rows so the list dot /
        // any health badge stops showing a stale "Unknown" after the run.
        await RefreshProviderRowsAsync();
        await LoadModelsAsync();
        StatusMessage = L10n.F("L10n_Setup_ModelsTested", provider.Name, verified, models.Length);
        if (notes.Count > 0)
        {
            StatusMessage += " " + L10n.F("L10n_Setup_FirstIssue", notes[0]);
        }
    }

    private async Task TestModelCoreAsync(ModelDescriptor? model)
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        model ??= SelectedModel
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_SelectModelFirst"));

        _dialogs.ShowModelTest(provider.Name, provider.Id, model.DisplayName, model.RemoteId);
        await LoadModelsAsync();
        SelectedModel = Models.FirstOrDefault(x => x.RemoteId == model.RemoteId);
    }

    private async Task ToggleModelCoreAsync(ModelDescriptor? model)
    {
        var provider = SelectedProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoProviderSelected"));
        model ??= SelectedModel
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_SelectModelFirst"));
        await _models.SetEnabledAsync(provider.Id, model.RemoteId, !model.Enabled);
        await LoadModelsAsync();
        SelectedModel = Models.First(x => x.RemoteId == model.RemoteId);
        StatusMessage = model.Enabled
            ? L10n.F("L10n_Setup_ModelDisabledMsg", model.DisplayName)
            : L10n.F("L10n_Setup_ModelEnabledMsg", model.DisplayName);
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
    private string _revisionSummary = string.Empty;
    private string _continuationInstruction = string.Empty;

    public SessionsViewModel(
        IProviderManager providers,
        IProjectStateService projectState,
        ISessionContinuityService sessions)
        : base("L10n_Session_Title", "L10n_Session_Subtitle")
    {
        _providers = providers;
        _projectState = projectState;
        _sessions = sessions;
        RefreshProjectCommand = new AsyncRelayCommand(() => RunAsync(RefreshProjectCoreAsync));
        PrepareSwitchCommand = new AsyncRelayCommand(() => RunAsync(PrepareSwitchCoreAsync));
        RevisionSummary = L10n.T("L10n_Session_NotSync");
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

    private bool _everSynced;

    /// <summary>
    /// True once a real project read/refresh produced a revision summary; while false the
    /// default "Not synchronized" placeholder is re-localized on language switches.
    /// </summary>
    public bool HasRealSummary => _everSynced;

    protected override void NotifyLanguageChanged()
    {
        base.NotifyLanguageChanged();
        if (!_everSynced)
        {
            RevisionSummary = L10n.T("L10n_Session_NotSync");
        }
    }

    public override Task InitializeAsync() => RunAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        Replace(Providers, await _providers.GetAllAsync());
        var active = await _providers.GetActiveAsync();
        SelectedTargetProvider = Providers.FirstOrDefault(x => x.Id != active?.Id) ?? Providers.FirstOrDefault();

        if (Directory.Exists(ProjectPath))
        {
            var state = await _projectState.ReadAsync(ProjectPath);
            if (state is null)
            {
                RevisionSummary = L10n.T("L10n_Session_NotYetSync");
            }
            else
            {
                _everSynced = true;
                RevisionSummary = L10n.F(
                    "L10n_Session_RevSummary",
                    state.Revision,
                    state.ChangedFiles.Count,
                    state.UpdatedAt.LocalDateTime);
            }
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
        _everSynced = true;
        RevisionSummary = L10n.F(
            "L10n_Session_RevCurrent",
            state.Revision,
            state.ChangedFiles.Count);
        StatusMessage = L10n.T("L10n_Session_Refreshed");
    }

    private async Task PrepareSwitchCoreAsync()
    {
        EnsureProjectPath();
        var source = await _providers.GetActiveAsync()
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_NoActiveProvider"));
        var target = SelectedTargetProvider
            ?? throw new InvalidOperationException(L10n.T("L10n_Msg_SelectTarget"));
        if (source.Id == target.Id)
        {
            throw new InvalidOperationException(L10n.T("L10n_Msg_SameTarget"));
        }

        var plan = await _sessions.PrepareSwitchAsync(ProjectPath, source.Id, target.Id);
        ContinuationInstruction = plan.ContinuationInstruction;
        _everSynced = true;
        RevisionSummary = L10n.F(
            "L10n_Session_RevSwitch",
            plan.ProjectState.Revision,
            plan.RecommendedSyncLevel,
            plan.RequiresNewSession
                ? L10n.T("L10n_Session_NewSession")
                : L10n.T("L10n_Session_ResumeSession"));
        StatusMessage = L10n.T("L10n_Session_HandoffReady");
    }

    private void EnsureProjectPath()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath))
        {
            throw new DirectoryNotFoundException(L10n.T("L10n_Msg_BadProjectDir"));
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
        : base("L10n_Diag_Title", "L10n_Diag_Subtitle")
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
            ? L10n.F("L10n_Diag_Listening", _gateway.Port)
            : L10n.T("L10n_Diag_Stopped");
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
            ProviderWarnings = L10n.F("L10n_Diag_LoadCheckFailed", ex.Message);
        }
    }

    private async Task RefreshCodexStatusAsync()
    {
        CodexStatus = await _config.HasAccountProfileAsync()
            ? L10n.F("L10n_Diag_AccountProtected", _config.CodexHome)
            : L10n.F("L10n_Diag_AccountNotCaptured", _config.CodexHome);
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
        StatusMessage = _gateway.IsRunning
            ? L10n.T("L10n_Diag_GatewayStarted")
            : L10n.T("L10n_Diag_GatewayStopped");
    }

    private async Task RestoreConfigCoreAsync()
    {
        await _config.RestoreLastKnownGoodAsync();
        await RefreshCoreAsync();
        StatusMessage = L10n.T("L10n_Diag_ConfigRestored");
    }
}

public sealed class SettingsViewModel : PageViewModel
{
    public SettingsViewModel()
        : base("L10n_Set_Title", "L10n_Set_Subtitle")
    {
        StatusMessage = L10n.T("L10n_Set_DefaultsMsg");
    }

    protected override void NotifyLanguageChanged()
    {
        base.NotifyLanguageChanged();
        StatusMessage = L10n.T("L10n_Set_DefaultsMsg");
    }
}
