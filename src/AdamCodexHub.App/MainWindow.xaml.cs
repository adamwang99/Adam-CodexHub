using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdamCodexHub.App.ViewModels;

namespace AdamCodexHub.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public MainViewModel ViewModel { get; }

    private void OnProviderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox ||
            listBox.DataContext is not HomeViewModel home ||
            home.SelectedCard is not { } card)
        {
            return;
        }

        e.Handled = true;
        home.DoubleClickCommand.Execute(card);
    }
}
