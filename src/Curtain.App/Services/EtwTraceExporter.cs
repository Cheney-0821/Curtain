using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Curtain.App.Services;

internal sealed record EtwTraceExportResult(bool Success, string? XmlContent, string ResultCode);

/// <summary>
/// Converts a stopped ETL to a fixed, Curtain-only event schema. Tracerpt output is untrusted:
/// only recognized provider events and allow-listed, validated fields are emitted.
/// </summary>
internal static class EtwTraceExporter
{
    private const long MaximumXmlBytes = 64 * 1024 * 1024;
    private static readonly Regex SafeCode = new("^[A-Za-z0-9_.:-]{1,96}$", RegexOptions.Compiled);

    internal static EtwTraceExportResult ConvertToSanitizedXml(FileInfo etlFile)
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Curtain-EtwExport-" + Guid.NewGuid().ToString("N"));
        string outputPath = Path.Combine(temporaryDirectory, Path.GetFileNameWithoutExtension(etlFile.Name) + ".xml");
        try
        {
            if (!etlFile.Exists || etlFile.Length <= 0 || etlFile.Length > MaximumXmlBytes)
                return new(false, null, "EtwFileMissingOrTooLarge");

            Directory.CreateDirectory(temporaryDirectory);
            string tracerpt = Path.Combine(Environment.SystemDirectory, "tracerpt.exe");
            var start = new ProcessStartInfo { FileName = tracerpt, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(etlFile.FullName);
            start.ArgumentList.Add("-o"); start.ArgumentList.Add(outputPath);
            start.ArgumentList.Add("-of"); start.ArgumentList.Add("XML");
            start.ArgumentList.Add("-y");
            if (etlFile.Length <= 0 || etlFile.Length > MaximumXmlBytes)
                return new(false, null, "EtwInputTooLarge");
            var result = BoundedDiagnosticProcess.Run(start, 30_000, 32 * 1024, outputPath, MaximumXmlBytes);
            if (result.ReasonCode != "Success") return new(false, null, "Tracerpt" + result.ReasonCode);
            if (result.ExitCode != 0 || !File.Exists(outputPath)) return new(false, null, "TracerptFailed");

            var outputInfo = new FileInfo(outputPath);
            if (outputInfo.Length <= 0 || outputInfo.Length > MaximumXmlBytes) return new(false, null, "EtwXmlTooLarge");
            XDocument source = XDocument.Load(outputPath, LoadOptions.None);
            XElement[] events = source.Root?.DescendantsAndSelf()
                .Where(element => element.Name.LocalName == "Event")
                .Select(NormalizeCurtainEvent)
                .Where(element => element is not null)
                .Cast<XElement>()
                .Take(100_000)
                .ToArray() ?? [];
            if (events.Length == 0) return new(false, null, "NoCurtainProviderEvents");
            return new(true, new XElement("CurtainEtwEvents", events).ToString(SaveOptions.DisableFormatting), "Success");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or System.ComponentModel.Win32Exception or
                                   System.Security.SecurityException or System.Xml.XmlException)
        {
            CurtainDiagnostics.WriteException("diagnostic.etw-export.failed", ex,
                stage: "tracerpt", reasonCode: "EtwXmlExportFailed");
            return new(false, null, ex.GetType().Name);
        }
        finally
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static XElement? NormalizeCurtainEvent(XElement source)
    {
        XElement? system = Child(source, "System");
        XElement? provider = Child(system, "Provider");
        string providerName = provider?.Attribute("Name")?.Value ?? string.Empty;
        string? providerGuidText = provider?.Attribute("Guid")?.Value;
        bool isCurtain = providerName.Equals("Curtain-Privacy", StringComparison.OrdinalIgnoreCase) ||
                         Guid.TryParse(providerGuidText, out Guid providerGuid) &&
                         providerGuid == Guid.Parse(CurtainEtwSessionController.ProviderGuid);
        if (!isCurtain) return null;
        if (!uint.TryParse(Child(system, "EventID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint eventId) ||
            eventId is not (1 or 2)) return null;

        string timestamp = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value ?? string.Empty;
        XElement normalized = new("Event", new XAttribute("id", eventId));
        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time))
            normalized.Add(new XAttribute("timeUtc", time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        AddUInt(normalized, "processId", Child(system, "Execution")?.Attribute("ProcessID")?.Value);
        AddUInt(normalized, "threadId", Child(system, "Execution")?.Attribute("ThreadID")?.Value);

        Dictionary<string, string> data = source.Descendants()
            .Where(element => element.Name.LocalName == "Data")
            .Select(element => (Name: element.Attribute("Name")?.Value, Value: element.Value.Trim()))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Name))
            .GroupBy(pair => pair.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

        AddSafeCode(normalized, data, "eventName", "eventName");
        AddSafeCode(normalized, data, "level", "level");
        AddSafeCode(normalized, data, "operationId", "operationId");
        AddSafeCode(normalized, data, "stage", "stage");
        AddSafeCode(normalized, data, "reasonCode", "reasonCode");
        AddSafeCode(normalized, data, "processName", "processName");
        AddSafeCode(normalized, data, "sessionId", "sessionId");
        AddSafeCode(normalized, data, "ruleId", "ruleId");
        foreach (string field in new[] { "productVersion", "fileVersion", "gitCommit", "buildId", "appSessionId" })
            AddSafeCode(normalized, data, field, field);
        AddInt64(normalized, data, "applicationProcessId", "applicationProcessId");
        if (data.TryGetValue("appSessionStartedAtUtc", out string? sessionTime) &&
            DateTimeOffset.TryParse(sessionTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started))
            normalized.SetAttributeValue("appSessionStartedAtUtc", started.ToUniversalTime().ToString("O"));
        AddSafeCode(normalized, data, "state", "state");
        AddInt64(normalized, data, "hwnd", "hwnd");
        AddInt64(normalized, data, "windowFromPointHwnd", "windowFromPointHwnd");
        AddInt64(normalized, data, "ownerHwnd", "ownerHwnd");
        AddInt64(normalized, data, "relatedHwnd", "relatedHwnd");
        AddInt64(normalized, data, "targetProcessId", "targetProcessId");
        AddInt64(normalized, data, "processId", "targetProcessId");
        AddInt64(normalized, data, "errorCode", "errorCode");
        AddInt64(normalized, data, "resultCode", "resultCode");
        AddInt64(normalized, data, "left", "left");
        AddInt64(normalized, data, "top", "top");
        AddInt64(normalized, data, "width", "width");
        AddInt64(normalized, data, "height", "height");
        AddInt64(normalized, data, "mouseX", "mouseX");
        AddInt64(normalized, data, "mouseY", "mouseY");
        AddSafeCode(normalized, data, "metricName", "metricName");
        AddDouble(normalized, data, "metricValue", "metricValue");
        return normalized;
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static void AddUInt(XElement element, string name, string? value)
    {
        if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint number))
            element.Add(new XAttribute(name, number));
    }

    private static void AddInt64(XElement element, Dictionary<string, string> source, string key, string output)
    {
        if (source.TryGetValue(key, out string? value) && long.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long number))
            element.SetAttributeValue(output, number);
    }

    private static void AddSafeCode(XElement element, Dictionary<string, string> source, string key, string output)
    {
        if (source.TryGetValue(key, out string? value) && SafeCode.IsMatch(value))
            element.SetAttributeValue(output, value);
    }

    private static void AddDouble(XElement element, Dictionary<string, string> source, string key, string output)
    {
        if (source.TryGetValue(key, out string? value) && double.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) && Math.Abs(number) <= 1_000_000)
            element.SetAttributeValue(output, number.ToString("R", CultureInfo.InvariantCulture));
    }
}
