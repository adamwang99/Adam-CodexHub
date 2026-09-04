using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
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
                "Could not open legal notice",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        e.Handled = true;
    }
}
