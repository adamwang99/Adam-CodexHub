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
}
