using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Curtain.Core.Updates;

/// <summary>Reserved metadata schema for a future signed-update path; the active updater uses Velopack.</summary>
public sealed record ReleaseManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("asset_url")] string AssetUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("signature")] string Signature)
{
    public IReadOnlyList<string> Validate(string trustedDownloadHost)
    {
        var errors = new List<string>();
        if (SchemaVersion != 1) errors.Add("Unsupported schema_version");
        if (!System.Version.TryParse(Version, out _)) errors.Add("Invalid version");
        if (Channel is not ("stable" or "beta")) errors.Add("Invalid channel");
        if (!Uri.TryCreate(AssetUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, trustedDownloadHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            errors.Add("Asset must use HTTPS and the allowlisted host");
        if (!Regex.IsMatch(Sha256 ?? "", "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            errors.Add("Invalid sha256");
        if (SizeBytes is <= 0 or > 2_000_000_000) errors.Add("Invalid size_bytes");
        if (string.IsNullOrWhiteSpace(KeyId)) errors.Add("Missing key_id");
        if (string.IsNullOrWhiteSpace(Signature)) errors.Add("Missing signature");
        // Checking presence is not signature verification. Update installation MUST remain
        // disabled until a genuine signature verifier with pinned public keys exists.
        return errors;
    }
}
