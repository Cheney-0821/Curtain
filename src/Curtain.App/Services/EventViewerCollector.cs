using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Curtain.App.Services;

internal sealed record EventViewerCollectionResult(
    bool Success,
    string? SanitizedXml,
    int MatchingEventCount,
    string ResultCode);

/// <summary>
/// Queries a bounded set of recent application errors, then reconstructs a tiny allow-listed
/// document. The original rendered event payload is never added to the diagnostic archive.
/// </summary>
internal static class EventViewerCollector
{
    private const int MaximumOutputCharacters = 8 * 1024 * 1024;
    private const int MaximumMatchingEvents = 30;
    private static readonly Regex HexCode = new("^0x[0-9a-fA-F]{1,16}$", RegexOptions.Compiled);
    private static readonly Regex Version = new("^[0-9]{1,6}(\\.[0-9]{1,6}){1,3}$", RegexOptions.Compiled);
    private static readonly Regex SafeBaseName = new("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.Compiled);

    internal static EventViewerCollectionResult CollectCurtainApplicationErrors()
    {
        try
        {
            string wevtutil = Path.Combine(Environment.SystemDirectory, "wevtutil.exe");
            const string query = "*[System[(EventID=1000 or EventID=1001 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
            var start = new ProcessStartInfo
            {
                FileName = wevtutil,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("qe");
            start.ArgumentList.Add("Application");
            start.ArgumentList.Add("/q:" + query);
            start.ArgumentList.Add("/f:RenderedXml");
            start.ArgumentList.Add("/c:200");
            start.ArgumentList.Add("/rd:true");

            var result = BoundedDiagnosticProcess.Run(start, 15_000, MaximumOutputCharacters);
            if (result.ReasonCode != "Success") return new(false, null, 0, "Wevtutil" + result.ReasonCode);
            if (result.ExitCode != 0) return new(false, null, 0, "WevtutilQueryFailed");
            string output = result.Output;
            if (string.IsNullOrWhiteSpace(output)) return new(true, null, 0, "NoApplicationErrors");

            int declarationEnd = output.IndexOf("?>", StringComparison.Ordinal);
            if (output.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && declarationEnd >= 0)
                output = output[(declarationEnd + 2)..];
            string trimmed = output.Trim();
            XDocument source = trimmed.StartsWith("<Events", StringComparison.OrdinalIgnoreCase)
                ? XDocument.Parse(trimmed, LoadOptions.None)
                : XDocument.Parse("<Events>" + output + "</Events>", LoadOptions.None);
            XElement[] events = source.Root!.DescendantsAndSelf()
                .Where(element => element.Name.LocalName == "Event")
                .Where(IsCurtainCrash)
                .Take(MaximumMatchingEvents)
                .Select(NormalizeEvent)
                .ToArray();
            if (events.Length == 0) return new(true, null, 0, "NoCurtainApplicationErrors");

            string xml = new XElement("CurtainApplicationErrors", events).ToString(SaveOptions.DisableFormatting);
            return new(true, xml, events.Length, "Success");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or System.ComponentModel.Win32Exception or
                                   System.Xml.XmlException or System.Security.SecurityException)
        {
            CurtainDiagnostics.WriteException("diagnostic.event-viewer.failed", ex,
                stage: "wevtutil", reasonCode: "EventViewerCollectionFailed");
            return new(false, null, 0, ex.GetType().Name);
        }
    }

    private static bool IsCurtainCrash(XElement eventElement)
    {
        XElement? system = Child(eventElement, "System");
        string provider = Child(system, "Provider")?.Attribute("Name")?.Value ?? string.Empty;
        if (provider is not ("Application Error" or "Windows Error Reporting" or ".NET Runtime")) return false;
        if (!int.TryParse(Child(system, "EventID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
            id is not (1000 or 1001 or 1026)) return false;

        return eventElement.Descendants()
            .Where(element => element.Name.LocalName is "Data" or "Binary" or "Name")
            .Any(element => element.Value.Contains("Curtain.App.exe", StringComparison.OrdinalIgnoreCase) ||
                            element.Value.Contains("Curtain.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static XElement NormalizeEvent(XElement source)
    {
        XElement? system = Child(source, "System");
        string provider = Child(system, "Provider")?.Attribute("Name")?.Value ?? "Unknown";
        string eventId = Child(system, "EventID")?.Value ?? "0";
        string timestamp = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value ?? string.Empty;
        string? processId = Child(system, "Execution")?.Attribute("ProcessID")?.Value;
        string? threadId = Child(system, "Execution")?.Attribute("ThreadID")?.Value;

        var normalized = new XElement("Event",
            new XAttribute("provider", provider),
            new XAttribute("eventId", eventId));
        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time))
            normalized.Add(new XAttribute("timeUtc", time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        AddNumericAttribute(normalized, "processId", processId);
        AddNumericAttribute(normalized, "threadId", threadId);

        string? executable = FindExecutableName(source);
        if (executable is not null) normalized.Add(new XAttribute("application", executable));
        foreach (XElement data in source.Descendants().Where(element => element.Name.LocalName == "Data"))
        {
            string name = data.Attribute("Name")?.Value ?? string.Empty;
            string value = data.Value.Trim();
            if (name.Equals("ExceptionCode", StringComparison.OrdinalIgnoreCase) && HexCode.IsMatch(value))
                normalized.Add(new XAttribute("exceptionCode", value.ToUpperInvariant()));
            else if (name.Equals("FaultingModuleName", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("ModuleName", StringComparison.OrdinalIgnoreCase))
            {
                string basename = Path.GetFileName(value.Replace('/', '\\'));
                if (SafeBaseName.IsMatch(basename)) normalized.Add(new XAttribute("faultingModule", basename));
            }
            else if (name.Equals("AppVersion", StringComparison.OrdinalIgnoreCase) && Version.IsMatch(value))
                normalized.Add(new XAttribute("applicationVersion", value));
        }
        return normalized;
    }

    private static string? FindExecutableName(XElement source)
    {
        foreach (XElement value in source.Descendants())
        {
            if (value.Value.Contains("Curtain.App.exe", StringComparison.OrdinalIgnoreCase)) return "Curtain.App.exe";
            if (value.Value.Contains("Curtain.exe", StringComparison.OrdinalIgnoreCase)) return "Curtain.exe";
        }
        return null;
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static void AddNumericAttribute(XElement element, string name, string? value)
    {
        if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint number))
            element.Add(new XAttribute(name, number));
    }
}
