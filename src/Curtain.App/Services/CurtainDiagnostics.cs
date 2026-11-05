using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Curtain.App.Services;

internal enum CurtainDiagnosticLevel
{
    Trace,
    Information,
    Warning,
    Error
}

internal sealed record CurtainDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string EventName,
    string Level,
    string OperationId,
    string Stage,
    string ReasonCode,
    int ApplicationProcessId,
    int ThreadId,
    long Hwnd,
    long WindowFromPointHwnd,
    long OwnerHwnd,
    long RelatedHwnd,
    int TargetProcessId,
    int ErrorCode,
    int Left,
    int Top,
    int Width,
    int Height,
    int MouseX,
    int MouseY,
    string MetricName,
    double MetricValue,
    string ProcessName,
    string SessionId,
    string RuleId);

/// <summary>
/// Release-safe structured local diagnostics. This deliberately has no arbitrary message,
/// exception-message, window-title, clipboard, screenshot or full-path field.
/// </summary>
internal static class CurtainDiagnostics
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private const int MaximumRetainedFiles = 20;
    private const int MaximumRetainedEtwFiles = 3;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Curtain");
    private static string _directoryPath = Path.Combine(_dataDirectory, "Diagnostics");
    private static string? _activePath;
    private static string? _activeEtwPath;
    internal static void SetActiveEtwPath(string? path) { lock (Gate) _activeEtwPath = path; }
    internal static bool IsActiveEtwFile(string path)
    { lock (Gate) return string.Equals(path, _activeEtwPath, StringComparison.OrdinalIgnoreCase); }
    private static bool _initialized;
    private static volatile bool _enabled = true;

    internal static void SetEnabled(bool enabled)
    {
        // Serialize the preference boundary with writers. When this method returns false,
        // no writer that started earlier can still append a new diagnostic record.
        lock (Gate) _enabled = enabled;
    }

    internal static void Prune()
    {
        try
        {
            lock (Gate) CleanupOldLogsLocked();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"Curtain log prune failed: {ex.GetType().Name} / 0x{ex.HResult:X8}");
        }
    }

    internal static string DirectoryPath => _directoryPath;
    internal static string DataDirectory => _dataDirectory;

    internal static void Initialize(string? dataDirectoryOverride = null)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(dataDirectoryOverride))
                _dataDirectory = dataDirectoryOverride;
            _directoryPath = string.Empty;
            try
            {
                _directoryPath = Path.Combine(_dataDirectory, "Diagnostics");
                Directory.CreateDirectory(_directoryPath);
                _initialized = true;
                CleanupOldLogsLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                _initialized = false;
                Debug.WriteLine($"Curtain diagnostics initialization failed: {ex.GetType().Name} / 0x{ex.HResult:X8}");
            }
        }

        Write("app.started", CurtainDiagnosticLevel.Information, stage: "startup");
    }

    internal static string NewOperationId() => Guid.NewGuid().ToString("N");

    internal static void WriteEtwCaptureState(string state, int processId, int resultCode)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            try
            {
                CurtainEventSource.Log.EtwCaptureState(SafeCode(state, "state"),
                    Math.Max(0, processId), resultCode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Curtain ETW state write failed: {ex.GetType().Name} / 0x{ex.HResult:X8}");
            }
        }
    }

    internal static void Write(
        string eventName,
        CurtainDiagnosticLevel level = CurtainDiagnosticLevel.Information,
        string? operationId = null,
        string? stage = null,
        string? reasonCode = null,
        long hwnd = 0,
        long windowFromPointHwnd = 0,
        long ownerHwnd = 0,
        long relatedHwnd = 0,
        int targetProcessId = 0,
        int errorCode = 0,
        int left = 0,
        int top = 0,
        int width = 0,
        int height = 0,
        int mouseX = 0,
        int mouseY = 0,
        string? metricName = null,
        double metricValue = 0,
        string? processName = null,
        string? sessionId = null,
        string? ruleId = null)
    {
        if (!_enabled) return;
        string safeEvent = SafeCode(eventName, "event.unknown");
        string safeLevel = level.ToString().ToLowerInvariant();
        string safeOperation = SafeCode(operationId, string.Empty);
        string safeStage = SafeCode(stage, string.Empty);
        string safeReason = SafeCode(reasonCode, string.Empty);

        var entry = new CurtainDiagnosticEntry(
            DateTimeOffset.UtcNow,
            safeEvent,
            safeLevel,
            safeOperation,
            safeStage,
            safeReason,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            hwnd,
            windowFromPointHwnd,
            ownerHwnd,
            relatedHwnd,
            targetProcessId,
            errorCode,
            left,
            top,
            width,
            height,
            mouseX,
            mouseY,
            SafeCode(metricName, string.Empty),
            double.IsFinite(metricValue) && Math.Abs(metricValue) <= 1_000_000 ? metricValue : 0,
            SafeCode(processName, string.Empty),
            SafeCode(sessionId, string.Empty),
            SafeCode(ruleId, string.Empty));

        lock (Gate)
        {
            // SetEnabled uses the same gate, so the opt-out is a strict boundary for both
            // the JSONL writer and the ETW provider.
            if (!_enabled) return;

            try
            {
                CurtainEventSource.Log.DiagnosticRecord(safeEvent, safeLevel, safeOperation,
                    safeStage, safeReason, hwnd, windowFromPointHwnd, ownerHwnd, relatedHwnd,
                    targetProcessId, errorCode,
                    left, top, width, height, mouseX, mouseY,
                    SafeCode(metricName, string.Empty),
                    double.IsFinite(metricValue) && Math.Abs(metricValue) <= 1_000_000 ? metricValue : 0,
                    SafeCode(processName, string.Empty),
                    SafeCode(sessionId, string.Empty), SafeCode(ruleId, string.Empty));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Curtain ETW write failed: {ex.GetType().Name} / 0x{ex.HResult:X8}");
            }

            if (!_initialized)
            {
                try
                {
                    Directory.CreateDirectory(_directoryPath);
                    _initialized = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException or ArgumentException or NotSupportedException)
                {
                    Debug.WriteLine($"Curtain log unavailable: {ex.GetType().Name} / 0x{ex.HResult:X8}");
                    return;
                }
            }

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
                string path = GetWritableLogPathLocked(bytes.Length);
                bool rotated = !string.Equals(path, _activePath, StringComparison.OrdinalIgnoreCase);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                stream.Write(bytes);
                _activePath = path;
                if (rotated) CleanupOldLogsLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException or ArgumentException or NotSupportedException or JsonException)
            {
                Debug.WriteLine($"Curtain log write failed: {ex.GetType().Name} / 0x{ex.HResult:X8}");
            }
        }
    }

    internal static void WriteException(
        string eventName,
        Exception exception,
        string? operationId = null,
        string? stage = null,
        string? reasonCode = null,
        long hwnd = 0,
        long ownerHwnd = 0,
        int targetProcessId = 0,
        int win32Error = 0,
        string? sessionId = null,
        string? ruleId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // Exception.Message and StackTrace are intentionally excluded; they can contain user paths.
        int code = exception is Win32Exception win32
            ? win32.NativeErrorCode
            : exception.HResult != 0 ? exception.HResult : win32Error;
        Write(eventName, CurtainDiagnosticLevel.Error,
            operationId: operationId,
            stage: stage,
            reasonCode: reasonCode ?? exception.GetType().Name,
            hwnd: hwnd,
            ownerHwnd: ownerHwnd,
            targetProcessId: targetProcessId,
            errorCode: code,
            sessionId: sessionId,
            ruleId: ruleId);
    }

    private static string GetWritableLogPathLocked(int additionalBytes)
    {
        string hour = DateTime.UtcNow.ToString("yyyyMMdd-HH");
        string stem = $"curtain-{hour}-{Environment.ProcessId}";
        if (_activePath is not null && Path.GetFileName(_activePath).StartsWith(stem, StringComparison.Ordinal) &&
            File.Exists(_activePath) && new FileInfo(_activePath).Length + additionalBytes <= MaximumLogBytes)
            return _activePath;
        for (int index = 0; ; index++)
        {
            string name = index == 0 ? stem + ".jsonl" : $"{stem}-{index}.jsonl";
            string candidate = Path.Combine(_directoryPath, name);
            if (!File.Exists(candidate) || new FileInfo(candidate).Length + additionalBytes <= MaximumLogBytes) return candidate;
        }
    }

    private static void CleanupOldLogsLocked()
    {
        try
        {
            var files = new DirectoryInfo(_directoryPath)
                .EnumerateFiles("curtain-*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            DateTime cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc < cutoff || files.Length > MaximumRetainedFiles &&
                    Array.IndexOf(files, file) >= MaximumRetainedFiles)
                {
                    if (!string.Equals(file.FullName, _activePath, StringComparison.OrdinalIgnoreCase))
                        file.Delete();
                }
            }

            var etlFiles = new DirectoryInfo(_directoryPath)
                .EnumerateFiles("curtain-etw-*.etl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            var retainedEtw = etlFiles
                .OrderByDescending(file => string.Equals(file.FullName, _activeEtwPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .Take(MaximumRetainedEtwFiles).Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in etlFiles)
            {
                bool active = string.Equals(file.FullName, _activeEtwPath, StringComparison.OrdinalIgnoreCase);
                if (!active && (file.LastWriteTimeUtc < cutoff || !retainedEtw.Contains(file.FullName))) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"Curtain log cleanup failed: {ex.GetType().Name} / 0x{ex.HResult:X8}");
        }
    }

    private static string SafeCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string source = value.Trim();
        var builder = new StringBuilder(Math.Min(source.Length, 96));
        foreach (char character in source)
        {
            if (builder.Length >= 96) break;
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                builder.Append(character);
        }
        return builder.Length == 0 ? fallback : builder.ToString();
    }
}
