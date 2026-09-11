using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using AdamCodexHub.App.Mvvm;
using AdamCodexHub.Core.Maintenance;

namespace AdamCodexHub.App.Services;

/// <summary>
/// The update notice, shared by the two places that show it: the banner on the main screen and the
/// card in Settings. Deliberately the same shape as <see cref="HandoffState"/> — one source of truth,
/// so the two views can never disagree about whether a newer release exists.
///
/// Adam asked for exactly this on 2026-09-11: *"khi có bản cập nhật mới cần có báo noti ở giao diện
/// chính để người dùng nhấn và đưa vào setting, chứ không cần người dùng phải vào check mới biết."*
/// So the check now runs by itself once per launch and the result announces itself on the main
/// screen; clicking the banner goes to Settings, where the download lives.
///
/// Two things it must never do: block the UI (the check is fire-and-forget and the window is already
/// drawing), and interrupt with a failure. A launch without a network simply has no notice — the
/// manual button in Settings is still there for someone who wants to know why.
/// </summary>
public sealed class UpdateState : INotifyPropertyChanged
{
    /// <summary>App-wide instance both views bind to.</summary>
    public static UpdateState Current { get; } = new();

    private bool _checking;
    private string? _latestVersion;
    private string? _releaseUrl;
    private ReleaseCheck.SetupDownload? _setup;
    private ReleaseCheck.SetupDownload? _updatePackage;
    private string _statusText = string.Empty;
    private bool _dismissed;

    private UpdateState()
    {
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(manual: true));
        DismissCommand = new RelayCommand(() => Dismissed = true);
        ShowSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the notice is clicked; the shell turns it into a page switch.</summary>
    public event Action? SettingsRequested;

    /// <summary>Asks GitHub whether a newer release exists. Never throws, never blocks.</summary>
    public ICommand CheckCommand { get; }

    /// <summary>Hides the banner for this session. It returns on the next launch.</summary>
    public ICommand DismissCommand { get; }

    /// <summary>Opens the Settings page, where the download button lives.</summary>
    public ICommand ShowSettingsCommand { get; }

    /// <summary>The informational version of the running assembly (e.g. "1.5.5+e073576…").</summary>
    public static string RunningVersion =>
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The newer version, or null when the running build is current.</summary>
    public string? LatestVersion => _latestVersion;

    /// <summary>The installer to offer, when the release carries one.</summary>
    public ReleaseCheck.SetupDownload? Setup => _setup;

    /// <summary>
    /// The small update package for this architecture, when the release carries one. Preferred over the
    /// installer when present: it is a few megabytes against ninety-odd, and it is applied in place
    /// rather than run as a new installation.
    /// </summary>
    public ReleaseCheck.SetupDownload? UpdatePackage => _updatePackage;

    /// <summary>Only offered when the release actually carries a small update package.</summary>
    public Visibility InstallVisibility =>
        _updatePackage is null ? Visibility.Collapsed : Visibility.Visible;

    public string? ReleaseUrl => _releaseUrl;

    /// <summary>True while a newer release is known and the user has not dismissed the notice.</summary>
    public bool IsUpdateAvailable => _latestVersion is not null && !_dismissed;

    public Visibility BannerVisibility =>
        IsUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReleaseLinkVisibility =>
        string.IsNullOrWhiteSpace(_releaseUrl) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DownloadVisibility =>
        _setup is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What the banner says; localized, and re-localized on a language switch.</summary>
    public string BannerText =>
        _latestVersion is null ? string.Empty : L10n.F("L10n_Update_Banner", _latestVersion);

    /// <summary>The Settings card's status line — the only place a failed check is reported.</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            Raise(nameof(StatusText));
        }
    }

    private bool Dismissed
    {
        get => _dismissed;
        set
        {
            if (_dismissed == value)
            {
                return;
            }

            _dismissed = value;
            Raise(nameof(IsUpdateAvailable));
            Raise(nameof(BannerVisibility));
        }
    }

    /// <summary>
    /// The background check, run once when the shell is built. Failures are swallowed on purpose:
    /// a hub that throws a network error at launch over an update nobody asked for is worse than a
    /// hub that simply says nothing.
    /// </summary>
    public async Task CheckQuietlyAsync()
    {
        await CheckAsync(manual: false);
    }

    /// <summary>
    /// One request to the public releases API. <paramref name="manual"/> decides whether the outcome
    /// is written to the status line — the button in Settings has to answer, the startup check does
    /// not.
    /// </summary>
    public async Task CheckAsync(bool manual)
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        if (manual)
        {
            StatusText = L10n.T("L10n_Set_UpdateChecking");
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // GitHub rejects requests without a User-Agent; the header names the app, nothing else.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AdamCodexHub");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var payload = await client.GetStringAsync(ReleaseCheck.LatestReleaseUrl);
            var result = ReleaseCheck.Evaluate(RunningVersion, payload);

            if (!result.Succeeded)
            {
                ApplyNothing();
                if (manual)
                {
                    StatusText = L10n.F("L10n_Set_UpdateFailed", result.Failure ?? string.Empty);
                }
            }
            else if (result.IsNewer)
            {
                _latestVersion = result.LatestVersion;
                _releaseUrl = result.ReleaseUrl;
                // Only a release that actually carries an installer offers the download button.
                _setup = ReleaseCheck.FindSetupDownload(payload);
                // And one that carries a small update package offers the in-place update instead.
                _updatePackage = ReleaseCheck.FindUpdatePackage(payload);
                // A newer release than the one dismissed is worth mentioning again.
                _dismissed = false;
                if (manual)
                {
                    StatusText = L10n.F("L10n_Set_UpdateAvailable", result.LatestVersion ?? string.Empty);
                }
            }
            else
            {
                ApplyNothing();
                if (manual)
                {
                    StatusText = L10n.T("L10n_Set_UpdateCurrent");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            ApplyNothing();
            if (manual)
            {
                StatusText = L10n.F("L10n_Set_UpdateFailed", ex.Message);
            }
        }
        finally
        {
            _checking = false;
        }

        Raise(nameof(LatestVersion));
        Raise(nameof(IsUpdateAvailable));
        Raise(nameof(BannerText));
        Raise(nameof(BannerVisibility));
        Raise(nameof(Setup));
        Raise(nameof(ReleaseUrl));
        Raise(nameof(ReleaseLinkVisibility));
        Raise(nameof(DownloadVisibility));
        Raise(nameof(InstallVisibility));
    }

    /// <summary>Drops the notice — used for every outcome that is not "a newer release exists".</summary>
    private void ApplyNothing()
    {
        _latestVersion = null;
        _releaseUrl = null;
        _setup = null;
        _updatePackage = null;
    }

    /// <summary>Re-localizes the banner text after a language switch.</summary>
    internal void RefreshLanguage() => Raise(nameof(BannerText));

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
