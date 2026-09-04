using System.IO;
using System.Windows;
using AdamCodexHub.App.ViewModels;
using AdamCodexHub.App.Services;
using AdamCodexHub.Codex;
using AdamCodexHub.Core.Interfaces;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdamCodexHub.App;

public partial class App : Application
{
    private const int RequiredSessionAcknowledgementVersion = 2;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LogStartup("OnStartup entered");

        try
        {
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

                services.AddSingleton<ICodexConfigService, CodexConfigService>();
                services.AddSingleton<IProjectStateService, FileProjectStateService>();
                services.AddSingleton<ISessionContinuityService, SessionContinuityService>();
                services.AddSingleton<IProviderActivationService, ProviderActivationService>();
                services.AddSingleton<ProviderShutdownService>();

                services.AddSingleton<IGatewayService, LocalGatewayService>();
                services.AddSingleton<IUserDialogService, UserDialogService>();

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

            var window = _host.Services.GetRequiredService<MainWindow>();
            LogStartup("Main window resolved");
            MainWindow = window;
            window.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            LogStartup("Main window shown");
            await window.ViewModel.InitializeAsync();
            LogStartup("Main view model initialized");
            LogProviderStartupWarnings(_host);
        }
        catch (Exception ex)
        {
            LogStartup("Startup failed", ex);
            MessageBox.Show(
                ex.Message,
                "Adam CodexHub could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var host = _host;
        _host = null;

        if (host is not null)
        {
            Task.Run(() => ShutdownHostAsync(host)).GetAwaiter().GetResult();
            host.Dispose();
        }

        base.OnExit(e);
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
