using System.ComponentModel;
using Curtain.App.Localization;
using Curtain.Core.Protection;
using Curtain.Core.Settings;

namespace Curtain.App.Services;

public sealed class ProtectedApplicationItem : INotifyPropertyChanged
{
    internal string Key { get; }
    internal ProtectedWindowItem[] Members { get; private set; } = [];
    private ProtectedAppRule? _rule;
    private WindowProtectionSettings _defaults = new();
    internal Guid? RuleId => _rule?.RuleId;
    public bool HasWindows => Members.Length != 0;
    public WindowProtectionSettings ProtectionOptions => HasWindows ? Members[0].ProtectionOptions
        : WindowBlurScale.Resolve(_defaults, _rule?.CustomProtection);
    public bool UsesGlobal => HasWindows ? Members[0].UsesGlobal : _rule?.CustomProtection is null;
    public string Title => _rule?.DisplayName ?? Members.FirstOrDefault()?.Title ?? string.Empty;
    public string State => HasWindows ? LocalizationService.Get("ApplicationWindowState", Members.Length,
        Members.Count(item => item.Session.Target.IsClosed)) : LocalizationService.Get("ApplicationNotRunning");
    public event PropertyChangedEventHandler? PropertyChanged;

    internal ProtectedApplicationItem(string key, ProtectedWindowItem[] members, ProtectedAppRule? rule,
        WindowProtectionSettings defaults)
    {
        Key = key;
        Update(members, rule, defaults);
    }

    internal void Update(ProtectedWindowItem[] members, ProtectedAppRule? rule, WindowProtectionSettings defaults)
    {
        foreach (var item in Members) item.PropertyChanged -= OnMemberChanged;
        Members = members;
        _rule = rule;
        _defaults = defaults;
        foreach (var item in Members) item.PropertyChanged += OnMemberChanged;
        PropertyChanged?.Invoke(this, new(nameof(ProtectionOptions)));
        PropertyChanged?.Invoke(this, new(nameof(UsesGlobal)));
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
        PropertyChanged?.Invoke(this, new(nameof(HasWindows)));
    }

    private void OnMemberChanged(object? sender, PropertyChangedEventArgs args)
    {
        PropertyChanged?.Invoke(this, new(nameof(ProtectionOptions)));
        PropertyChanged?.Invoke(this, new(nameof(UsesGlobal)));
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
    }

    internal void Dispose()
    {
        foreach (var item in Members) item.PropertyChanged -= OnMemberChanged;
    }
}
