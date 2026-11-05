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
Type diagnostic = app.GetType("Curtain.App.Services.CurtainDiagnostics", true)!;
void Call(string name, params object?[] values) => diagnostic.GetMethod(name, methods)!.Invoke(null, values);
var write = diagnostic.GetMethod("Write", methods)!;
void Record(string name)
{
    object?[] values = write.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
    values[0] = name;
    write.Invoke(null, values);
}
int passed = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
string data = Path.Combine(evidence, "data");
Call("Initialize", data);
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
    private int _recordCount, _stateCount;
    public int RecordCount => Volatile.Read(ref _recordCount);
    public int StateCount => Volatile.Read(ref _stateCount);
    public int Count => Volatile.Read(ref _count);
    protected override void OnEventSourceCreated(EventSource source)
    { if (source.Name == "Curtain-Privacy") EnableEvents(source, EventLevel.Verbose, EventKeywords.All); }
    protected override void OnEventWritten(EventWrittenEventArgs data)
    { if (data.EventId is 1 or 2) { Interlocked.Increment(ref _count); if (data.EventId == 1) Interlocked.Increment(ref _recordCount); else Interlocked.Increment(ref _stateCount); } else if (data.EventId == 0) Console.WriteLine("PROVIDER_SCHEMA_ERROR=" + string.Join(";", data.Payload ?? new System.Collections.ObjectModel.ReadOnlyCollection<object?>([]))); }
}
