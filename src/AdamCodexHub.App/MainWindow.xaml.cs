using System.Windows;
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
}
