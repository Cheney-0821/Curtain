using System.ComponentModel;
using Curtain.Core.Protection;
using Curtain.App.Localization;

namespace Curtain.App.Services;

public sealed class ProtectedAppRuleListItem : INotifyPropertyChanged
{
    private string _stateCode;
    private int _activeWindowCount;

    internal ProtectedAppRule Rule { get; private set; }
    public Guid RuleId => Rule.RuleId;
    public string DisplayName => Rule.DisplayName;
    public bool Enabled => Rule.Enabled;
    public string State => LocalizationService.Get(_activeWindowCount > 0
        ? "RememberedAppActive" : "RememberedAppWaiting", _activeWindowCount);
    public event PropertyChangedEventHandler? PropertyChanged;

    public string StateCode
    {
        get => _stateCode;
        private set
        {
            if (_stateCode == value) return;
            _stateCode = value;
            PropertyChanged?.Invoke(this, new(nameof(StateCode)));
            PropertyChanged?.Invoke(this, new(nameof(State)));
        }
    }

    public int ActiveWindowCount
    {
        get => _activeWindowCount;
        private set
        {
            if (_activeWindowCount == value) return;
            _activeWindowCount = value;
            PropertyChanged?.Invoke(this, new(nameof(ActiveWindowCount)));
            PropertyChanged?.Invoke(this, new(nameof(State)));
        }
    }

    internal ProtectedAppRuleListItem(ProtectedAppRule rule, int activeWindowCount)
    {
        Rule = rule;
        _activeWindowCount = activeWindowCount;
        _stateCode = activeWindowCount > 0 ? "Active" : "WaitingForApplication";
    }

    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(State)));

    internal void Update(ProtectedAppRule rule, int activeWindowCount)
    {
        if (Rule != rule)
        {
            Rule = rule;
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        }
        ActiveWindowCount = activeWindowCount;
        StateCode = activeWindowCount > 0 ? "Active" : "WaitingForApplication";
    }
}
