using System.Diagnostics;
using System.Text.Json;
namespace Curtain.App.Services;

internal static class DevelopmentDiagnostics
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Curtain", "Diagnostics");
    private static readonly string LogPath = Path.Combine(DirectoryPath,
        $"b1-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
    [Conditional("DEBUG")]
    internal static void Write(string kind, object data)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(new
                { time = DateTimeOffset.Now, kind, data }) + Environment.NewLine);
        }
        catch (IOException ex) { Debug.WriteLine(ex.Message); }
        catch (UnauthorizedAccessException ex) { Debug.WriteLine(ex.Message); }
    }
}
