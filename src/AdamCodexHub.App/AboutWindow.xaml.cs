using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace AdamCodexHub.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        var guide = new GuideWindow { Owner = this };
        guide.ShowDialog();
    }

    /// <summary>Opens the button's Tag URL in the default browser (real adamwang99 links only).</summary>
    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                Services.L10n.T("L10n_About_OpenTip"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
