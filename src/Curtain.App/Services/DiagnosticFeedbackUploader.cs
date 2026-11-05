using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Curtain.App.Services;

internal sealed record DiagnosticFeedbackUploadResult(
    bool Success,
    string? FeedbackId,
    string ResultCode,
    int HttpStatusCode = 0,
    int RetryAfterSeconds = 0);

/// <summary>
/// Explicit opt-in feedback upload. Never stores credentials or auto-uploads diagnostics.
/// Uses the public Curtain feedback endpoint, with redirects disabled to prevent ZIP exfiltration.
/// </summary>
internal sealed class DiagnosticFeedbackUploader
{
    internal const long MaxZipBytes = 16L * 1024 * 1024;
    private static readonly Uri UploadUri = new("https://feedback.cheneylab.cn/api/feedback");
    private static readonly Uri HealthUri = new("https://feedback.cheneylab.cn/health");
    private static readonly HttpClient DefaultClient = CreateClient();
    private readonly HttpClient _client;

    internal DiagnosticFeedbackUploader(HttpClient? client = null)
    {
        _client = client ?? DefaultClient;
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    internal async Task<DiagnosticFeedbackUploadResult> UploadAsync(
        string archivePath, string? email, string? description, Guid requestId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(archivePath) || !archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return new(false, null, "ArchiveMissing");

            FileInfo info = new(archivePath);
            if (info.Length < 22) return new(false, null, "ArchiveInvalid");
            if (info.Length > MaxZipBytes) return new(false, null, "ArchiveTooLarge");
            string cleanEmail = (email ?? string.Empty).Trim();
            string cleanDescription = (description ?? string.Empty).Trim();
            if (cleanDescription.Length > 300 || Encoding.UTF8.GetByteCount(cleanDescription) > 900 ||
                cleanDescription.Any(c => c < ' ' && c != '\r' && c != '\n' && c != '\t'))
                return new(false, null, "DescriptionInvalid");
            if (!IsValidOptionalEmail(cleanEmail)) return new(false, null, "EmailInvalid");

            // Only a newly generated local Curtain report is accepted by the UI. Other ZIP
            // names are rejected here as a defense against accidentally uploading user files.
            if (!Path.GetFileName(archivePath).StartsWith("Curtain-Diagnostic-", StringComparison.OrdinalIgnoreCase))
                return new(false, null, "ArchiveInvalid");

            using FileStream fileStream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (HttpResponseMessage health = await _client.GetAsync(HealthUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (health.StatusCode != HttpStatusCode.OK)
                    return new(false, null, "ProtocolUnavailable", (int)health.StatusCode);
                byte[]? healthJson = await ReadBoundedResponseAsync(health, cancellationToken).ConfigureAwait(false);
                if (healthJson is null) return new(false, null, "ProtocolUnavailable");
                using JsonDocument healthDocument = JsonDocument.Parse(healthJson);
                JsonElement healthRoot = healthDocument.RootElement;
                if (healthRoot.ValueKind != JsonValueKind.Object ||
                    !healthRoot.TryGetProperty("status", out var status) || status.GetString() != "ok" ||
                    !healthRoot.TryGetProperty("service", out var service) || service.GetString() != "curtain-feedback-upload" ||
                    !healthRoot.TryGetProperty("protocol", out var protocol) || protocol.GetString() != "multipart-v1")
                    return new(false, null, "ProtocolUnavailable");
            }
            string archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(fileStream, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            fileStream.Position = 0;
            using MultipartFormDataContent form = new();
            using StreamContent zipPart = new(fileStream);
            zipPart.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(zipPart, "file", "Curtain-Diagnostics.zip");
            zipPart.Headers.ContentDisposition!.FileNameStar = null;
            zipPart.Headers.ContentDisposition.FileName = "\"Curtain-Diagnostics.zip\"";
            form.Add(new StringContent(cleanEmail, Encoding.UTF8), "email");
            form.Add(new StringContent(cleanDescription, Encoding.UTF8), "description");
            string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.2.0-beta.6";
            form.Add(new StringContent(version, Encoding.UTF8), "version");
            form.Add(new StringContent(requestId.ToString("D"), Encoding.UTF8), "requestId");
            // Match browser FormData syntax accepted by the Worker parser.
            foreach (HttpContent part in form)
                part.Headers.ContentDisposition!.Name = "\"" + part.Headers.ContentDisposition.Name!.Trim('"') + "\"";

            using HttpRequestMessage request = new(HttpMethod.Post, UploadUri) { Content = form };
            request.Headers.TryAddWithoutValidation("X-Curtain-Consent", "1");
            request.Headers.UserAgent.ParseAdd("Curtain-Diagnostics/1.0");

            using HttpResponseMessage response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                return new(false, null, "ArchiveTooLarge", (int)response.StatusCode);
            // A 409 means this stable request ID is already bound to a different ZIP.
            // Keep the local archive and ask the user to generate a fresh bundle/ID.
            if (response.StatusCode == HttpStatusCode.Conflict)
                return new(false, null, "RequestIdConflict", (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(false, null, "RateLimited", (int)response.StatusCode,
                    Math.Clamp((int)Math.Ceiling((response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60)).TotalSeconds), 1, 3600));
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return new(false, null, "ServiceUnavailable", (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                return new(false, null, "ArchiveInvalid", (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.BadRequest)
                return new(false, null, await ReadBadRequestCodeAsync(response, cancellationToken).ConfigureAwait(false), (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.UnsupportedMediaType)
                return new(false, null, "UnexpectedResponse", (int)response.StatusCode);
            if (response.StatusCode != HttpStatusCode.Created)
                return new(false, null, "UnexpectedResponse", (int)response.StatusCode);

            byte[]? json = await ReadBoundedResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (json is null)
                return new(false, null, "UnexpectedResponse", (int)response.StatusCode);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("feedbackId", out JsonElement id) ||
                id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out Guid parsedId) || parsedId != requestId)
                return new(false, null, "UnexpectedResponse", (int)response.StatusCode);
            if (!root.TryGetProperty("retentionDays", out JsonElement retention) ||
                retention.ValueKind != JsonValueKind.Number || !retention.TryGetInt32(out int days) || days != 14)
                return new(false, null, "UnexpectedResponse", (int)response.StatusCode);
            if (!root.TryGetProperty("sizeBytes", out var size) || !size.TryGetInt64(out long bytes) || bytes != fileStream.Length ||
                !root.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String || hash.GetString() != archiveHash ||
                !root.TryGetProperty("receivedAt", out var received) || received.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(received.GetString(), out _))
                return new(false, null, "UnexpectedResponse", (int)response.StatusCode);

            return new(true, parsedId.ToString("D"), "Success", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, null, "Cancelled");
        }
        catch (OperationCanceledException)
        {
            return new(false, null, "Timeout");
        }
        catch (HttpRequestException)
        {
            return new(false, null, "NetworkUnavailable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or JsonException or
                                   ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new(false, null, "UploadFailed");
        }
    }

    private static async Task<byte[]?> ReadBoundedResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is > 8192) return null;
        using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] buffer = new byte[8193];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) return buffer[..length];
            length += read;
        }
        return null;
    }

    private static async Task<string> ReadBadRequestCodeAsync(HttpResponseMessage response, CancellationToken token)
    {
        byte[]? json = await ReadBoundedResponseAsync(response, token).ConfigureAwait(false);
        if (json is null) return "BadRequest";
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
                return "BadRequest";
            return error.GetString() switch
            {
                "INVALID_MULTIPART" => "MultipartInvalid",
                "INVALID_VERSION" => "VersionInvalid",
                "INVALID_REQUEST_ID" => "RequestIdInvalid",
                "INVALID_EMAIL" => "EmailInvalid",
                "INVALID_DESCRIPTION" => "DescriptionInvalid",
                "CONSENT_REQUIRED" => "ConsentRequired",
                _ => "BadRequest"
            };
        }
        catch (JsonException) { return "BadRequest"; }
    }

    private static bool IsValidOptionalEmail(string email)
    {
        if (email.Length == 0) return true;
        if (email.Length > 254 || Encoding.UTF8.GetByteCount(email) > 254 ||
            email.Contains('\r') || email.Contains('\n')) return false;
        try
        {
            var parsed = new System.Net.Mail.MailAddress(email);
            return string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase) &&
                   email.Contains('.') && email.Contains('@');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
