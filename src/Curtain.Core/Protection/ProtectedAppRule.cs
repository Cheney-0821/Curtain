using System.Text.Json.Serialization;
using Curtain.Core.Settings;

namespace Curtain.Core.Protection;

/// <summary>
/// Stable application identity. Runtime HWND/PID/thread IDs deliberately do not belong here.
/// Unpackaged desktop applications are matched by normalized executable path. Packaged
/// applications can additionally be matched by AUMID or package family name.
/// </summary>
public sealed record ProtectedAppIdentity
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string? PackageFamilyName { get; init; }
    public string? ApplicationUserModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Publisher { get; init; }

    [JsonIgnore]
    public string StableKey =>
        !string.IsNullOrWhiteSpace(ApplicationUserModelId)
            ? "aumid:" + ApplicationUserModelId.Trim().ToUpperInvariant()
            : !string.IsNullOrWhiteSpace(PackageFamilyName)
                ? "package:" + PackageFamilyName.Trim().ToUpperInvariant()
                : !string.IsNullOrWhiteSpace(ExecutablePath)
                    ? "path:" + NormalizeExecutablePath(ExecutablePath)
                    : string.Empty;

    public ProtectedAppIdentity Normalize()
    {
        string path = NormalizeExecutablePath(ExecutablePath);
        return this with
        {
            ExecutablePath = path,
            PackageFamilyName = NormalizeOptional(PackageFamilyName, 256),
            ApplicationUserModelId = NormalizeOptional(ApplicationUserModelId, 512),
            DisplayName = NormalizeDisplayName(DisplayName),
            Publisher = NormalizeOptional(Publisher, 256)
        };
    }

    public bool IsValid =>
        (!string.IsNullOrWhiteSpace(ExecutablePath) && ExecutablePath.Length <= 32767) ||
        !string.IsNullOrWhiteSpace(PackageFamilyName) ||
        !string.IsNullOrWhiteSpace(ApplicationUserModelId);

    public static string NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string candidate = value.Trim().Trim('"');
        try
        {
            candidate = Path.GetFullPath(candidate);
            return Path.TrimEndingDirectorySeparator(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return candidate;
        }
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown application";
        string normalized = value.Trim();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }
}

/// <summary>
/// A user-owned application rule. One rule can bind to multiple top-level windows
/// created by the same application. The rule never stores a live HWND or PID.
/// </summary>
public sealed record ProtectedAppRule
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RuleId { get; init; } = Guid.NewGuid();
    public ProtectedAppIdentity Identity { get; init; } = new();
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    /// <summary>Null means follow the current global protection defaults.</summary>
    public WindowProtectionSettings? CustomProtection { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string StableKey => Identity.StableKey;

    public ProtectedAppRule Normalize()
    {
        var identity = (Identity ?? new ProtectedAppIdentity()).Normalize();
        string displayName = string.IsNullOrWhiteSpace(DisplayName)
            ? identity.DisplayName
            : DisplayName.Trim();
        if (displayName.Length > 128) displayName = displayName[..128];

        return this with
        {
            SchemaVersion = 1,
            RuleId = RuleId == Guid.Empty ? Guid.NewGuid() : RuleId,
            Identity = identity,
            DisplayName = displayName,
            CustomProtection = CustomProtection is null ? null : WindowBlurScale.Normalize(CustomProtection),
            CreatedAtUtc = CreatedAtUtc == default ? DateTimeOffset.UtcNow : CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc == default ? DateTimeOffset.UtcNow : UpdatedAtUtc
        };
    }

    public bool IsValid => SchemaVersion == 1 && RuleId != Guid.Empty &&
        Identity is not null && Identity.IsValid && !string.IsNullOrWhiteSpace(StableKey);
}

public sealed record ProtectedAppRulesDocument
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<ProtectedAppRule> Rules { get; init; } = new();
}

public sealed record ProtectedAppRulesLoadResult(
    IReadOnlyList<ProtectedAppRule> Rules,
    bool RecoveredFromBackup,
    bool RecoveredFromInvalidEntries,
    string? RecoveryCode = null);
