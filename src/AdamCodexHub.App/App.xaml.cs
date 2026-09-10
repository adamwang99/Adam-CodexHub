using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using AdamCodexHub.App.ViewModels;
using AdamCodexHub.App.Services;
using AdamCodexHub.App.Converters;
using AdamCodexHub.Codex;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Gateway;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Keys;
using AdamCodexHub.Infrastructure.Models;
using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Infrastructure.Providers;
using AdamCodexHub.Infrastructure.Security;
using AdamCodexHub.Infrastructure.Settings;
using AdamCodexHub.Providers;
using AdamCodexHub.Providers.Adapters;
using AdamCodexHub.Providers.Registry;
using AdamCodexHub.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AdamCodexHub.App;

public partial class App : Application
{
    private const int RequiredSessionAcknowledgementVersion = 2;
    private const string SingleInstanceMutexName = "Global\\AdamCodexHub.SingleInstance.v1";
    private const string ShowSignalName = "Global\\AdamCodexHub.ShowWindow.v1";

    public const string ThemeDark = "dark";
    public const string ThemeLight = "light";
    public static string CurrentTheme { get; private set; } = ThemeDark;
    private static Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSignal;
    private IHost? _host;
    private WinForms.NotifyIcon? _trayIcon;
    private CodexSessionModelWatchService? _sessionWatch;
    private Drawing.Icon? _trayIconImage;

    /// <summary>
    /// True once the user has chosen a real exit (tray "Exit" or OS session ending). Window
    /// closing is intercepted otherwise, so the app hides to the system tray and the in-process
    /// gateway keeps running.
    /// </summary>
    public static bool IsRealExit { get; set; }

    /// <summary>
    /// Swaps the active UI-language dictionary (Locale.EN.xaml &lt;=&gt; Locale.VI.xaml),
    /// updates the static <see cref="L10n"/> state and persists the choice. Every
    /// {DynamicResource L10n_...} reference re-resolves against the new dictionary, then
    /// L10n.LanguageChanged lets ViewModels re-notify their C#-composed strings.
    /// </summary>
    public static void ApplyLanguage(string language)
    {
        var target = language == L10n.Vietnamese ? L10n.Vietnamese : L10n.English;

        try
        {
            if (Current?.Resources is { MergedDictionaries.Count: > 1 } resources)
            {
                var dict = resources.MergedDictionaries[1];
                dict.Source = new Uri(
                    $"Resources/Locales/Locale.{target.ToUpperInvariant()}.xaml",
                    UriKind.Relative);
            }
        }
        catch (Exception swapEx)
        {
            LogStartup("ApplyLanguage dictionary swap failed", swapEx);
        }

        L10n.SetLanguage(target);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        UiSettingsStore.SaveLanguage(Path.Combine(localAppData, "AdamCodexHub"), target);
    }

    /// <summary>
    /// Swaps the merged color-theme dictionary (index 0) so every {DynamicResource ...}
    /// palette reference re-resolves; persists the choice for the next launch.
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        var target = theme == ThemeLight ? ThemeLight : ThemeDark;

        try
        {
            if (Current?.Resources is { MergedDictionaries.Count: > 0 } resources)
            {
                var dict = resources.MergedDictionaries[0];
                dict.Source = new Uri(
                    $"Resources/Themes/Theme.{char.ToUpperInvariant(target[0])}{target[1..]}.xaml",
                    UriKind.Relative);
            }
        }
        catch (Exception swapEx)
        {
            LogStartup("ApplyTheme dictionary swap failed", swapEx);
        }

        CurrentTheme = target;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        UiSettingsStore.SaveTheme(Path.Combine(localAppData, "AdamCodexHub"), target);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LogStartup("OnStartup entered");

        try
        {
            var ownsMutex = false;
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out ownsMutex);
            if (!ownsMutex)
            {
                LogStartup("Second instance blocked");
                // Ask the running primary to bring its window up (it may be hidden in the tray).
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                    signal.Set();
                }
                catch
                {
                    // Primary is still starting up; nothing to signal yet. Fall through.
                }

                Shutdown();
                return;
            }

            // Restore the persisted UI language and color theme BEFORE any window is created so
            // the very first frame is already localized and themed correctly.
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataRoot = Path.Combine(localAppData, "AdamCodexHub");
            ApplyLanguage(UiSettingsStore.LoadLanguage(appDataRoot));
            ApplyTheme(UiSettingsStore.LoadTheme(appDataRoot));
            LogStartup($"UI language: {L10n.CurrentLanguage}, theme: {CurrentTheme}");

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddHttpClient();

                services.AddSingleton<AppPaths>();
                services.AddSingleton<SqliteDatabase>();

                services.AddSingleton<IAppSettingsService, AppSettingsService>();
                services.AddSingleton<IKeyVault, DpapiKeyVault>();
                services.AddSingleton<IKeyPoolService, SqliteKeyPoolService>();
                services.AddSingleton<IProviderStore, SqliteProviderStore>();
                services.AddSingleton<IModelStore, SqliteModelStore>();

                services.AddSingleton<IProviderRegistryService, EmbeddedProviderRegistryService>();
                services.AddSingleton<IProviderManager, ProviderManager>();

                services.AddSingleton<OpenAiCompatibleAdapter>();
                services.AddSingleton<IProviderAdapter>(sp =>
                    sp.GetRequiredService<OpenAiCompatibleAdapter>());
                services.AddSingleton<IProviderAdapter, OpenAiResponsesAdapter>();
                services.AddSingleton<IModelDiscoveryService, ModelDiscoveryService>();
                services.AddSingleton<ICompatibilityService, CompatibilityService>();
                services.AddSingleton<IKeyTestService, KeyTestService>();
                // Background refresher for the active provider's models (latency-aware badges).
                services.AddSingleton<ModelAutoPingService>();

                // Codex-readiness probe: keeps the model list Codex Desktop and the tray are
                // offered limited to models that really answer a Codex request (tools +
                // tool_choice = required), re-checked every 5 minutes.
                services.AddSingleton<ICodexReadinessStore, SqliteCodexReadinessStore>();
                services.AddSingleton<ICodexReadinessChecker, CodexReadinessChecker>();
                services.AddSingleton<CodexReadinessPingService>();

                services.AddSingleton<ICodexConfigService, CodexConfigService>();
                // Codex → hub sync: the model picked inside Codex Desktop lives per thread in
                // ~/.codex/state_5.sqlite (its picker never rewrites config.toml), so poll it and
                // publish what Codex really runs to the Home card and the tray menu.
                services.AddSingleton<ICodexSessionModelReader>(_ => new CodexSessionModelReader());
                services.AddSingleton<CodexSessionModelWatchService>();
                services.AddSingleton<IProjectStateService, FileProjectStateService>();
                services.AddSingleton<ISessionContinuityService, SessionContinuityService>();
                services.AddSingleton<IProviderActivationService, ProviderActivationService>();
                services.AddSingleton<ProviderShutdownService>();

                services.AddSingleton<IGatewayService, LocalGatewayService>();
                services.AddSingleton<IUserDialogService, UserDialogService>();
                services.AddSingleton<StartupKeyRevalidator>();

                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<ProviderSetupViewModel>();
                services.AddSingleton<SessionsViewModel>();
                services.AddSingleton<DiagnosticsViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();
            LogStartup("Host built");

            await _host.StartAsync();
            LogStartup("Host started");

            var database = _host.Services.GetRequiredService<SqliteDatabase>();
            await database.InitializeAsync();
            LogStartup("Database initialized");

            var settings = _host.Services.GetRequiredService<IAppSettingsService>();
            var acknowledged = await settings.HasAcknowledgedSessionMechanismAsync(
                RequiredSessionAcknowledgementVersion);
            LogStartup($"Acknowledgement checked: {acknowledged}");

            if (!acknowledged)
            {
                var dialog = new FirstRunAcknowledgementWindow(settings);
                LogStartup("First-run dialog created");
                var accepted = dialog.ShowDialog();
                LogStartup($"First-run dialog closed: {accepted}");

                if (accepted != true)
                {
                    Shutdown();
                    return;
                }
            }

            // Startup routing: put Codex Desktop back on the provider the user last ran.
            // If that provider is a keyed third-party channel (DeepSeek, TTMAPI, 9Router…), the
            // gateway overlay is re-written automatically (stable port + persisted token), so a
            // restart never strands Codex on the account model list. Only when there is no active
            // keyed provider does the old heal logic restore the native Codex Account profile.
            try
            {
                var routingProviders = _host.Services.GetRequiredService<IProviderManager>();
                var activeRoutingProvider = await routingProviders.GetActiveAsync();
                if (activeRoutingProvider is not null &&
                    !string.Equals(activeRoutingProvider.Id, "codex-account", StringComparison.OrdinalIgnoreCase) &&
                    activeRoutingProvider.Enabled)
                {
                    var modelStore = _host.Services.GetRequiredService<IModelStore>();
                    var enabledModel = (await modelStore.GetAllAsync(
                            activeRoutingProvider.Id,
                            CancellationToken.None))
                        .FirstOrDefault(x =>
                            x.Enabled && x.State == AdamCodexHub.Core.Domain.ModelLifecycleState.Enabled);
                    if (enabledModel is not null)
                    {
                        var activation = _host.Services.GetRequiredService<IProviderActivationService>();
                        await activation.ActivateDesktopAsync(
                            activeRoutingProvider.Id,
                            enabledModel.RemoteId,
                            projectPath: null,
                            CancellationToken.None);
                        LogStartup(
                            $"Startup overlay: Codex Desktop routed to {activeRoutingProvider.Name} / {enabledModel.RemoteId}.");
                    }
                    else
                    {
                        LogStartup(
                            $"Startup routing: provider {activeRoutingProvider.Name} has no enabled model; keeping account config.");
                    }
                }
                else
                {
                    var healConfig = _host.Services.GetRequiredService<ICodexConfigService>();
                    if (await healConfig.RestoreAccountIfGatewayOverlayAsync())
                    {
                        await routingProviders.SetActiveAsync("codex-account");
                        LogStartup("Startup heal: restored ~/.codex account config (stale gateway overlay).");
                    }
                }
            }
            catch (Exception healEx)
            {
                LogStartup("Startup heal failed", healEx);
            }

            var window = _host.Services.GetRequiredService<MainWindow>();
            LogStartup("Main window resolved");
            MainWindow = window;
            window.Show();

            // ShutdownMode stays OnExplicitShutdown (declared in App.xaml): closing the window
            // only hides it to the tray while the in-process gateway keeps serving Codex.
            InitializeTrayIcon(window);
            LogStartup("Tray icon initialized");
            LogStartup("Main window shown");

            // A second launch signals us (a named event) to surface the window from the tray,
            // so "open the app again" actually brings it to the front instead of doing nothing.
            try
            {
                _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
                _ = Task.Run(async () =>
                {
                    while (_showSignal is { } signal)
                    {
                        try
                        {
                            await Task.Run(() => signal.WaitOne());
                        }
                        catch
                        {
                            break;
                        }

                        await Dispatcher.InvokeAsync(() => ShowMainWindow(window));
                    }
                });
            }
            catch (Exception signalEx)
            {
                LogStartup("Show-signal listener unavailable", signalEx);
            }

            // Latency-aware model classification: start the bounded background refresher that
            // re-tests a few models of the ACTIVE provider every 15 minutes and publishes the
            // result to ModelStatusState (the single source of truth the badges read).
            try
            {
                var autoPing = _host.Services.GetRequiredService<ModelAutoPingService>();
                autoPing.LogMessage += (message, pingException) =>
                    LogStartup($"Model auto-ping: {message}", pingException);
                autoPing.TickCompleted += tick => Dispatcher.InvokeAsync(
                    () => ModelStatusState.Current.Apply(tick));
                autoPing.Start();
                LogStartup(
                    $"Model auto-ping started (every {autoPing.Interval.TotalMinutes:0} min, " +
                    $"up to {autoPing.BatchSize} model(s) per tick)");
            }
            catch (Exception pingEx)
            {
                LogStartup("Model auto-ping could not start", pingEx);
            }

            // Codex readiness: every 5 minutes re-run the Codex-shaped probe (instructions + tools)
            // for the active provider's models that are not ready yet, so Codex Desktop's picker
            // and the tray menu only ever offer models that work — and pick up a model the moment
            // it starts working.
            try
            {
                var readiness = _host.Services.GetRequiredService<CodexReadinessPingService>();
                readiness.LogMessage += (message, readinessException) =>
                    LogStartup($"Codex readiness: {message}", readinessException);
                readiness.TickCompleted += tick => Dispatcher.InvokeAsync(
                    () => ModelStatusState.Current.ApplyReadiness(tick));
                readiness.Start();
                LogStartup(
                    $"Codex readiness probe started (every {readiness.Interval.TotalMinutes:0} min, " +
                    $"up to {readiness.BatchSize} model(s) per tick)");
            }
            catch (Exception readinessEx)
            {
                LogStartup("Codex readiness probe could not start", readinessEx);
            }

            // Codex → hub sync: switching the model inside Codex Desktop's own picker writes it to
            // the thread (state_5.sqlite) and never to ~/.codex/config.toml, so the hub polls
            // Codex's state every few seconds and mirrors the value into the Home card dropdown,
            // the tray tick and the tray's "Codex is using…" header.
            try
            {
                _sessionWatch = _host.Services.GetRequiredService<CodexSessionModelWatchService>();
                LogStartup(
                    $"Codex session watch started (every {_sessionWatch.Interval.TotalSeconds:0}s)");
                // Publish the value we already have first (one log line, no subscriber yet), then
                // start following changes: the timer's own first tick sees no change.
                ApplyCodexSession(_sessionWatch.Poll());
                _sessionWatch.Changed += OnCodexSessionModelChanged;
                _sessionWatch.Start();
            }
            catch (Exception sessionEx)
            {
                LogStartup("Codex session watch could not start", sessionEx);
            }

            // Keys left degraded (Offline / rate-limited / stale Unknown) by transient gateway
            // failures in the previous session get one quiet probe each, so an installed and
            // previously-tested provider is usable right away — no manual re-test required.
            // Runs bounded (8s cap) so startup never stalls on a dead provider.
            try
            {
                using var revalidationCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var revalidator = _host.Services.GetRequiredService<StartupKeyRevalidator>();
                await revalidator.RevalidateAsync(revalidationCts.Token);
                LogStartup("Startup key revalidation completed");
            }
            catch (OperationCanceledException)
            {
                LogStartup("Startup key revalidation timed out after 8s");
            }
            catch (Exception revalidateEx)
            {
                LogStartup("Startup key revalidation failed", revalidateEx);
            }

            await window.ViewModel.InitializeAsync();
            LogStartup("Main view model initialized");
            LogProviderStartupWarnings(_host);
        }
        catch (Exception ex)
        {
            LogStartup("Startup failed", ex);
            MessageBox.Show(
                ex.Message,
                L10n.T("L10n_App_StartupErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var host = _host;
        _host = null;

        try
        {
            _sessionWatch?.Stop();
            _sessionWatch = null;
        }
        catch
        {
            // Exiting: the watcher's timer is best-effort cleanup only.
        }

        DisposeTrayIcon();

        if (host is not null)
        {
            Task.Run(() => ShutdownHostAsync(host)).GetAwaiter().GetResult();
            host.Dispose();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _showSignal?.Dispose();
        _showSignal = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Never let close-to-tray block a Windows logoff/shutdown; stop the host gracefully
        // (gateway + state) before the OS tears the session down.
        IsRealExit = true;
        base.OnSessionEnding(e);
        if (!e.Cancel)
        {
            Shutdown();
        }
    }

    private void InitializeTrayIcon(Window window)
    {
        var menu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem(L10n.T("L10n_Tray_Show"));
        showItem.Click += (_, _) => ShowMainWindow(window);
        var modelItem = new WinForms.ToolStripMenuItem(L10n.T("L10n_Tray_Model"));
        modelItem.DropDownOpening += (_, _) => PopulateTrayModelMenu(modelItem);
        var exitItem = new WinForms.ToolStripMenuItem(L10n.T("L10n_Tray_Exit"));
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(showItem);
        menu.Items.Add(modelItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIconImage = LoadTrayIcon();
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "Adam CodexHub",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            BalloonTipTitle = L10n.T("L10n_Tray_NotificationTitle"),
            BalloonTipText = L10n.T("L10n_Tray_CodexLaunched"),
            BalloonTipIcon = WinForms.ToolTipIcon.Info,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow(window);
    }

    /// <summary>Colour of a tray model entry, matching the in-app callability badges:
    /// green = callable, amber = slow, red = skipped, grey = not classified yet.</summary>
    private static Drawing.Color TrayCallabilityColor(Callability callability) => callability switch
    {
        Callability.Callable => Drawing.Color.FromArgb(0x2F, 0xD0, 0xC8),
        Callability.Slow => Drawing.Color.FromArgb(0xE0, 0xA8, 0x3C),
        Callability.Skip => Drawing.Color.FromArgb(0xE6, 0x6A, 0x6A),
        _ => Drawing.Color.FromArgb(0xC4, 0xC4, 0xC4)
    };

    /// <summary>Model label for the tray menu: the name plus its one-letter callability tag,
    /// e.g. "claude-opus-4-6[1M] (F)". Same classifier as the colour beside it, so colour and
    /// letter can never disagree.</summary>
    private static string TrayModelLabel(
        ModelStatusState status,
        string providerId,
        string? modelId,
        string name)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return name;
        }

        var snapshot = status.GetSnapshot(providerId, modelId);
        var tag = ModelCallability.StatusTag(
            status.GetCallability(providerId, modelId),
            snapshot?.FirstByteMs,
            snapshot?.TotalMs);
        return $"{name} ({tag})";
    }

    /// <summary>Build the tray "Model" submenu: only the active provider's enabled models,
    /// each coloured by its latency-aware callability. Models classified "Skip" are hidden
    /// unless the "show all models" preference is on; the pause / show-all switches live at
    /// the bottom of the same submenu.</summary>
    private void PopulateTrayModelMenu(WinForms.ToolStripMenuItem modelMenu)
    {
        modelMenu.DropDownItems.Clear();
        if (_host is null)
        {
            modelMenu.DropDownItems.Add(L10n.T("L10n_Tray_NoModel"));
            return;
        }

        try
        {
            var providers = _host.Services.GetRequiredService<IProviderManager>();
            var modelStore = _host.Services.GetRequiredService<IModelStore>();
            var activation = _host.Services.GetRequiredService<IProviderActivationService>();
            var configService = _host.Services.GetRequiredService<ICodexConfigService>();
            var autoPing = _host.Services.GetRequiredService<ModelAutoPingService>();
            var status = ModelStatusState.Current;
            var window = MainWindow;

            var active = providers.GetActiveAsync().GetAwaiter().GetResult();
            if (active is null || active.Id == "codex-account")
            {
                modelMenu.DropDownItems.Add(L10n.T("L10n_Tray_NoModel"));
                return;
            }

            var enabled = modelStore.GetAllAsync(active.Id).GetAwaiter().GetResult()
                .Where(m => m.Enabled && m.State == ModelLifecycleState.Enabled)
                .ToList();
            if (enabled.Count == 0)
            {
                modelMenu.DropDownItems.Add(L10n.T("L10n_Tray_NoModel"));
                return;
            }

            // Seed the shared callability state from the stored results before reading any colour
            // or (F/N/S/U) tag. A fresh launch starts with an empty in-memory cache and the
            // background ping skips results that are still fresh, so without this every entry
            // would read "not checked yet" for up to the 6h TTL.
            status.PopulateFromStoreAsync(modelStore, active.Id, enabled).GetAwaiter().GetResult();

            // Same rule as the Codex catalog the gateway serves: only models that answered a real
            // Codex-shaped request are offered here, so picking one from the tray cannot land on a
            // model Codex will fail on. The 5-minute readiness tick adds a model back the moment it
            // starts working.
            var readinessStore = _host.Services.GetRequiredService<ICodexReadinessStore>();
            var verdicts = readinessStore.GetAllAsync(active.Id).GetAwaiter().GetResult();
            var publishable = CodexCatalogPolicy
                .SelectPublished(enabled.Select(m => m.RemoteId), verdicts, DateTimeOffset.UtcNow)
                .ToHashSet(StringComparer.Ordinal);
            enabled = enabled.Where(m => publishable.Contains(m.RemoteId)).ToList();
            status.ApplyReadiness(new CodexReadinessTick(
                active.Id,
                active.Name,
                DateTimeOffset.UtcNow,
                verdicts,
                Array.Empty<string>()));

            // Mark the model Codex is currently using. Codex's own session state wins: switching
            // the model inside Codex's picker never writes ~/.codex/config.toml, so the config
            // value (what the last hub activation wrote) is only the fallback.
            var sessionModelId = status.CodexSession?.ModelId;
            var currentModelId = sessionModelId
                ?? configService.GetCurrentModelAsync().GetAwaiter().GetResult();

            var header = new WinForms.ToolStripMenuItem(L10n.F("L10n_Tray_ProviderHeader", active.Name))
            {
                Enabled = false
            };
            modelMenu.DropDownItems.Add(header);

            // Second header line: the model Codex really runs right now, so the tray answers
            // "which model am I on?" even when that model is not in the list below (a model that
            // fails the Codex-shaped probe is filtered out of the list but is still in use).
            // It carries the same callability colour as the entries below (green = the provider
            // answers, amber = slow, grey = not probed yet, red = skipped), so one glance says
            // whether the model Codex is running is healthy. Enabled stays true because WinForms
            // paints a disabled item grey whatever ForeColor says; there is no Click handler, so
            // the line is still read-only.
            modelMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem(
                TrayModelLabel(status, active.Id, sessionModelId, status.CodexSessionText))
            {
                ForeColor = TrayCallabilityColor(status.GetCallability(active.Id, sessionModelId)),
                ToolTipText = sessionModelId is null
                    ? L10n.T("L10n_Codex_NoSessionModel")
                    : status.DescribeTooltip(active.Id, sessionModelId)
            });

            // Same selection order as the in-app pickers (callable → slow → unknown → skip, then
            // alphabetical); Skip entries stay hidden unless "show all" is on.
            var visible = CallabilityVisuals
                .OrderForSelection(enabled)
                .Where(m => CallabilityFilter.IsVisible(
                    status.GetCallability(active.Id, m.RemoteId),
                    status.ShowAllModels))
                .ToList();
            if (visible.Count == 0)
            {
                // Everything is currently marked Skip; the switches below can reveal them again.
                modelMenu.DropDownItems.Add(L10n.T("L10n_Tray_NoModel"));
            }

            foreach (var model in visible)
            {
                var providerId = active.Id;
                var modelId = model.RemoteId;
                var callability = status.GetCallability(providerId, modelId);
                var modelItem = new WinForms.ToolStripMenuItem(
                    TrayModelLabel(status, providerId, modelId, model.DisplayName))
                {
                    Checked = string.Equals(model.RemoteId, currentModelId, StringComparison.Ordinal),
                    CheckOnClick = false,
                    ForeColor = TrayCallabilityColor(callability),
                    ToolTipText = status.DescribeTooltip(providerId, modelId)
                };
                modelItem.Click += (_, _) => ActivateTrayModelAsync(activation, providerId, modelId, window);
                modelMenu.DropDownItems.Add(modelItem);
            }

            modelMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());

            var lastChecked = new WinForms.ToolStripMenuItem(status.LastRefreshedText)
            {
                Enabled = false
            };
            modelMenu.DropDownItems.Add(lastChecked);

            // Legend for the one-letter tags above (F/N/S/U/X). Shown here rather than in the
            // icon tooltip because WinForms caps NotifyIcon.Text at 63 characters.
            modelMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem(L10n.T("L10n_Tray_StatusLegend"))
            {
                Enabled = false
            });

            var pauseItem = new WinForms.ToolStripMenuItem(L10n.T("L10n_Tray_PauseAutoPing"))
            {
                Checked = autoPing.IsPaused,
                CheckOnClick = true,
                ToolTipText = L10n.T("L10n_Tray_PauseAutoPingTip")
            };
            pauseItem.CheckedChanged += (_, _) =>
            {
                autoPing.IsPaused = pauseItem.Checked;
                // The Codex-readiness probe talks to the provider too, so one switch pauses both.
                if (_host is not null)
                {
                    try
                    {
                        _host.Services.GetRequiredService<CodexReadinessPingService>().IsPaused = pauseItem.Checked;
                    }
                    catch (Exception ex)
                    {
                        LogStartup("Codex readiness pause toggle failed", ex);
                    }
                }
            };
            modelMenu.DropDownItems.Add(pauseItem);

            var showAllItem = new WinForms.ToolStripMenuItem(L10n.T("L10n_Tray_ShowAllModels"))
            {
                Checked = status.ShowAllModels,
                CheckOnClick = true,
                ToolTipText = L10n.T("L10n_Tray_ShowAllModelsTip")
            };
            showAllItem.CheckedChanged += (_, _) => status.ShowAllModels = showAllItem.Checked;
            modelMenu.DropDownItems.Add(showAllItem);
        }
        catch (Exception ex)
        {
            LogStartup("Tray model menu build failed", ex);
            modelMenu.DropDownItems.Add(L10n.T("L10n_Tray_NoModel"));
        }
    }

    /// <summary>Activate the chosen provider + model straight from the tray, then surface the window.</summary>
    private async void ActivateTrayModelAsync(
        IProviderActivationService activation, string providerId, string modelId, Window window)
    {
        try
        {
            await activation.ActivateDesktopAsync(providerId, modelId);
            // The hub just decided the model: show that until Codex itself reports it back, so the
            // watcher cannot flip the UI to the (still stale) session row in the next few seconds.
            _sessionWatch?.NoteHubActivation(modelId);
            await QueueModelSwitchToOpenSessionAsync(modelId);

            MainViewModel? main = null;
            if (_host is not null)
            {
                try { main = _host.Services.GetRequiredService<MainViewModel>(); }
                catch (Exception ex) { LogStartup("Tray model UI refresh resolve failed", ex); }
            }

            // Sync the UI to the tray-side switch: reload the Home/provider view models so the
            // per-card dropdown + Home selector show the model just written to config.
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ShowMainWindow(window));
            if (main is not null)
            {
                try { await main.InitializeAsync(); }
                catch (Exception ex) { LogStartup("Tray model UI refresh failed", ex); }
            }
        }
        catch (Exception ex)
        {
            LogStartup("Tray model activation failed", ex);
        }
    }

    /// <summary>
    /// Pushes the model just chosen in the tray into the newest Codex session, so that session's
    /// next turn runs the new model instead of waiting for a fresh chat.
    /// </summary>
    private async Task QueueModelSwitchToOpenSessionAsync(string modelId)
    {
        try
        {
            var queue = new AdamCodexHub.Codex.CodexSessionQueue();
            var notice = string.Format(L10n.T("L10n_Tray_QueueNotice"), modelId);
            var outcome = await queue.QueueMessageAsync(modelId, notice);
            LogStartup($"Tray model queue queued={outcome.Queued} thread={outcome.ThreadId ?? "-"} detail={outcome.Detail}");

            if (outcome.Queued && _trayIcon is not null)
            {
                _trayIcon.BalloonTipTitle = L10n.T("L10n_Tray_NotificationTitle");
                _trayIcon.BalloonTipText = string.Format(L10n.T("L10n_Tray_QueueSent"), modelId);
                _trayIcon.ShowBalloonTip(15000);
            }
        }
        catch (Exception ex)
        {
            LogStartup("Tray model queue failed", ex);
        }
    }

    /// <summary>
    /// The model Codex is really running changed (read from Codex's own state, or a pending
    /// hub-side switch) — mirror it into the hub UI. Raised on the watcher's timer thread.
    /// </summary>
    private void OnCodexSessionModelChanged(object? sender, CodexSessionModel? session) =>
        Dispatcher.InvokeAsync(() => ApplyCodexSession(session));

    /// <summary>
    /// Publishes the live model to <see cref="ModelStatusState"/> (tray header + tick), the tray
    /// tooltip and the Home card dropdown (via <see cref="MainViewModel.ApplyCodexSession"/>).
    /// </summary>
    private void ApplyCodexSession(CodexSessionModel? session)
    {
        try
        {
            ModelStatusState.Current.ApplyCodexSession(session);
            LogStartup(
                $"Codex session model: {session?.ModelId ?? "-"} " +
                $"(thread {session?.ThreadId ?? "-"}, source {session?.Source ?? "-"})");

            if (_trayIcon is not null)
            {
                // WinForms NotifyIcon.Text is capped at 63 characters.
                var tooltip = $"Adam CodexHub · {ModelStatusState.Current.CodexSessionText}";
                _trayIcon.Text = tooltip.Length > 63 ? string.Concat(tooltip.AsSpan(0, 60), "...") : tooltip;
            }
        }
        catch (Exception ex)
        {
            LogStartup("Codex session model publish failed", ex);
        }

        if (_host is null)
        {
            return;
        }

        try
        {
            _host.Services.GetRequiredService<MainViewModel>().ApplyCodexSession(session);
        }
        catch (Exception ex)
        {
            LogStartup("Codex session model UI sync failed", ex);
        }
    }

    private void ExitApplication()
    {
        IsRealExit = true;
        DisposeTrayIcon();
        Shutdown();
    }

    private void DisposeTrayIcon()
    {
        var icon = _trayIcon;
        _trayIcon = null;
        if (icon is not null)
        {
            icon.Visible = false;
            icon.Dispose();
        }

        _trayIconImage?.Dispose();
        _trayIconImage = null;
    }

    public void NotifyCodexLaunched()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipTitle = L10n.T("L10n_Tray_NotificationTitle");
            _trayIcon.BalloonTipText = L10n.T("L10n_Tray_CodexLaunched");
            _trayIcon.ShowBalloonTip(15000);
        }
    }

    private static void ShowMainWindow(Window window)
    {
        window.ShowInTaskbar = true;
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var packUri = new Uri(
            "pack://application:,,,/AdamCodexHub.App;component/Assets/adam-codexhub-logo.png",
            UriKind.Absolute);
        using var stream = Application.GetResourceStream(packUri)?.Stream
            ?? throw new InvalidOperationException("Bundled app logo resource was not found.");

        // Scale the (usually large) logo down so the small tray icon stays crisp, then convert
        // the bitmap handle into a standalone icon (the handle is destroyed after cloning).
        using var source = new Drawing.Bitmap(stream);
        using var scaled = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.Clear(Drawing.Color.Transparent);
            graphics.DrawImage(source, 0, 0, 32, 32);
        }

        var hIcon = scaled.GetHicon();
        try
        {
            using var fromHandle = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static async Task ShutdownHostAsync(IHost host)
    {
        try
        {
            var shutdown = host.Services.GetRequiredService<ProviderShutdownService>();
            var status = await shutdown.RestoreAccountAsync();
            LogStartup($"Shutdown account restore: {status}");
        }
        catch (Exception ex)
        {
            LogStartup("Shutdown account restore failed", ex);
        }

        try
        {
            var gateway = host.Services.GetRequiredService<IGatewayService>();
            await gateway.StopAsync();
        }
        catch (Exception ex)
        {
            LogStartup("Gateway shutdown failed", ex);
        }

        try
        {
            await host.StopAsync();
        }
        catch (Exception ex)
        {
            LogStartup("Host shutdown failed", ex);
        }
    }

    private static void LogProviderStartupWarnings(IHost? host)
    {
        if (host is null)
        {
            return;
        }

        try
        {
            var providers = host.Services.GetRequiredService<IProviderManager>();
            foreach (var warning in providers.StartupWarnings)
            {
                LogStartup($"Provider startup warning: {warning}");
            }
        }
        catch (Exception ex)
        {
            LogStartup("Provider startup warning check failed", ex);
        }
    }

    private static void LogStartup(string stage, Exception? exception = null)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AdamCodexHub",
                "logs");
            Directory.CreateDirectory(directory);
            var detail = exception is null
                ? string.Empty
                : $" | {exception.GetType().Name}: {exception.Message}";
            File.AppendAllText(
                Path.Combine(directory, "startup.log"),
                $"{DateTimeOffset.Now:O} | {stage}{detail}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
