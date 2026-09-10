using System.ComponentModel;
using AdamCodexHub.Codex;

namespace AdamCodexHub.App.Services;

/// <summary>
/// The session hand-off switches, shared by every view that shows them: the Home toolbar
/// switch and the two checkboxes on the Settings tab all bind to this one observable object,
/// so flipping either place updates the other immediately and writes the preference file.
/// Keeping a single source of truth (instead of one copy per view model) is what guarantees
/// the two screens can never drift apart.
/// </summary>
public sealed class HandoffState : INotifyPropertyChanged
{
    /// <summary>App-wide instance the views bind to.</summary>
    public static HandoffState Current { get; } = new();

    private bool _openFreshChat;
    private bool _autoSubmitRecap;

    private HandoffState()
    {
        // Page VMs are app-lifetime singletons, so these subscriptions live for the whole app.
        var preferences = CodexHandoffPreferences.Load();
        _openFreshChat = preferences.OpenFreshChat;
        _autoSubmitRecap = preferences.AutoSubmit;
        L10n.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Open a fresh Codex chat and carry the session over when a provider is activated.</summary>
    public bool OpenFreshChat
    {
        get => _openFreshChat;
        set
        {
            if (_openFreshChat == value)
            {
                return;
            }

            _openFreshChat = value;
            Raise(nameof(OpenFreshChat));
            Raise(nameof(CanAutoSubmitRecap));
            Raise(nameof(OpenFreshChatTooltip));
            Persist();
        }
    }

    /// <summary>Submit the recap instead of leaving it in the composer for Enter.</summary>
    public bool AutoSubmitRecap
    {
        get => _autoSubmitRecap;
        set
        {
            if (_autoSubmitRecap == value)
            {
                return;
            }

            _autoSubmitRecap = value;
            Raise(nameof(AutoSubmitRecap));
            Persist();
        }
    }

    /// <summary>Without a fresh chat there is nothing to submit, so the second switch greys out.</summary>
    public bool CanAutoSubmitRecap => _openFreshChat;

    /// <summary>Hover text of the hand-off switch (shown by both the Home toolbar and Settings):
    /// spells out what the current state does, so the switch explains itself.</summary>
    public string OpenFreshChatTooltip => L10n.T(
        _openFreshChat ? "L10n_Set_HandoffOpenChatTipOn" : "L10n_Set_HandoffOpenChatTipOff");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLanguageChanged() => Raise(nameof(OpenFreshChatTooltip));

    private void Persist()
    {
        new CodexHandoffPreferences
        {
            OpenFreshChat = _openFreshChat,
            AutoSubmit = _autoSubmitRecap
        }.Save();
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
