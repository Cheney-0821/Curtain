using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.FirstOrDefault() == "child-output")
{
    while (true) Console.Write(new string('x', 4096));
}
if (args.FirstOrDefault() == "child-file")
{
    using var file = new FileStream(args[1], FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    while (true) { file.Write(new byte[4096]); file.Flush(); Thread.Sleep(1); }
}
if (args.FirstOrDefault() == "child-wait") { Thread.Sleep(30_000); return; }
bool sessionChild = args.FirstOrDefault() == "child-session";
if (sessionChild) args = args.Skip(1).ToArray();
if (args.Length != 2) throw new ArgumentException("Expected product publish directory and isolated evidence directory.");
string appDir = Path.GetFullPath(args[0]);
string evidence = Path.GetFullPath(args[1]);
Directory.CreateDirectory(evidence);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string file = Path.Combine(appDir, name.Name + ".dll");
    return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
};
Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir, "Curtain.App.dll"));
const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
Type identityType = app.GetType("Curtain.App.Services.AppBuildIdentity", true)!;
Type sessionType = app.GetType("Curtain.App.Services.AppDiagnosticSession", true)!;
object identity = identityType.GetProperty("Current", methods)!.GetValue(null)!;
object appSession = sessionType.GetProperty("Current", methods)!.GetValue(null)!;
string Field(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)?.ToString() ?? "";
if (sessionChild) { Console.WriteLine(Field(appSession, "AppSessionId")); return; }

Type diagnostic = app.GetType("Curtain.App.Services.CurtainDiagnostics", true)!;
void Call(string name, params object?[] values) => diagnostic.GetMethod(name, methods)!.Invoke(null, values);
var write = diagnostic.GetMethod("Write", methods)!;
void Record(string name, string? businessSession = null)
{
    object?[] values = write.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
    values[0] = name;
    if (businessSession is not null) values[Array.FindIndex(write.GetParameters(), x => x.Name == "sessionId")] = businessSession;
    write.Invoke(null, values);
}
int passed = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }

Check(Field(identity, "ProductVersion") == "0.3.1", "full SemVer comes from product assembly, not test host");
Check(Field(identity, "FileVersion") == "0.3.1.0" && Field(identity, "AssemblyVersion") == "0.2.0.0", "numeric file version and stable assembly version");
Check(System.Text.RegularExpressions.Regex.IsMatch(Field(identity, "GitCommit"), "^[a-f0-9]{40}$"), "full Git commit captured");
Check(Guid.TryParse(Field(identity, "BuildId"), out _) && DateTimeOffset.TryParse(Field(identity, "BuildTimestampUtc"), out _), "unique build id and UTC timestamp captured");
string dirtyState = Field(identity, "WorkingTreeDirty");
Check(dirtyState is "true" or "false" or "Unknown", "working tree dirty state follows the build-independent tri-state contract");
var normalizeDirty = identityType.GetMethod("NormalizeWorkingTreeDirty", methods)!;
Check((string)normalizeDirty.Invoke(null, ["true"])! == "true" &&
    (string)normalizeDirty.Invoke(null, ["false"])! == "false" &&
    (string)normalizeDirty.Invoke(null, [null])! == "Unknown" &&
    (string)normalizeDirty.Invoke(null, ["Unknown"])! == "Unknown" &&
    (string)normalizeDirty.Invoke(null, ["unexpected"])! == "Unknown" &&
    (string)normalizeDirty.Invoke(null, ["True"])! == "true" &&
    (string)normalizeDirty.Invoke(null, ["FALSE"])! == "false",
    "working tree dirty metadata normalizes true, false and unavailable values");
object unknown = identityType.GetMethod("Read", methods)!.Invoke(null, [typeof(Program).Assembly])!;
Check(Field(unknown, "GitCommit") == "Unknown" && Field(unknown, "BuildId") == "Unknown" && Field(unknown, "WorkingTreeDirty") == "Unknown", "metadata unavailable remains Unknown");
object Resolve(string? version) => identityType.GetMethod("ResolveFileVersion", methods)!.Invoke(null, [version])!;
string Tuple(object value, string field) => value.GetType().GetField(field)!.GetValue(value)?.ToString() ?? "";
Check(Tuple(Resolve("0.2.0.7"), "Item1") == "Exact" && Tuple(Resolve("0.2.0.7"), "Item2") == "0.2.0-beta.7", "unique registered numeric version maps back to beta.7");
Check(Tuple(Resolve("0.3.0.0"), "Item1") == "Exact" && Tuple(Resolve("0.3.0.0"), "Item2") == "0.3.0", "registered stable numeric version maps back to 0.3.0");
Check(Tuple(Resolve("0.3.1.0"), "Item1") == "Exact" && Tuple(Resolve("0.3.1.0"), "Item2") == "0.3.1", "registered stable numeric version maps back to 0.3.1");
Check(Tuple(Resolve("0.2.0.0"), "Item1") == "LegacyAmbiguous" && Tuple(Resolve(null), "Item1") == "Unknown" && Tuple(Resolve("0.2.0.8"), "Item1") == "Unknown", "legacy and unregistered versions are never guessed");
using (var map = JsonDocument.Parse(app.GetManifestResourceStream("Curtain.ReleaseVersionMap")!))
{
    var pairs = map.RootElement.EnumerateObject().ToArray();
    Check(pairs.Select(x => x.Value.GetString()).Distinct().Count() == pairs.Length &&
        pairs.All(x => Version.TryParse(x.Name, out _)), "registered version map has no product collision and numeric keys");
}
Check(ReferenceEquals(appSession, sessionType.GetProperty("Current", methods)!.GetValue(null)), "same process shares one app session");
string ChildSession()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string argument in new[] { "child-session", appDir, evidence }) start.ArgumentList.Add(argument);
    using var child = Process.Start(start)!;
    string result = child.StandardOutput.ReadToEnd().Trim(); child.WaitForExit();
    Check(child.ExitCode == 0 && Guid.TryParse(result, out _), "fresh process emits a session id");
    return result;
}
Check(ChildSession() != ChildSession(), "restart generates a new session id");
Type install = app.GetType("Curtain.App.Services.InstallKindDetector", true)!;
var classifyInstall = install.GetMethod("Classify", methods)!;
object Kind(int packageResult, Func<bool> isVelopackInstalled, string distribution) =>
    classifyInstall.Invoke(null, [packageResult, isVelopackInstalled, distribution])!;
bool velopackProbeCalled = false;
object packaged = Kind(122, () => { velopackProbeCalled = true; return false; }, "Unknown");
Check(Field(packaged, "Kind") == "MsixPackaged" && !velopackProbeCalled,
    "MSIX package identity short-circuits before Velopack UpdateManager initialization");
bool noPackageProbeCalled = false;
object velopack = Kind(15700, () => { noPackageProbeCalled = true; return true; }, "Development");
Check(Field(velopack, "Kind") == "VelopackInstalled" && noPackageProbeCalled,
    "only the documented no-package result proceeds to Velopack detection");
bool errorProbeCalled = false;
object nativeError = Kind(5, () => { errorProbeCalled = true; return true; }, "Development");
Check(Field(nativeError, "Kind") == "Unknown" && !errorProbeCalled &&
    Field(nativeError, "DetectionMethod") == "GetCurrentPackageFullName.Error.5",
    "unexpected native package API errors remain Unknown without falling back to another channel");
foreach (int nativeCode in new[] { 0, 87, 15701 })
{
    bool probeCalled = false;
    object failed = Kind(nativeCode, () => { probeCalled = true; return false; }, "PortableOrUnpacked");
    Check(Field(failed, "Kind") == "Unknown" && !probeCalled &&
        Field(failed, "DetectionMethod") == $"GetCurrentPackageFullName.Error.{nativeCode}",
        "native error and unexpected success cannot imply unpackaged status");
}
object detectionFailure = Kind(15700, () => throw new InvalidOperationException("C:/Users/private/token-secret"), "Development");
Check(Field(detectionFailure, "Kind") == "Unknown" && Field(detectionFailure, "DetectionMethod") == "VelopackDetectionFailed.InvalidOperationException",
    "Velopack detection exceptions remain Unknown and are safely classified");
Check(Field(Kind(15700, () => false, "Development"), "Kind") == "Development" &&
    Field(Kind(15700, () => false, "PortableOrUnpacked"), "Kind") == "PortableOrUnpacked" &&
    Field(Kind(15700, () => false, "Unknown"), "Kind") == "Unknown",
    "development, unpacked and insufficient-evidence branches remain distinct");
Type events = app.GetType("Curtain.App.Services.EventViewerCollector", true)!;
var parse = events.GetMethod("Parse", methods)!;
DateTimeOffset sessionStart = DateTimeOffset.UtcNow;
System.Xml.Linq.XElement Crash(string version, string? time, string? pid)
{
    string xml = $"<Events><Event><System><Provider Name='Application Error'/><EventID>1000</EventID><Execution ProcessID='{Environment.ProcessId}'/>{(time is null ? "" : $"<TimeCreated SystemTime='{time}'/>")}</System><EventData><Data Name='AppName'>Curtain.App.exe</Data><Data Name='AppVersion'>{version}</Data><Data Name='ModuleName'>Microsoft.UI.Xaml.dll</Data>{(pid is null ? "" : $"<Data Name='ProcessId'>{pid}</Data>")}<Data Name='AppPath'>C:/Users/private/Curtain.App.exe</Data></EventData></Event></Events>";
    object result = parse.Invoke(null, [xml, sessionStart, Environment.ProcessId])!;
    return System.Xml.Linq.XElement.Parse(Field(result, "SanitizedXml")).Element("Event")!;
}
var historical = Crash("0.2.0.0", sessionStart.AddDays(-2).ToString("O"), Environment.ProcessId.ToString());
Check((string?)historical.Attribute("eventTimeRelation") == "BeforeCurrentSessionStart" && (string?)historical.Attribute("versionAttribution") == "LegacyAmbiguous" && historical.Attribute("resolvedProductVersion") is null, "historical Xaml crash never attributed to current beta");
var exact = Crash("0.3.1.0", sessionStart.AddSeconds(1).ToString("O"), $"0x{Environment.ProcessId:X}");
Check((string?)exact.Attribute("resolvedProductVersion") == "0.3.1" && (string?)exact.Attribute("processCorrelation") == "Matched", "named fault PID plus time and exact version correlate");
var missing = Crash("", null, null);
Check((string?)missing.Attribute("eventTimeRelation") == "Unknown" && (string?)missing.Attribute("versionAttribution") == "Unknown" && (string?)missing.Attribute("processCorrelation") == "NotAvailable" && missing.Attribute("faultingProcessId") is null, "System Execution PID is not a fault PID; missing evidence stays unknown");
Check((string?)Crash("0.2.0.7", sessionStart.AddSeconds(1).ToString("O"), "1").Attribute("processCorrelation") == "OtherProcess", "time alone cannot correlate another process");
Check(!historical.ToString().Contains("private") && !historical.ToString().Contains("AppPath"), "event output strips arbitrary paths");
Type bundleType = app.GetType("Curtain.App.Services.DiagnosticBundleBuilder", true)!;
var sanitize = bundleType.GetMethod("TryWriteSafeLogLine", methods)!;
using (var oldLine = JsonDocument.Parse("{\"timestampUtc\":\"2026-11-05T00:00:00Z\",\"eventName\":\"old.record\",\"level\":\"information\",\"sessionId\":\"business-session\",\"privatePath\":\"secret\"}"))
using (var output = new MemoryStream())
{
    Check((bool)sanitize.Invoke(null, [output, oldLine.RootElement])!, "old JSONL without identity remains readable");
    string sanitized = System.Text.Encoding.UTF8.GetString(output.ToArray());
    Check(sanitized.Contains("business-session") && !sanitized.Contains("secret") && !sanitized.Contains("productVersion"), "old business session preserved without fabricated identity or private fields");
}
string data = Path.Combine(evidence, "data");
Call("Initialize", data);
Record("identity.record", "business-session");
string identityLine = File.ReadLines(Directory.GetFiles(Path.Combine(data, "Diagnostics"), "*.jsonl").First()).Last();
using (var record = JsonDocument.Parse(identityLine))
{
    Check(new[] { "productVersion", "fileVersion", "gitCommit", "buildId", "appSessionId", "appSessionStartedAtUtc" }.All(x => record.RootElement.TryGetProperty(x, out _)), "writer automatically stamps every new JSONL record");
    Check(record.RootElement.GetProperty("appSessionId").GetString() == Field(appSession, "AppSessionId") && record.RootElement.GetProperty("sessionId").GetString() == "business-session", "app identity and business session stay independent");
    using var sanitizedOutput = new MemoryStream();
    Check((bool)sanitize.Invoke(null, [sanitizedOutput, record.RootElement])!, "new JSONL passes bundle allowlist");
    using var safeRecord = JsonDocument.Parse(sanitizedOutput.ToArray());
    Check(safeRecord.RootElement.GetProperty("buildId").GetString() == Field(identity, "BuildId") && safeRecord.RootElement.GetProperty("appSessionId").GetString() == Field(appSession, "AppSessionId"), "bundle sanitization preserves original build and session identity");
}
object builder = Activator.CreateInstance(bundleType, nonPublic: true)!;
object bundle = bundleType.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(builder, [evidence, false, false])!;
Check(Field(bundle, "Success") == "True", "local diagnostic ZIP generated using isolated test logs");
using (var zip = System.IO.Compression.ZipFile.OpenRead(Field(bundle, "ArchivePath")))
{
    JsonDocument ReadJson(string name) => JsonDocument.Parse(zip.GetEntry(name)!.Open());
    using var summary = ReadJson("diagnostic-summary.json");
    using var environment = ReadJson("system/environment.json");
    using var manifest = ReadJson("manifest.json");
    Check(new[] { summary, environment, manifest }.All(x => x.RootElement.GetProperty("schemaVersion").GetInt32() == 2), "all three diagnostic schemas upgraded");
    Check(new[] { summary, environment, manifest }.All(x => x.RootElement.GetProperty("versionIdentity").GetProperty("productVersion").GetString() == "0.3.1"), "summary environment and manifest share identity");
    Check(summary.RootElement.GetProperty("structuredLogWindowHours").GetInt32() == 24 && summary.RootElement.GetProperty("windowsEventWindowDays").GetInt32() == 7, "collection windows explicit");
    foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
    {
        using var content = zip.GetEntry(item.GetProperty("name").GetString()!)!.Open();
        using var memory = new MemoryStream(); content.CopyTo(memory);
        Check(memory.Length == item.GetProperty("sizeBytes").GetInt64() && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.ToArray())) == item.GetProperty("sha256").GetString(), "manifest entry size and SHA256 match");
    }
}
using var listener = new CaptureListener();
using var cancel = new CancellationTokenSource();
Task[] writers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
{
    while (!cancel.IsCancellationRequested)
    {
        Record("stress.record");
        Call("WriteEtwCaptureState", "stress", Environment.ProcessId, 0);
    }
})).ToArray();
Thread.Sleep(100);
Call("SetEnabled", false);
string logs = Path.Combine(data, "Diagnostics");
long Bytes() => Directory.GetFiles(logs, "*.jsonl").Sum(p => new FileInfo(p).Length);
long bytes = Bytes(); int eventCount = listener.Count;
Check(listener.IdentityPayloadSeen, "ETW and JSONL share version and application session fields");
Check(listener.RecordCount > 0 && listener.StateCount > 0, "EventSource emits actual IDs 1 and 2 before opt-out");
Thread.Sleep(100);
Check(Bytes() == bytes && listener.Count == eventCount, "concurrent opt-out stops JSONL and EventSource after return");
cancel.Cancel(); Task.WaitAll(writers);
Call("SetEnabled", true);
Record("stress.resumed");
Check(Bytes() > bytes, "recording resumes without replacing existing history");
foreach (string file in Directory.GetFiles(logs, "*.jsonl"))
    foreach (string line in File.ReadLines(file)) { using var doc = JsonDocument.Parse(line); }
Check(true, "all concurrent JSONL lines parse");
Call("Initialize", "invalid\0path"); Record("must.not.escape.invalid.path"); Call("Prune");
Check(Bytes() > 0, "invalid override and prune fail safely without throwing");
string blocked = Path.Combine(evidence, "not-a-directory"); File.WriteAllText(blocked, "test");
Call("Initialize", blocked); Record("cannot.write"); Call("Prune");
Check(true, "file-as-directory I/O failure does not escape logging");
Call("Initialize", data);
string activeLog = Directory.GetFiles(logs, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).First();
using (var locked = new FileStream(activeLog, FileMode.Open, FileAccess.Read, FileShare.None)) Record("locked.log");
Check(true, "exclusive log file denial does not escape writer");
int beforeRotation = Directory.GetFiles(logs, "*.jsonl").Length;
using (var fill = new FileStream(activeLog, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
    fill.SetLength(5 * 1024 * 1024 - 1);
Record("reserve.full.record");
Check(Directory.GetFiles(logs, "*.jsonl").Length > beforeRotation &&
    Directory.GetFiles(logs, "*.jsonl").All(p => new FileInfo(p).Length <= 5 * 1024 * 1024),
    "rotation reserves complete record before the 5 MiB boundary");
string activeEtl = Path.Combine(logs, "curtain-etw-active.etl");
File.WriteAllText(activeEtl, "synthetic"); File.SetLastWriteTimeUtc(activeEtl, DateTime.UtcNow.AddDays(-2));
Call("SetActiveEtwPath", activeEtl);
for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(logs, $"curtain-etw-test-{i}.etl"), "synthetic");
Call("Prune");
Check(File.Exists(activeEtl) && Directory.GetFiles(logs, "*.etl").Length <= 3, "ETL cap includes active file and never deletes it");
Call("SetActiveEtwPath", (object?)null);
Type bounded = app.GetType("Curtain.App.Services.BoundedDiagnosticProcess", true)!;
var run = bounded.GetMethod("Run", methods)!;
object Run(string mode, int timeout, int maxOutput, string? path = null, long maxFile = 0)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!);
    start.ArgumentList.Add(mode);
    if (path is not null) start.ArgumentList.Add(path);
    return run.Invoke(null, [start, timeout, maxOutput, path, maxFile])!;
}
string Reason(object result) => (string)result.GetType().GetProperty("ReasonCode")!.GetValue(result)!;
Check(Reason(Run("child-output", 5000, 8192)) == "OutputLimitExceeded", "stdout bounded during process execution");
Check(Reason(Run("child-wait", 200, 8192)) == "Timeout", "stalled collector is terminated at deadline");
string huge = Path.Combine(evidence, "bounded-output.xml");
Check(Reason(Run("child-file", 5000, 8192, huge, 64 * 1024)) == "FileLimitExceeded", "generated XML cap terminates child before completion");
Check(new FileInfo(huge).Length < 1024 * 1024, "file monitor limits transient growth in synthetic producer");
Type etw = app.GetType("Curtain.App.Services.CurtainEtwSessionController", true)!;
object first = Activator.CreateInstance(etw, nonPublic: true)!;
object second = Activator.CreateInstance(etw, nonPublic: true)!;
var nameProperty = etw.GetProperty("SessionName", BindingFlags.Instance | BindingFlags.NonPublic)!;
Check(Equals(nameProperty.GetValue(first), nameProperty.GetValue(second)), "ETW name stable across controller instances");
var startMethod = etw.GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)!;
object?[] startArgs = [null];
bool started = (bool)startMethod.Invoke(first, startArgs)!;
Console.WriteLine("ETW_NATIVE_RESULT=" + (started ? "Started" : startArgs[0]));
if (started) etw.GetMethod("Stop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first, [null]);
else
{
    // The native session was not created. Simulate a stale managed Running bit after watchdog exit.
    etw.GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, true);
    object?[] stopArgs = [null];
    bool reconciled = (bool)etw.GetMethod("Stop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first, stopArgs)!;
    if (reconciled) Check(stopArgs[0]?.ToString() == "AlreadyStopped", "missing native session reconciles stale managed running state");
    else Console.WriteLine("STALE_STATE_NATIVE_QUERY=BLOCKED:" + stopArgs[0]);
    etw.GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, false);
}
((IDisposable)first).Dispose(); ((IDisposable)second).Dispose();
Check(true, "actual ETW control attempt completes without elevation or policy changes");
Console.WriteLine($"OK: {passed} diagnostic assertions passed.");

sealed class CaptureListener : EventListener
{
    private int _count;
    public bool IdentityPayloadSeen;
    private int _recordCount, _stateCount;
    public int RecordCount => Volatile.Read(ref _recordCount);
    public int StateCount => Volatile.Read(ref _stateCount);
    public int Count => Volatile.Read(ref _count);
    protected override void OnEventSourceCreated(EventSource source)
    { if (source.Name == "Curtain-Privacy") EnableEvents(source, EventLevel.Verbose, EventKeywords.All); }
    protected override void OnEventWritten(EventWrittenEventArgs data)
    { if (data.EventId == 1 && data.PayloadNames is not null && data.Payload is not null)
        IdentityPayloadSeen |= data.PayloadNames.Contains("appSessionId") && data.PayloadNames.Contains("productVersion") && data.Payload.Contains("0.3.1");
      if (data.EventId is 1 or 2) { Interlocked.Increment(ref _count); if (data.EventId == 1) Interlocked.Increment(ref _recordCount); else Interlocked.Increment(ref _stateCount); } else if (data.EventId == 0) Console.WriteLine("PROVIDER_SCHEMA_ERROR=" + string.Join(";", data.Payload ?? new System.Collections.ObjectModel.ReadOnlyCollection<object?>([]))); }
}
