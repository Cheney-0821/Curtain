using System.ComponentModel;

namespace Curtain.App.Services;

public sealed class ProtectedWindowItem : INotifyPropertyChanged
{
    internal CurtainSession Session { get; }
    public string Title { get; private set; }
    public string State { get; private set; } = "保护中";
    public string ActionLabel { get; private set; } = "暂停保护";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal ProtectedWindowItem(CurtainSession session, string title)
    { Session = session; Title = title; }
    internal void Update(string title, bool closed, bool suspended)
    {
        string state = (suspended ? "窗口不可用 · " : "") + (closed ? "保护中" : "已暂停");
        string action = closed ? "暂停保护" : "启用保护";
        if (Title != title) { Title = title; PropertyChanged?.Invoke(this, new(nameof(Title))); }
        if (State != state) { State = state; PropertyChanged?.Invoke(this, new(nameof(State))); }
        if (ActionLabel != action) { ActionLabel = action; PropertyChanged?.Invoke(this, new(nameof(ActionLabel))); }
    }
}
