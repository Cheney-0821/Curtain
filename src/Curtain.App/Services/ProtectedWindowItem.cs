using System.ComponentModel;
using Curtain.App.Localization;
using Curtain.Core.Settings;

namespace Curtain.App.Services;

public sealed class ProtectedWindowItem : INotifyPropertyChanged
{
    internal CurtainSession Session { get; }
    public string Title { get; private set; }
    public string State { get; private set; } = string.Empty;
    public string SettingsLabel { get; private set; } = string.Empty;
    public string ActionLabel { get; private set; } = string.Empty;
    public WindowProtectionSettings ProtectionOptions => Session.EffectiveProtection;
    public bool UsesGlobal => Session.CustomProtection is null;
    private WindowProtectionSettings? _lastOptions;
    private bool? _lastGlobal;
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
        if (_lastOptions != ProtectionOptions || _lastGlobal != UsesGlobal)
        {
            _lastOptions = ProtectionOptions; _lastGlobal = UsesGlobal;
            PropertyChanged?.Invoke(this, new(nameof(ProtectionOptions)));
            PropertyChanged?.Invoke(this, new(nameof(UsesGlobal)));
        }
        string state = (_suspended ? LocalizationService.Get("WindowUnavailable") + " · " : "") +
            LocalizationService.Get(_closed ? "Protected" : "Paused");
        string settings = LocalizationService.Get(Session.CustomProtection is null ? "WindowFollowsGlobal" : "WindowUsesCustom");
        if (SettingsLabel != settings) { SettingsLabel = settings; PropertyChanged?.Invoke(this, new(nameof(SettingsLabel))); }
        string action = LocalizationService.Get(_closed ? "PauseProtection" : "EnableProtection");
        if (State != state) { State = state; PropertyChanged?.Invoke(this, new(nameof(State))); }
        if (ActionLabel != action) { ActionLabel = action; PropertyChanged?.Invoke(this, new(nameof(ActionLabel))); }
    }

    internal void Dispose() => LocalizationService.LanguageChanged -= OnLanguageChanged;
}
