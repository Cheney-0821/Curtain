using System.Diagnostics.Tracing;

namespace Curtain.App.Services;

/// <summary>
/// App-specific ETW diagnostic provider. ETW capture is best-effort and subject to Windows
/// trace-session permissions. Payloads are structured diagnostic fields only; never pass titles,
/// paths, arbitrary messages, clipboard data or screen content.
/// </summary>
[EventSource(Name = "Curtain-Privacy", Guid = "a4e7e3f1-6e7d-4c21-9b9c-2e47a4f1b620")]
public sealed class CurtainEventSource : EventSource
{
    internal static readonly CurtainEventSource Log = new();

    private CurtainEventSource() { }

    public static class Keywords
    {
        public const EventKeywords Diagnostics = (EventKeywords)1;
    }

    [Event(1, Version = 2, Level = EventLevel.Informational, Keywords = Keywords.Diagnostics)]
    public void DiagnosticRecord(
        string eventName,
        string level,
        string operationId,
        string stage,
        string reasonCode,
        long hwnd,
        long windowFromPointHwnd,
        long ownerHwnd,
        long relatedHwnd,
        int targetProcessId,
        int errorCode,
        int left,
        int top,
        int width,
        int height,
        int mouseX,
        int mouseY,
        string metricName,
        double metricValue,
        string processName,
        string sessionId,
        string ruleId,
        string productVersion,
        string fileVersion,
        string gitCommit,
        string buildId,
        string appSessionId,
        string appSessionStartedAtUtc,
        int applicationProcessId)
    {
        if (IsEnabled())
            WriteEvent(1, new object?[]
            {
                eventName, level, operationId, stage, reasonCode, hwnd, windowFromPointHwnd,
                ownerHwnd, relatedHwnd, targetProcessId, errorCode,
                left, top, width, height, mouseX, mouseY, metricName, metricValue,
                processName, sessionId, ruleId, productVersion, fileVersion, gitCommit, buildId,
                appSessionId, appSessionStartedAtUtc, applicationProcessId
            });
    }

    [Event(2, Level = EventLevel.Informational, Keywords = Keywords.Diagnostics)]
    public void EtwCaptureState(string state, int processId, int resultCode)
    {
        if (IsEnabled()) WriteEvent(2, state, processId, resultCode);
    }
}
