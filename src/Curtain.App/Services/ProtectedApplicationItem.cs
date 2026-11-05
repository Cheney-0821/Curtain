using System.ComponentModel;
using Curtain.App.Localization;

namespace Curtain.App.Services;

public sealed class ProtectedApplicationItem : INotifyPropertyChanged
{
    internal string Key { get; }
    internal ProtectedWindowItem[] Members { get; private set; } = [];
    public Curtain.Core.Settings.WindowProtectionSettings ProtectionOptions => Members[0].ProtectionOptions;
    public bool UsesGlobal => Members[0].UsesGlobal;
    public string Title => Members[0].Title;
    public string State => LocalizationService.Get("ApplicationWindowState", Members.Length,
        Members.Count(item => item.Session.Target.IsClosed));
    public event PropertyChangedEventHandler? PropertyChanged;

    internal ProtectedApplicationItem(string key, ProtectedWindowItem[] members)
    {
        Key = key;
        Update(members);
    }

    internal void Update(ProtectedWindowItem[] members)
    {
        foreach (var item in Members) item.PropertyChanged -= OnMemberChanged;
        Members = members;
        foreach (var item in Members) item.PropertyChanged += OnMemberChanged;
        PropertyChanged?.Invoke(this, new(nameof(ProtectionOptions)));
        PropertyChanged?.Invoke(this, new(nameof(UsesGlobal)));
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
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
