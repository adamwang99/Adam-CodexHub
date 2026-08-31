using System.Windows;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.App;

public partial class FirstRunAcknowledgementWindow : Window
{
    private const int AcknowledgementVersion = 1;
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
        ContinueButton.IsEnabled = AcknowledgementCheckBox.IsChecked == true;
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (AcknowledgementCheckBox.IsChecked != true)
        {
            return;
        }

        ContinueButton.IsEnabled = false;
        await _settings.AcknowledgeSessionMechanismAsync(AcknowledgementVersion);

        _accepted = true;
        DialogResult = true;
        Close();
    }
}
