using System.Windows;
using AdamCodexHub.App.ViewModels;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.App;

public partial class ModelTestWindow : Window
{
    private readonly ICompatibilityService _compatibility;
    private readonly string _providerId;
    private readonly string _modelId;
    private readonly ModelTestViewModel _viewModel;

    public ModelTestWindow(
        ICompatibilityService compatibility,
        string providerName,
        string providerId,
        string modelName,
        string modelId)
    {
        InitializeComponent();
        _compatibility = compatibility;
        _providerId = providerId;
        _modelId = modelId;

        _viewModel = new ModelTestViewModel(providerName, modelName)
        {
            IsRunning = true
        };
        DataContext = _viewModel;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var progress = new Progress<ModelTestProgress>(_viewModel.Apply);
            var result = await _compatibility.TestAsync(_providerId, _modelId, progress);
            _viewModel.Complete(result);
        }
        catch (Exception ex)
        {
            _viewModel.IsRunning = false;
            _viewModel.Summary = Services.L10n.T("L10n_MT_TestFailed");
            _viewModel.Notes = ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning)
        {
            return;
        }

        Close();
    }
}
