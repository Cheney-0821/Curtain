using System.Runtime.InteropServices;
using System.Reflection;
using Velopack;

namespace Curtain.App.Services;

internal sealed record InstallKindResult(string Kind, string DetectionMethod);

internal static class InstallKindDetector
{
    internal const int ErrorInsufficientBuffer = 122;
    internal const int AppModelErrorNoPackage = 15700;

    internal static InstallKindResult Detect()
    {
        try
        {
            int length = 0;
            int packageResult = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            string distribution = packageResult == AppModelErrorNoPackage ? ReadDistributionKind() : "Unknown";

            // The package-identity check must run before Velopack initialization. A Store/MSIX
            // process is classified immediately and never creates a Direct-channel UpdateManager.
            return Classify(packageResult, () =>
            {
                var manager = new UpdateManager(LocalUpdateService.BetaFeedUrl);
                return manager.IsInstalled && !manager.IsPortable;
            }, distribution);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return new("Unknown", $"DetectionFailed.{ex.GetType().Name}");
        }
    }

    internal static InstallKindResult Classify(int packageResult, Func<bool> isVelopackInstalled, string distribution)
    {
        if (packageResult == ErrorInsufficientBuffer)
            return new("MsixPackaged", "GetCurrentPackageFullName.PackageIdentityPresent");

        // Only the documented NO_PACKAGE result permits probing the unpackaged/Velopack path.
        // Any other native result is an actual detection error and must remain Unknown.
        if (packageResult != AppModelErrorNoPackage)
            return new("Unknown", $"GetCurrentPackageFullName.Error.{packageResult}");

        try
        {
            if (isVelopackInstalled())
                return new("VelopackInstalled", "Velopack.UpdateManager.IsInstalled.NotPortable");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return new("Unknown", $"VelopackDetectionFailed.{ex.GetType().Name}");
        }

        return distribution switch
        {
            "Development" => new("Development", "AssemblyMetadata.DistributionKind"),
            "PortableOrUnpacked" => new("PortableOrUnpacked", "AssemblyMetadata.DistributionKind"),
            _ => new("Unknown", "InsufficientEvidence")
        };
    }

    private static string ReadDistributionKind() =>
        typeof(InstallKindDetector).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == "DistributionKind")?.Value ?? "Unknown";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);
}
