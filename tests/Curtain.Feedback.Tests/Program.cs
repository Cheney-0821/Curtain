using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.IO.Compression;
using Curtain.App.Services;

if (args.SequenceEqual(new[] { "--live-synthetic" }))
{
    string syntheticRoot = Path.Combine(Path.GetTempPath(), "CurtainFeedbackLive-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(syntheticRoot);
    string syntheticZip = Path.Combine(syntheticRoot, "Curtain-Diagnostic-Synthetic.zip");
    try
    {
        using (var archive = ZipFile.Open(syntheticZip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("README.txt").Open()))
            writer.Write("Synthetic Curtain feedback test. No personal data.");
        Guid id = Guid.NewGuid();
        var result = await new DiagnosticFeedbackUploader().UploadAsync(syntheticZip, "test@example.invalid", "合成测试", id);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { requestId = id, result.Success, result.FeedbackId, result.ResultCode, result.HttpStatusCode }));
        if (!result.Success) Environment.ExitCode = 1;
    }
    finally { Directory.Delete(syntheticRoot, recursive: true); }
    return;
}

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

string root = Path.Combine(Path.GetTempPath(), "CurtainFeedbackTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string archivePath = Path.Combine(root, "Curtain-Diagnostic-20261105-123456-0001.zip");
// For HTTP contract validation, uploader itself never extracts ZIP content.
byte[] testPayload = new byte[36];
testPayload[0] = 0x50; testPayload[1] = 0x4b; testPayload[2] = 0x03; testPayload[3] = 0x04;
File.WriteAllBytes(archivePath, testPayload);
try
{
    var handler = new FakeHandler(async request =>
    {
        Check(request.RequestUri!.ToString() == "https://feedback.cheneylab.cn/api/feedback", "fixed HTTPS endpoint");
        Check(request.Method == HttpMethod.Post, "uses POST");
        Check(request.Headers.GetValues("X-Curtain-Consent").Single() == "1", "explicit consent header");
        byte[] body = await request.Content!.ReadAsByteArrayAsync();
        if (args.Length == 2 && args[0] == "--export-wire")
        {
            Directory.CreateDirectory(args[1]);
            File.WriteAllBytes(Path.Combine(args[1], "synthetic-multipart.bin"), body);
            File.WriteAllText(Path.Combine(args[1], "content-type.txt"), request.Content.Headers.ContentType!.ToString());
        }
        string multipart = Encoding.UTF8.GetString(body);
        Check(multipart.Contains("Curtain-Diagnostics.zip"), "filename excludes local path/PID");
        Check(multipart.Contains("name=\"file\"; filename=\"Curtain-Diagnostics.zip\"") && !multipart.Contains("filename*="), "browser-compatible quoted file disposition");
        Check(new[] { "email", "description", "version", "requestId" }.All(name => multipart.Contains("name=\"" + name + "\"")), "all field names quoted for Worker parser");
        Check(multipart.Contains("version") && multipart.Contains("requestId"), "version and idempotency metadata");
        Check(multipart.Contains("description") && multipart.Contains("email"), "optional fields included");
        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new {
                feedbackId = "4a02c85b-b077-4c9f-9aa2-f14d48f298b3", retentionDays = 14,
                sizeBytes = testPayload.Length, sha256 = Convert.ToHexString(SHA256.HashData(testPayload)).ToLowerInvariant(),
                receivedAt = DateTimeOffset.UtcNow.ToString("O") }), Encoding.UTF8, "application/json")
        };
    });
    using var client = new HttpClient(handler);
    var uploader = new DiagnosticFeedbackUploader(client);
    Guid requestId = Guid.Parse("4a02c85b-b077-4c9f-9aa2-f14d48f298b3");
    var success = await uploader.UploadAsync(archivePath, "", "模拟窗口无法选择", requestId);
    Check(success.Success && success.FeedbackId == "4a02c85b-b077-4c9f-9aa2-f14d48f298b3", "success returns feedback ID");

    var invalidEmail = await uploader.UploadAsync(archivePath, "not-an-email", "", requestId);
    Check(!invalidEmail.Success && invalidEmail.ResultCode == "EmailInvalid", "email validated before network");
    var missing = await uploader.UploadAsync(Path.Combine(root, "none.zip"), null, null, requestId);
    Check(!missing.Success && missing.ResultCode == "ArchiveMissing", "missing archive doesn't upload");
    string unrelatedZip = Path.Combine(root, "my-personal-files.zip");
    File.Copy(archivePath, unrelatedZip);
    var invalidFile = await uploader.UploadAsync(unrelatedZip, null, null, requestId);
    Check(!invalidFile.Success && invalidFile.ResultCode == "ArchiveInvalid", "unrelated ZIP refused");
    var cancelled = await uploader.UploadAsync(archivePath, null, null, requestId, new CancellationToken(true));
    Check(!cancelled.Success && cancelled.ResultCode == "Cancelled", "cancelled upload doesn't send");

    var rateLimit = new DiagnosticFeedbackUploader(new HttpClient(new FakeHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)))));
    var limited = await rateLimit.UploadAsync(archivePath, null, null, requestId);
    Check(!limited.Success && limited.ResultCode == "RateLimited", "429 user-friendly reason");
    Check(limited.RetryAfterSeconds == 60, "429 respects default retry delay");
    using var rejected = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) {
        Content = new StringContent("{\"error\":\"INVALID_MULTIPART\"}")
    })));
    Check((await new DiagnosticFeedbackUploader(rejected).UploadAsync(archivePath, null, null, requestId)).ResultCode == "MultipartInvalid",
        "server multipart error retained as fixed reason code");
    using var requestIdConflict = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) {
        Content = new StringContent("{\"error\":\"REQUEST_ID_CONFLICT\"}")
    })));
    var conflict = await new DiagnosticFeedbackUploader(requestIdConflict).UploadAsync(archivePath, null, null, requestId);
    Check(!conflict.Success && conflict.ResultCode == "RequestIdConflict" && conflict.HttpStatusCode == 409,
        "409 request ID conflict maps to a fixed recoverable reason");
    Check(File.Exists(archivePath), "409 conflict preserves the local diagnostic ZIP");
    using var unknownError = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) {
        Content = new StringContent("{\"error\":\"untrusted-server-detail\"}")
    })));
    Check((await new DiagnosticFeedbackUploader(unknownError).UploadAsync(archivePath, null, null, requestId)).ResultCode == "BadRequest",
        "arbitrary server response never becomes log reason");
    var redirected = new DiagnosticFeedbackUploader(new HttpClient(new FakeHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)))));
    var redirectResult = await redirected.UploadAsync(archivePath, null, null, requestId);
    Check(!redirectResult.Success && redirectResult.ResultCode == "UnexpectedResponse", "redirect isn't success");
    var wrongRetention = new DiagnosticFeedbackUploader(new HttpClient(new FakeHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"feedbackId\":\"4a02c85b-b077-4c9f-9aa2-f14d48f298b3\",\"retentionDays\":30}")
        }))));
    Check((await wrongRetention.UploadAsync(archivePath, null, null, requestId)).ResultCode == "UnexpectedResponse",
        "unexpected retention contract isn't silently accepted");
    Check(File.Exists(archivePath), "local ZIP remains after upload and errors");
    int incompatiblePosts = 0;
    using var incompatible = new HttpClient(new FakeHandler(request => {
        if (request.Method == HttpMethod.Post) incompatiblePosts++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\"}") });
    }, healthPreflight: false));
    Check((await new DiagnosticFeedbackUploader(incompatible).UploadAsync(archivePath, null, null, requestId)).ResultCode == "ProtocolUnavailable"
        && incompatiblePosts == 0, "legacy server receives no ZIP");
    using var oversized = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) {
        Content = new StreamContent(new NonSeekableStream(new byte[10000]))
    })));
    Check((await new DiagnosticFeedbackUploader(oversized).UploadAsync(archivePath, null, null, requestId)).ResultCode == "UnexpectedResponse",
        "response stream is bounded without trusting Content-Length");
    using var offline = new HttpClient(new FakeHandler(_ => throw new HttpRequestException()));
    Check((await new DiagnosticFeedbackUploader(offline).UploadAsync(archivePath, null, null, requestId)).ResultCode == "NetworkUnavailable", "network failure keeps local ZIP");
    using var timeout = new HttpClient(new FakeHandler(_ => throw new TaskCanceledException()));
    Check((await new DiagnosticFeedbackUploader(timeout).UploadAsync(archivePath, null, null, requestId)).ResultCode == "Timeout", "timeout reason");
    using var cancelDuringUpload = new CancellationTokenSource();
    using var slow = new HttpClient(new FakeHandler(async _ => {
        cancelDuringUpload.CancelAfter(20);
        await Task.Delay(Timeout.Infinite, cancelDuringUpload.Token);
        return new HttpResponseMessage(HttpStatusCode.Created);
    }));
    Check((await new DiagnosticFeedbackUploader(slow).UploadAsync(archivePath, null, null, requestId, cancelDuringUpload.Token)).ResultCode == "Cancelled",
        "in-flight cancellation keeps ZIP");
    using (var huge = File.Create(archivePath)) huge.SetLength(DiagnosticFeedbackUploader.MaxZipBytes + 1);
    Check((await uploader.UploadAsync(archivePath, null, null, requestId)).ResultCode == "ArchiveTooLarge", "16 MiB preflight before network");
}

finally
{
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"PASS: {passed} assertions");

sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
}

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback, bool healthPreflight = true) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => healthPreflight && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/health"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"status\":\"ok\",\"service\":\"curtain-feedback-upload\",\"protocol\":\"multipart-v1\"}") })
            : callback(request);
}
