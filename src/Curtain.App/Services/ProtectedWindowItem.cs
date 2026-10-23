using System.ComponentModel;
using Curtain.App.Localization;

namespace Curtain.App.Services;

public sealed class ProtectedWindowItem : INotifyPropertyChanged
{
    internal CurtainSession Session { get; }
    public string Title { get; private set; }
    public string State { get; private set; } = string.Empty;
    public string ActionLabel { get; private set; } = string.Empty;
    private bool _closed = true;
    private bool _suspended;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal ProtectedWindowItem(CurtainSession session, string title)
    {
        Session = session; Title = title;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        RefreshLabels();
    }
    internal void Update(string title, bool closed, bool suspended)
    {
        _closed = closed;
        _suspended = suspended;
        if (Title != title) { Title = title; PropertyChanged?.Invoke(this, new(nameof(Title))); }
        RefreshLabels();
    }

    private void OnLanguageChanged() => RefreshLabels();

    private void RefreshLabels()
    {
        string state = (_suspended ? LocalizationService.Get("WindowUnavailable") + " · " : "") +
            LocalizationService.Get(_closed ? "Protected" : "Paused");
        string action = LocalizationService.Get(_closed ? "PauseProtection" : "EnableProtection");
        if (State != state) { State = state; PropertyChanged?.Invoke(this, new(nameof(State))); }
        if (ActionLabel != action) { ActionLabel = action; PropertyChanged?.Invoke(this, new(nameof(ActionLabel))); }
    }

    internal void Dispose() => LocalizationService.LanguageChanged -= OnLanguageChanged;
}
