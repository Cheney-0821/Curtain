using System.Diagnostics;
using System.Text;

namespace Curtain.App.Services;

internal sealed record BoundedProcessResult(int ExitCode, string Output, string ReasonCode);

internal static class BoundedDiagnosticProcess
{
    internal static BoundedProcessResult Run(ProcessStartInfo start, int timeoutMilliseconds,
        int maximumOutputBytes, string? outputFile = null, long maximumFileBytes = 0)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start);
        if (process is null) return new(-1, string.Empty, "ProcessUnavailable");
        int outputExceeded = 0;
        async Task<byte[]> ReadBounded(Stream stream, int limit)
        {
            using var result = new MemoryStream();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                int remaining = limit - (int)result.Length;
                if (read > remaining)
                {
                    Interlocked.Exchange(ref outputExceeded, 1);
                    break;
                }
                result.Write(buffer, 0, read);
            }
            return result.ToArray();
        }
        var stdout = ReadBounded(process.StandardOutput.BaseStream, maximumOutputBytes);
        var stderr = ReadBounded(process.StandardError.BaseStream, 32 * 1024);
        var clock = Stopwatch.StartNew();
        string reason = "Success";
        bool FileExceeded() => outputFile is not null && File.Exists(outputFile) &&
            new FileInfo(outputFile).Length >= maximumFileBytes;
        try
        {
            while (true)
            {
                bool exited = process.WaitForExit(20);
                if (Volatile.Read(ref outputExceeded) != 0) reason = "OutputLimitExceeded";
                else if (FileExceeded()) reason = "FileLimitExceeded";
                else if (!exited && clock.ElapsedMilliseconds >= timeoutMilliseconds) reason = "Timeout";
                if (exited || reason != "Success") break;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        if (!Task.WhenAll(stdout, stderr).Wait(2000)) return new(-1, string.Empty, "PipeDrainTimeout");
        if (Volatile.Read(ref outputExceeded) != 0) reason = "OutputLimitExceeded";
        return new(process.ExitCode, reason == "Success" ? Encoding.UTF8.GetString(stdout.Result) : string.Empty, reason);
    }
}
