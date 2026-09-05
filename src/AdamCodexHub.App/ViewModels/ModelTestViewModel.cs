using System.Collections.ObjectModel;
using AdamCodexHub.App.Mvvm;
using AdamCodexHub.App.Services;
using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.App.ViewModels;

/// <summary>
/// One live row in the model-test window. Shows a single probe stage and its
/// running / passed / failed state, with the detail (usually the HTTP error)
/// when a probe fails.
/// </summary>
public sealed class ProbeStepRow : ObservableObject
{
    private ModelTestStepStatus _status;
    private string? _detail;

    public string Stage { get; }

    public ProbeStepRow(string stage)
    {
        Stage = stage;
    }

    public ModelTestStepStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string? Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsRunning => Status == ModelTestStepStatus.Running;
    public bool Passed => Status == ModelTestStepStatus.Passed;
    public bool Failed => Status == ModelTestStepStatus.Failed;

    public void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Passed));
        OnPropertyChanged(nameof(Failed));
    }
}

public sealed class ModelTestViewModel : ObservableObject
{
    private bool _isRunning;
    private int _score;
    private string _summary = string.Empty;
    private string _notes = string.Empty;

    public ModelTestViewModel(string providerName, string modelName)
    {
        ProviderName = providerName;
        ModelName = modelName;
    }

    public string ProviderName { get; }
    public string ModelName { get; }

    public ObservableCollection<ProbeStepRow> Steps { get; } = new();

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public int Score
    {
        get => _score;
        set
        {
            if (SetProperty(ref _score, value))
            {
                OnPropertyChanged(nameof(ScoreText));
            }
        }
    }

    public string ScoreText => $"{Score}/100";

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    /// <summary>
    /// Adds a stage row if it is new, otherwise updates the existing row and
    /// raises change notifications so the window repaints live.
    /// </summary>
    public void Apply(ModelTestProgress progress)
    {
        var row = Steps.FirstOrDefault(x => x.Stage == progress.Stage);
        if (row is null)
        {
            row = new ProbeStepRow(progress.Stage)
            {
                Status = progress.Status,
                Detail = progress.Detail
            };
            Steps.Add(row);
        }
        else
        {
            row.Status = progress.Status;
            row.Detail = progress.Detail;
            row.NotifyStatusChanged();
        }
    }

    public void Complete(CompatibilityResult result)
    {
        IsRunning = false;
        Score = result.Score;
        Summary = result.Text
            ? L10n.F("L10n_MT_CompatOk", result.Score)
            : L10n.F("L10n_MT_CompatFailed", result.Score);

        var flags = new List<string>();
        if (result.Responses) flags.Add("Responses");
        if (result.ChatCompletions) flags.Add("Chat");
        if (result.Streaming) flags.Add("Streaming");
        if (result.ToolCalling) flags.Add("Tools");
        if (result.StructuredJson) flags.Add("Structured JSON");
        if (result.Vision) flags.Add("Vision");
        var supported = L10n.F(
            "L10n_MT_SupportedPrefix",
            flags.Count == 0 ? L10n.T("L10n_MT_None") : string.Join(", ", flags));
        Notes = result.Notes is null
            ? supported
            : $"{result.Notes}\n{supported}";
    }
}
