using System.Diagnostics;

namespace BilibiliLiveAssistant.App;

// Rare fault records only. Never persist exception messages, event data or URLs.
internal static class RuntimeErrorLog
{
    private static readonly object Gate = new();
    internal static void Record(string stage, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "runtime-errors.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
                    File.Move(path, path + ".old", overwrite: true);
                var trace = new StackTrace(exception, false).ToString();
                if (trace.Length > 8192) trace = trace[..8192];
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} stage={stage} type={exception.GetType().FullName} HRESULT=0x{exception.HResult:X8}\n{trace}\n");
            }
        }
        catch { /* Logging must not prevent window cleanup or application exit. */ }
    }
}
