using System.Windows;

namespace AdamCodexHub.App;

public partial class GuideWindow : Window
{
    public GuideWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
