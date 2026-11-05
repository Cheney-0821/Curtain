using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Curtain.Core.Protection;
using Curtain.Core.Settings;

namespace Curtain.App.Services;

internal sealed record DiagnosticBundleResult(
    bool Success,
    string? ArchivePath,
    int IncludedFileCount,
    IReadOnlyList<string> Warnings,
    string ResultCode);

/// <summary>
/// Builds a user-initiated, local ZIP report. Only allow-listed Curtain log files and
/// privacy-safe environment summaries are included; settings contents and user files are not.
/// </summary>
internal sealed class DiagnosticBundleBuilder
{
    private sealed record WindowsVersionSnapshot(string? ProductName, string? DisplayVersion,
        string? ReleaseId, string? Build, string? UpdateBuildRevision, string? UiLanguage);

    private const long MaximumSingleFileBytes = 64 * 1024 * 1024;
    private const long MaximumTotalBytes = 128 * 1024 * 1024;
    private const int MaximumLogFiles = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex SafeLogCode = new("^[A-Za-z0-9_.-]{0,96}$", RegexOptions.Compiled);

    internal DiagnosticBundleResult Create(string? destinationDirectory = null, bool includeRawEtw = false,
        bool includeEtwTrace = true)
    {
        var warnings = new List<string>();
        string? archivePath = null;
        int included = 0;

        try
        {
            bool destinationWasExplicit = !string.IsNullOrWhiteSpace(destinationDirectory);
            destinationDirectory = ResolveDestinationDirectory(destinationDirectory);
            try
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            catch (Exception ex) when (!destinationWasExplicit &&
                                       (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException))
            {
                string documentsRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string documentsFallback = Path.Combine(
                    string.IsNullOrWhiteSpace(documentsRoot) ? Path.GetTempPath() : documentsRoot,
                    "Curtain Diagnostics");
                try
                {
                    Directory.CreateDirectory(documentsFallback);
                    destinationDirectory = documentsFallback;
                    warnings.Add("DesktopUnavailableUsedDocumentsFallback");
                }
                catch (Exception fallbackException) when (fallbackException is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    destinationDirectory = Path.Combine(Path.GetTempPath(), "Curtain Diagnostics");
                    Directory.CreateDirectory(destinationDirectory);
                    warnings.Add("DesktopAndDocumentsUnavailableUsedTempFallback");
                }
            }

            archivePath = Path.Combine(destinationDirectory,
                $"Curtain-Diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.zip");
            archivePath = MakeUniquePath(archivePath);

            var manifestEntries = new List<object>();
            long totalBytes = 0;

            using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                AddTextEntry(archive, manifestEntries, "README.txt", BuildPrivacyReadme(), ref included);
                AddTextEntry(archive, manifestEntries, "system/environment.json",
                    JsonSerializer.Serialize(BuildEnvironmentSnapshot(), JsonOptions), ref included);
                AddTextEntry(archive, manifestEntries, "system/displays.json",
                    JsonSerializer.Serialize(WindowsDisplaySnapshotCollector.Collect(), JsonOptions), ref included);
                AddTextEntry(archive, manifestEntries, "system/settings-status.json",
                    JsonSerializer.Serialize(BuildSettingsStatus(), JsonOptions), ref included);
                AddTextEntry(archive, manifestEntries, "system/protected-rules-status.json",
                    JsonSerializer.Serialize(BuildProtectedRulesStatus(), JsonOptions), ref included);
                AddTextEntry(archive, manifestEntries, "system/startup-status.json",
                    JsonSerializer.Serialize(StartupRegistration.GetDiagnosticStatus(), JsonOptions), ref included);
                AddTextEntry(archive, manifestEntries, "system/runtime-status.json",
                    JsonSerializer.Serialize(CurtainRuntimeStatus.Snapshot(), JsonOptions), ref included);

                EventViewerCollectionResult eventViewer = EventViewerCollector.CollectCurtainApplicationErrors();
                if (eventViewer.Success && eventViewer.SanitizedXml is not null)
                {
                    long eventViewerBytes = Encoding.UTF8.GetByteCount(eventViewer.SanitizedXml);
                    if (eventViewerBytes <= MaximumSingleFileBytes && totalBytes + eventViewerBytes <= MaximumTotalBytes)
                    {
                        AddTextEntry(archive, manifestEntries, "system/event-viewer-curtain.xml",
                            eventViewer.SanitizedXml, ref included);
                        totalBytes += eventViewerBytes;
                    }
                    else
                    {
                        warnings.Add("EventViewerSkippedCollectionLimit");
                    }
                }
                else
                {
                    warnings.Add("EventViewerCollection:" + eventViewer.ResultCode);
                }

                foreach (FileInfo source in EnumerateAllowedLogFiles(warnings))
                {
                    if (included >= MaximumLogFiles + 6) break;
                    long sourceLength;
                    try { sourceLength = source.Length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        warnings.Add("LogFileMetadataReadFailed");
                        continue;
                    }
                    if (sourceLength <= 0 || sourceLength > MaximumSingleFileBytes)
                    {
                        warnings.Add($"SkippedOversizedOrEmpty:{source.Extension}");
                        continue;
                    }
                    if (totalBytes + sourceLength > MaximumTotalBytes)
                    {
                        warnings.Add("LogCollectionLimitReached");
                        break;
                    }

                    string entryName = "logs/" + source.Name;
                    if (TryAddSanitizedLogEntry(archive, manifestEntries, entryName, source, warnings, out long sanitizedLength))
                    {
                        included++;
                        totalBytes += sanitizedLength;
                    }
                }

                if (!includeEtwTrace) warnings.Add("EtwCaptureNotStoppedTraceOmitted");
                IEnumerable<FileInfo> etwFiles = includeEtwTrace
                    ? EnumerateEtwFiles(warnings)
                    : Array.Empty<FileInfo>();
                foreach (FileInfo etlFile in etwFiles)
                {
                    EtwTraceExportResult exported = EtwTraceExporter.ConvertToSanitizedXml(etlFile);
                    if (exported.Success && exported.XmlContent is not null)
                    {
                        long xmlBytes = Encoding.UTF8.GetByteCount(exported.XmlContent);
                        if (xmlBytes <= MaximumSingleFileBytes && totalBytes + xmlBytes <= MaximumTotalBytes)
                        {
                            AddTextEntry(archive, manifestEntries,
                                "etw/" + Path.GetFileNameWithoutExtension(etlFile.Name) + ".xml",
                                exported.XmlContent, ref included);
                            totalBytes += xmlBytes;
                        }
                        else
                        {
                            warnings.Add("EtwXmlSkippedCollectionLimit");
                        }
                    }
                    else
                    {
                        warnings.Add("EtwXmlExportFailed:" + exported.ResultCode);
                    }

                    if (includeRawEtw)
                    {
                        long etlLength;
                        try { etlLength = etlFile.Length; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            warnings.Add("RawEtwMetadataReadFailed");
                            continue;
                        }
                        if (etlLength > 0 && etlLength <= MaximumSingleFileBytes && totalBytes + etlLength <= MaximumTotalBytes &&
                            TryAddFileEntry(archive, manifestEntries, "etw/raw/" + etlFile.Name, etlFile, warnings))
                        {
                            included++;
                            totalBytes += etlLength;
                            warnings.Add("RawEtwIncludedMayContainProfilePath");
                        }
                        else
                        {
                            warnings.Add("RawEtwSkipped");
                        }
                    }
                }

                AddTextEntry(archive, manifestEntries, "diagnostic-summary.json",
                    JsonSerializer.Serialize(BuildSummary(warnings), JsonOptions), ref included);
                AddTextEntry(archive, manifestEntries, "manifest.json",
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        createdAtUtc = DateTimeOffset.UtcNow,
                        application = "Curtain",
                        files = manifestEntries.ToArray(),
                        warnings = warnings.ToArray()
                    }, JsonOptions), ref included);
            }

            CurtainDiagnostics.Write("diagnostic.bundle.created", stage: "zip-complete",
                reasonCode: "Success");
            return new(true, archivePath, included, warnings, "Success");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   JsonException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            if (archivePath is not null)
            {
                try { if (File.Exists(archivePath)) File.Delete(archivePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            CurtainDiagnostics.WriteException("diagnostic.bundle.failed", ex,
                stage: "zip-create", reasonCode: "BundleCreationFailed");
            return new(false, null, included, warnings, ex.GetType().Name);
        }
    }

    private static IEnumerable<FileInfo> EnumerateAllowedLogFiles(List<string> warnings)
    {
        string directory = CurtainDiagnostics.DirectoryPath;
        if (!Directory.Exists(directory))
        {
            warnings.Add("DiagnosticsDirectoryMissing");
            return Array.Empty<FileInfo>();
        }

        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("curtain-*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaximumLogFiles)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("DiagnosticsDirectoryReadFailed");
            return Array.Empty<FileInfo>();
        }
    }

    private static IEnumerable<FileInfo> EnumerateEtwFiles(List<string> warnings)
    {
        string directory = CurtainDiagnostics.DirectoryPath;
        if (!Directory.Exists(directory)) return Array.Empty<FileInfo>();
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("curtain-etw-*.etl", SearchOption.TopDirectoryOnly)
                .Where(file => !CurtainDiagnostics.IsActiveEtwFile(file.FullName))
                .Where(file => file.LastWriteTimeUtc >= DateTime.UtcNow.AddHours(-24))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(3)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("EtwDirectoryReadFailed");
            return Array.Empty<FileInfo>();
        }
    }

    private static bool TryAddFileEntry(
        ZipArchive archive,
        List<object> manifest,
        string entryName,
        FileInfo source,
        List<string> warnings)
    {
        try
        {
            using var snapshot = new MemoryStream();
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
            {
                input.CopyTo(snapshot);
            }

            if (snapshot.Length <= 0 || snapshot.Length > MaximumSingleFileBytes)
            {
                warnings.Add("LogFileChangedOrOversizedDuringRead");
                return false;
            }

            byte[] bytes = snapshot.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream output = entry.Open()) output.Write(bytes);

            manifest.Add(new { name = entryName, sizeBytes = bytes.Length, sha256 = hash });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            warnings.Add("LogFileReadFailed:" + source.Extension.TrimStart('.'));
            return false;
        }
    }

    private static bool TryAddSanitizedLogEntry(
        ZipArchive archive,
        List<object> manifest,
        string entryName,
        FileInfo source,
        List<string> warnings,
        out long sanitizedLength)
    {
        sanitizedLength = 0;
        try
        {
            if (source.Length <= 0 || source.Length > MaximumSingleFileBytes)
            {
                warnings.Add("LogFileChangedOrOversizedDuringRead");
                return false;
            }

            using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var output = new MemoryStream();
            int accepted = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length is 0 or > 64 * 1024) continue;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty("timestampUtc", out var timestamp) ||
                        timestamp.ValueKind != JsonValueKind.String ||
                        !timestamp.TryGetDateTimeOffset(out var recordedAt) ||
                        recordedAt < DateTimeOffset.UtcNow.AddHours(-24)) continue;
                    if (!TryWriteSafeLogLine(output, document.RootElement)) continue;
                    accepted++;
                }
                catch (JsonException) { }
                if (output.Length > MaximumSingleFileBytes)
                {
                    warnings.Add("LogSanitizedOutputLimitReached");
                    break;
                }
            }
            if (accepted == 0)
            {
                warnings.Add("NoValidStructuredLogRecords");
                return false;
            }

            byte[] bytes = output.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream stream = entry.Open()) stream.Write(bytes);
            manifest.Add(new { name = entryName, sizeBytes = bytes.Length, sha256 = hash });
            sanitizedLength = bytes.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            warnings.Add("LogFileReadFailed:jsonl");
            return false;
        }
    }

    private static bool TryWriteSafeLogLine(Stream output, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TrySafeString(root, "eventName", out string eventName) || eventName.Length == 0 ||
            !TrySafeString(root, "level", out string level) || level is not ("trace" or "information" or "warning" or "error") ||
            !root.TryGetProperty("timestampUtc", out JsonElement timestampValue) ||
            timestampValue.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(timestampValue.GetString(), out DateTimeOffset timestamp))
            return false;

        using var line = new MemoryStream();
        using (var writer = new Utf8JsonWriter(line))
        {
            writer.WriteStartObject();
            writer.WriteString("timestampUtc", timestamp.ToUniversalTime());
            writer.WriteString("eventName", eventName);
            writer.WriteString("level", level);
            WriteSafeString(writer, root, "operationId");
            WriteSafeString(writer, root, "stage");
            WriteSafeString(writer, root, "reasonCode");
            WriteSafeNumber(writer, root, "applicationProcessId");
            WriteSafeNumber(writer, root, "threadId");
            WriteSafeNumber(writer, root, "hwnd");
            WriteSafeNumber(writer, root, "windowFromPointHwnd");
            WriteSafeNumber(writer, root, "ownerHwnd");
            WriteSafeNumber(writer, root, "relatedHwnd");
            WriteSafeNumber(writer, root, "targetProcessId");
            WriteSafeNumber(writer, root, "errorCode");
            WriteSafeNumber(writer, root, "left");
            WriteSafeNumber(writer, root, "top");
            WriteSafeNumber(writer, root, "width");
            WriteSafeNumber(writer, root, "height");
            WriteSafeNumber(writer, root, "mouseX");
            WriteSafeNumber(writer, root, "mouseY");
            WriteSafeString(writer, root, "metricName");
            WriteSafeDouble(writer, root, "metricValue");
            WriteSafeString(writer, root, "processName");
            WriteSafeString(writer, root, "sessionId");
            WriteSafeString(writer, root, "ruleId");
            writer.WriteEndObject();
        }
        line.WriteByte((byte)'\n');
        line.Position = 0;
        line.CopyTo(output);
        return true;
    }

    private static bool TrySafeString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String &&
               (value = property.GetString() ?? string.Empty) is { } text && SafeLogCode.IsMatch(text);
    }

    private static void WriteSafeString(Utf8JsonWriter writer, JsonElement root, string name)
    {
        if (TrySafeString(root, name, out string value)) writer.WriteString(name, value);
    }

    private static void WriteSafeNumber(Utf8JsonWriter writer, JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out long value)) writer.WriteNumber(name, value);
    }

    private static void WriteSafeDouble(Utf8JsonWriter writer, JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out double value) && double.IsFinite(value) && Math.Abs(value) <= 1_000_000)
            writer.WriteNumber(name, value);
    }

    private static void AddTextEntry(
        ZipArchive archive,
        List<object> manifest,
        string entryName,
        string content,
        ref int included)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using (Stream output = entry.Open()) output.Write(bytes);
        manifest.Add(new
        {
            name = entryName,
            sizeBytes = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        });
        included++;
    }

    private static object BuildSummary(IReadOnlyList<string> warnings) => new
    {
        schemaVersion = 1,
        createdAtUtc = DateTimeOffset.UtcNow,
        application = "Curtain",
        applicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
        currentProcessId = Environment.ProcessId,
        currentProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        diagnosticsDirectoryAvailable = Directory.Exists(CurtainDiagnostics.DirectoryPath),
        detailedEtwFilesPresent = HasEtwFiles(),
        warnings
    };

    private static object BuildEnvironmentSnapshot()
    {
        string? fileVersion = null;
        string? productVersion = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath);
                fileVersion = version.FileVersion;
                productVersion = version.ProductVersion;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Version metadata is optional; do not fail bundle creation for it.
        }

        var windows = ReadWindowsVersion();
        return new
        {
            application = "Curtain",
            assemblyVersion = typeof(DiagnosticBundleBuilder).Assembly.GetName().Version?.ToString(),
            fileVersion,
            productVersion,
            windowsProductName = windows.ProductName,
            windowsDisplayVersion = windows.DisplayVersion,
            windowsReleaseId = windows.ReleaseId,
            windowsBuild = windows.Build,
            windowsUpdateBuildRevision = windows.UpdateBuildRevision,
            windowsUiLanguage = windows.UiLanguage,
            installKind = DetermineInstallKind(),
            osVersion = Environment.OSVersion.Version.ToString(),
            osBuild = Environment.OSVersion.Version.Build,
            osVersionString = Environment.OSVersion.VersionString,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            componentVersions = ReadLoadedComponentVersions(),
            binaries = CollectBinaryMetadata(),
            is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            is64BitProcess = Environment.Is64BitProcess,
            processorCount = Environment.ProcessorCount,
            processId = Environment.ProcessId,
            processName = Process.GetCurrentProcess().ProcessName
        };
    }

    private static WindowsVersionSnapshot ReadWindowsVersion()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            string? productName = key?.GetValue("ProductName") as string;
            string? displayVersion = key?.GetValue("DisplayVersion") as string;
            string? releaseId = key?.GetValue("ReleaseId") as string;
            string? build = key?.GetValue("CurrentBuildNumber") as string;
            string? ubr = key?.GetValue("UBR")?.ToString();
            string? language = key?.GetValue("InstallLanguage")?.ToString();
            string? uiLanguage = null;
            if (int.TryParse(language, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int lcid))
            {
                try { uiLanguage = CultureInfo.GetCultureInfo(lcid).Name; }
                catch (CultureNotFoundException) { }
            }
            return new WindowsVersionSnapshot(SafeDisplayValue(productName), SafeDisplayValue(displayVersion),
                SafeDisplayValue(releaseId), SafeDisplayValue(build),
                SafeDisplayValue(ubr), SafeDisplayValue(uiLanguage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new WindowsVersionSnapshot(null, null, null, null, null, null);
        }
    }

    private static string DetermineInstallKind()
    {
        try
        {
            string? directoryName = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.Name;
            return string.Equals(directoryName, "current", StringComparison.OrdinalIgnoreCase)
                ? "VelopackVersioned"
                : "UnpackedFolderOrDevelopmentOutput";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }

    private static object[] ReadLoadedComponentVersions()
    {
        string[] allowList = ["Microsoft.WindowsAppRuntime", "Microsoft.UI.Xaml", "Microsoft.Graphics.Canvas"];
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => allowList.Any(prefix => assembly.GetName().Name?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true))
            .Select(assembly => new { name = assembly.GetName().Name, version = assembly.GetName().Version?.ToString() })
            .OrderBy(item => item.name, StringComparer.Ordinal)
            .Cast<object>()
            .ToArray();
    }

    private static object[] CollectBinaryMetadata()
    {
        string[] allowList = ["Curtain.App.exe", "Curtain.Core.dll", "Microsoft.WindowsAppRuntime.Bootstrap.dll",
            "Microsoft.Graphics.Canvas.dll", "Microsoft.UI.Xaml.dll"];
        var results = new List<object>();
        foreach (string fileName in allowList)
        {
            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                results.Add(new
                {
                    name = fileName,
                    fileVersion = SafeDisplayValue(version.FileVersion),
                    productVersion = SafeDisplayValue(version.ProductVersion),
                    sha256 = Convert.ToHexString(SHA256.HashData(stream)),
                    sizeBytes = info.Length
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException or ArgumentException)
            {
                results.Add(new { name = fileName, fileVersion = (string?)null,
                    productVersion = (string?)null, sha256 = (string?)null, sizeBytes = 0L });
            }
        }
        return results.ToArray();
    }

    private static string? SafeDisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ? null : value;

    private static bool HasEtwFiles()
    {
        try
        {
            return Directory.Exists(CurtainDiagnostics.DirectoryPath) &&
                Directory.EnumerateFiles(CurtainDiagnostics.DirectoryPath, "curtain-etw-*.etl").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static object BuildSettingsStatus()
    {
        string path = Path.Combine(CurtainDiagnostics.DataDirectory, "settings.json");
        CurtainUserSettingsLoad load = new CurtainUserSettingsStore(CurtainDiagnostics.DataDirectory).Load();
        string validation = !File.Exists(path) ? "NotPresent" : load.RecoveredFromInvalidFile ? "Invalid" : "Valid";
        return new { file = BuildFileStatus(path), validation };
    }

    private static object BuildProtectedRulesStatus()
    {
        string path = Path.Combine(CurtainDiagnostics.DataDirectory, "protected-apps.json");
        ProtectedAppRulesLoadResult load = new ProtectedAppRuleStore(CurtainDiagnostics.DataDirectory).Load();
        string validation = !File.Exists(path) ? "NotPresent" :
            load.RecoveryCode ?? (load.RecoveredFromInvalidEntries ? "InvalidEntries" : "Valid");
        int? ruleCount = File.Exists(path) || load.RecoveredFromBackup ? load.Rules.Count : null;
        return new { file = BuildFileStatus(path), backup = BuildFileStatus(path + ".bak"), validation, ruleCount };
    }

    private static object BuildFileStatus(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new
            {
                exists = info.Exists,
                sizeBytes = info.Exists ? info.Length : 0,
                lastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : (DateTime?)null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new { exists = false, sizeBytes = 0L, lastWriteTimeUtc = (DateTime?)null };
        }
    }

    private static string BuildPrivacyReadme() =>
        "Curtain diagnostic report\n" +
        "\n" +
        "This archive was created locally after an explicit user action. It contains Curtain's allow-listed structured logs, sanitized XML exports of optional Curtain ETW traces, and a minimal system/runtime summary.\n" +
        "\n" +
        "It does not intentionally include screenshots, recordings, window titles, window contents, keyboard input, clipboard data, raw settings, browser data, cookies, tokens, passwords, memory dumps, or arbitrary user files.\n" +
        "\n" +
        "ETW exports are reconstructed from Curtain-Privacy events using a fixed field allow-list. Event Viewer entries are reduced to provider, event ID, timestamp, process/thread IDs, and validated crash codes; original event payloads are not included. If the user explicitly opted in to raw ETL, that binary may contain device/profile names, HWND/PID metadata and screen coordinates; review it before sharing.\n";

    private static string ResolveDestinationDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop)) return desktop;
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents)) return documents;
        return Path.GetTempPath();
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 1; index < 100; index++)
        {
            string candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to allocate a unique diagnostic archive name.");
    }
}
