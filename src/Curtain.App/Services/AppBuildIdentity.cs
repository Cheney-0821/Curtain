using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Curtain.App.Services;

internal sealed record AppBuildIdentity(string ProductVersion, string AssemblyVersion, string FileVersion,
    string InformationalVersion, string GitCommit, string BuildId, string BuildTimestampUtc,
    string WorkingTreeDirty)
{
    internal static AppBuildIdentity Current { get; } = Read(typeof(AppBuildIdentity).Assembly);

    internal static AppBuildIdentity Read(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(x => x.Key, x => x.Value ?? "Unknown");
        string Get(string key) => metadata.GetValueOrDefault(key, "Unknown");
        string info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
        return new(info.Split('+')[0], assembly.GetName().Version?.ToString() ?? "Unknown",
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "Unknown", info,
            Regex.IsMatch(Get("GitCommit"), "^[a-fA-F0-9]{40}$") ? Get("GitCommit") : "Unknown",
            Get("BuildId"), Get("BuildTimestampUtc"), NormalizeWorkingTreeDirty(Get("WorkingTreeDirty")));
    }

    internal static string NormalizeWorkingTreeDirty(string? value) =>
        bool.TryParse(value, out bool isDirty) ? isDirty.ToString().ToLowerInvariant() : "Unknown";

    internal static (string Attribution, string? ProductVersion) ResolveFileVersion(string? version)
    {
        using Stream stream = typeof(AppBuildIdentity).Assembly.GetManifestResourceStream("Curtain.ReleaseVersionMap")!;
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        if (version is not null && map.TryGetValue(version, out string? product)) return ("Exact", product);
        return version == "0.2.0.0" ? ("LegacyAmbiguous", null) : ("Unknown", null);
    }
}

internal sealed record AppDiagnosticSession(string AppSessionId, DateTimeOffset AppSessionStartedAtUtc, int ApplicationProcessId)
{
    internal static AppDiagnosticSession Current { get; } = Create();
    internal static AppDiagnosticSession Create() => new(Guid.NewGuid().ToString("N"),
        new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()), Environment.ProcessId);
}
