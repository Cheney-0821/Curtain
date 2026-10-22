using Velopack;

namespace Curtain.App.Services;

/// <summary>
/// Public beta update feed hosted on Cloudflare R2 via HTTPS.
/// No R2 credentials are embedded in the client.
/// </summary>
internal sealed class LocalUpdateService
{
    // The release feed and .nupkg files live together under /beta/.
    // Keep the existing Velopack channel "win" for the beta test releases.
    internal const string BetaFeedUrl = "https://curtain-updates.cheneylab.cn/beta";

    internal UpdateManager CreateManager() => new(BetaFeedUrl);

    internal string InstalledVersion
    {
        get
        {
            var manager = CreateManager();
            if (manager.CurrentVersion is not null) return manager.CurrentVersion.ToString();
            string? assemblyVersion = typeof(LocalUpdateService).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            return assemblyVersion?.Split('+')[0] ?? "开发版";
        }
    }
}
