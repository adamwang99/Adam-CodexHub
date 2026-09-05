using System.IO;
using System.Runtime.InteropServices;
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
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AdamCodexHub.App;

public partial class App : Application
{
    private const int RequiredSessionAcknowledgementVersion = 2;
    private IHost? _host;
    private WinForms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconImage;

    /// <summary>
    /// True once the user has chosen a real exit (tray "Exit" or OS session ending). Window
    /// closing is intercepted otherwise, so the app hides to the system tray and the in-process
    /// gateway keeps running.
    /// </summary>
    public static bool IsRealExit { get; set; }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

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

            // ShutdownMode stays OnExplicitShutdown (declared in App.xaml): closing the window
            // only hides it to the tray while the in-process gateway keeps serving Codex.
            InitializeTrayIcon(window);
            LogStartup("Tray icon initialized");
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

        DisposeTrayIcon();

        if (host is not null)
        {
            Task.Run(() => ShutdownHostAsync(host)).GetAwaiter().GetResult();
            host.Dispose();
        }

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
        var showItem = new WinForms.ToolStripMenuItem("Show Adam CodexHub");
        showItem.Click += (_, _) => ShowMainWindow(window);
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(showItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIconImage = LoadTrayIcon();
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "Adam CodexHub",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow(window);
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

    private static void ShowMainWindow(Window window)
    {
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
