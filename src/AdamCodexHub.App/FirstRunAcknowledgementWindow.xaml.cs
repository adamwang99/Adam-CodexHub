using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using AdamCodexHub.App.Services;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.App;

public partial class FirstRunAcknowledgementWindow : Window
{
    private const int AcknowledgementVersion = 2;
    private readonly IAppSettingsService _settings;
    private bool _accepted;

    public FirstRunAcknowledgementWindow(IAppSettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        L10n.LanguageChanged += UpdateLinkCaptions;
        UpdateLinkCaptions();
    }

    /// <summary>Hyperlink captions are plain inlines, so they are re-set from L10n keys on demand.</summary>
    private void UpdateLinkCaptions()
    {
        SetCaption(PrivacyLink, "L10n_FR_LinkPrivacy");
        SetCaption(DisclaimerLink, "L10n_FR_LinkDisclaimer");
        SetCaption(DisclosuresLink, "L10n_FR_LinkDisclosures");
    }

    private static void SetCaption(Hyperlink link, string key)
    {
        link.Inlines.Clear();
        link.Inlines.Add(new Run(L10n.T(key)));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_accepted)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void AcknowledgementChanged(object sender, RoutedEventArgs e)
    {
        ContinueButton.IsEnabled = SessionAcknowledgementCheckBox.IsChecked == true &&
                                   DataAcknowledgementCheckBox.IsChecked == true &&
                                   ResponsibilityAcknowledgementCheckBox.IsChecked == true;
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!ContinueButton.IsEnabled)
        {
            return;
        }

        ContinueButton.IsEnabled = false;
        await _settings.AcknowledgeSessionMechanismAsync(AcknowledgementVersion);

        _accepted = true;
        DialogResult = true;
        Close();
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                Services.L10n.T("L10n_FR_OpenLinkError"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        e.Handled = true;
    }
}
