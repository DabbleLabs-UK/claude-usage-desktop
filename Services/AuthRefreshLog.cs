using System.Text;

namespace ClaudeUsage.Services;

// Dedicated, append-only diagnostic log for the OAuth token-refresh lifecycle.
//
//   %APPDATA%\ClaudeUsage\auth-refresh.log
//
// Every line is "yyyy-MM-dd HH:mm:ss.fff  <message>" (UTC, millisecond precision).
//
// Why a bespoke logger: the app's ILogger pipeline has NO file or console provider in a
// Release build -- providers are cleared in App.BuildWebApp and only AddDebug() is wired
// under DEBUG -- so every _logger.LogWarning in the refresh path went nowhere in the
// field. This makes the refresh decision points visible on a shipped build.
//
// Writes are best-effort and fully guarded: a logging fault must NEVER disturb polling or
// the refresh it is observing.
//
// SECURITY: callers must never pass raw token material here. Reduce any secret to a
// length/prefix preview (see UsageService) before logging it.
public sealed class AuthRefreshLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsage", "auth-refresh.log");

    private readonly object _writeLock = new();

    public void Log(string message)
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var bytes = Encoding.UTF8.GetBytes($"{stamp}  {message}\n");

            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                using var fs = new FileStream(
                    LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // Diagnostic logging is strictly best-effort; swallow everything.
        }
    }

    // Logs a labelled exception with full detail (type, message, stack trace).
    public void LogException(string context, Exception ex) =>
        Log($"{context} -- EXCEPTION {ex.GetType().FullName}: {ex.Message}\n{ex}");
}
