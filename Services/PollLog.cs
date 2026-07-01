using System.Globalization;
using System.Text;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Dedicated, append-only diagnostic log for EVERY usage poll -- the evidence trail for the
// intermittent "false zero" (a window reading 0% / "no usage yet" mid-period when it shouldn't).
//
//   %APPDATA%\ClaudeUsage\poll.log        (current)
//   %APPDATA%\ClaudeUsage\poll.log.1      (one rotated backup)
//
// Style mirrors AuthRefreshLog: every line is "yyyy-MM-dd HH:mm:ss.fff  <message>" (UTC, ms).
// Unlike AuthRefreshLog this ROTATES: when the live file reaches MaxBytes it is moved to
// poll.log.1 (overwriting any previous backup) and a fresh file is started, so on-disk size is
// bounded at ~2 * MaxBytes regardless of how long the app runs.
//
// On EVERY poll we record one summary line: outcome, isStale, token source, and each window's
// utilization + resetsAt exactly as parsed from the API. On a SUSPICIOUS success (a window at 0%,
// or a sharp drop from the previous sample) we ALSO dump the raw usage JSON on the next line, so a
// false zero is captured in full the moment it happens and never needs to be caught live again.
//
// SAFETY: the usage-endpoint body carries no token material (only utilization/resets), so dumping
// it raw is safe -- unlike the auth log, no redaction is needed. Writes are best-effort and fully
// guarded: a logging fault must NEVER disturb polling.
public sealed class PollLog
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsage");
    private static readonly string LogPath    = Path.Combine(Dir, "poll.log");
    private static readonly string BackupPath = Path.Combine(Dir, "poll.log.1");

    // Rotation cap: 1 MB current + 1 MB backup = ~2 MB on-disk ceiling.
    private const long MaxBytes = 1 * 1024 * 1024;

    // "Sharp drop" = utilization fell by at least this many percentage points versus the previous
    // logged sample for the same window. Combined with resetsAt to tell a genuine rollover (resetsAt
    // advanced) from a suspect crater (resetsAt unchanged).
    private const double SharpDropPct = 15.0;

    // A window is treated as "zero" at or below this (guards float noise around 0).
    private const double ZeroEps = 0.0001;

    private readonly object _lock = new();

    // Previous logged utilization + resetsAt per window key, for drop/rollover detection. Guarded
    // by _lock (only ever touched inside LogSuccess, which the single poller calls sequentially).
    private readonly Dictionary<string, (double Util, string? Resets)> _last = new(StringComparer.Ordinal);

    // Logs one successful poll: the per-window summary always, plus the raw JSON when suspect.
    public void LogSuccess(string tokenSource, UsageData data, string rawJson)
    {
        try
        {
            var sb = new StringBuilder();
            var suspectReasons = new List<string>();

            // Deterministic key order so successive lines line up for eyeballing.
            var keys = data.Windows.Keys.ToList();
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var w = data.Windows[key];
                if (w is null)
                {
                    sb.Append(' ').Append(key).Append("=<null>");
                    continue;
                }

                var util   = w.Utilization;
                var resets = w.ResetsAt?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                                CultureInfo.InvariantCulture);
                sb.Append(' ').Append(key).Append('=')
                  .Append(util.ToString("0.##", CultureInfo.InvariantCulture)).Append('%')
                  .Append('@').Append(resets ?? "no-reset");

                var isZero = util <= ZeroEps;
                if (_last.TryGetValue(key, out var prev))
                {
                    var drop           = prev.Util - util;
                    var resetsAdvanced = resets is not null && prev.Resets is not null && resets != prev.Resets;
                    // A crater whose resetsAt did NOT advance is the prime false-zero signature: the
                    // period did not roll over, yet usage vanished. Flag zero-transitions and sharp
                    // drops; annotate whether resetsAt moved so a genuine reset reads differently.
                    if (isZero && prev.Util > ZeroEps)
                        suspectReasons.Add($"{key} ->0% (was {prev.Util:0.##}%; resetsAt {(resetsAdvanced ? "ADVANCED=genuine-reset?" : "UNCHANGED=suspect")})");
                    else if (drop >= SharpDropPct)
                        suspectReasons.Add($"{key} drop {prev.Util:0.##}%->{util:0.##}% ({drop:0.##}pp; resetsAt {(resetsAdvanced ? "advanced" : "UNCHANGED=suspect")})");
                }
                else if (isZero)
                {
                    // First sample this run and already zero -- no baseline to compare, capture anyway.
                    suspectReasons.Add($"{key} 0% (no prior sample this run)");
                }

                _last[key] = (util, resets);
            }

            var extra = data.ExtraUsage is { } eu
                ? eu.Utilization.ToString("0.##", CultureInfo.InvariantCulture) + "%"
                : "off";

            var line = $"POLL ok stale={data.IsStale} src=\"{tokenSource}\" " +
                       $"fetchedAt={data.FetchedAt.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'} " +
                       $"windows:{sb} extra_usage={extra}";

            if (suspectReasons.Count > 0)
                line += "  [SUSPECT: " + string.Join("; ", suspectReasons) + "]";

            Write(line);

            // Raw dump only on a suspect poll -- bounded, and only when something looked wrong, so the
            // steady state stays one line per poll while a false zero is captured in full.
            if (suspectReasons.Count > 0)
            {
                var raw = rawJson.Length > 4000 ? rawJson[..4000] + "...(truncated)" : rawJson;
                Write("POLL RAW: " + raw.Replace("\r", "").Replace("\n", " "));
            }
        }
        catch
        {
            // Diagnostic logging is strictly best-effort; swallow everything.
        }
    }

    // Logs one failed/skipped poll (network down, 401, 429, refresh-rate-limited, offline, other).
    // These are always the stale path -- there is no fresh API content to record.
    public void LogFailure(string reason)
    {
        try { Write($"POLL fail stale=True: {reason}"); }
        catch { /* best-effort */ }
    }

    private void Write(string message)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetBytes($"{stamp}  {message}\n");

        lock (_lock)
        {
            Directory.CreateDirectory(Dir);

            // Rotate BEFORE writing when the live file has reached the cap, so the ceiling holds.
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaxBytes)
                {
                    if (File.Exists(BackupPath)) File.Delete(BackupPath);
                    File.Move(LogPath, BackupPath);
                }
            }
            catch { /* if rotation fails, fall through and keep appending -- data over tidiness */ }

            using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(bytes, 0, bytes.Length);
        }
    }
}
