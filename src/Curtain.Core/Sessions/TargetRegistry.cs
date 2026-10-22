namespace Curtain.Core.Sessions;

public readonly record struct WindowIdentity(long Handle, uint ProcessId, uint ThreadId,
    string ClassName, long ProcessStartTicks);

public sealed class ProtectedTarget
{
    public WindowIdentity Identity { get; }
    public bool IsClosed { get; private set; } = true;
    public bool IsSuspended { get; private set; }
    internal ProtectedTarget(WindowIdentity identity) => Identity = identity;
    public void SetClosed(bool closed) => IsClosed = closed;
    public void SetSuspended(bool suspended) => IsSuspended = suspended;
}

public sealed class TargetRegistry
{
    private readonly Dictionary<long, ProtectedTarget> _targets = new();
    public IReadOnlyCollection<ProtectedTarget> Targets => _targets.Values;
    public ProtectedTarget Attach(WindowIdentity identity)
    {
        if (identity.Handle == 0 || identity.ProcessId == 0 || string.IsNullOrEmpty(identity.ClassName))
            throw new ArgumentException("Invalid window identity");
        if (_targets.TryGetValue(identity.Handle, out var existing) && existing.Identity == identity)
            return existing;
        var target = new ProtectedTarget(identity);
        _targets[identity.Handle] = target;
        return target;
    }
    public bool Remove(ProtectedTarget target)
    {
        if (!_targets.TryGetValue(target.Identity.Handle, out var current) || !ReferenceEquals(current, target))
            return false;
        return _targets.Remove(target.Identity.Handle);
    }
    public void SetAllClosed(bool closed)
    { foreach (var target in _targets.Values) target.SetClosed(closed); }
}
