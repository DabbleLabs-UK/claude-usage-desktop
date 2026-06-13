using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeUsage.Services;

public enum FirewallResult { AlreadyExists, Added, Declined, Error }

public sealed class FirewallService
{
    private const string RulePrefix = "ClaudeUsage-";

    public bool RuleExists(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{RulePrefix}{port}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Elevates via UAC to add the inbound rule for the given port.
    // If oldPort is provided, the old port's rule is removed in the same elevated session.
    public async Task<FirewallResult> TryAddRuleAsync(int port, int? oldPort = null)
    {
        try
        {
            var removeOld = oldPort.HasValue
                ? $"Remove-NetFirewallRule -Name '{RulePrefix}{oldPort.Value}' -ErrorAction SilentlyContinue; "
                : "";
            var cmd = $"{removeOld}" +
                      $"Remove-NetFirewallRule -Name '{RulePrefix}{port}' -ErrorAction SilentlyContinue; " +
                      $"New-NetFirewallRule -DisplayName '{RulePrefix}{port}' -Name '{RulePrefix}{port}' " +
                      $"-Direction Inbound -Protocol TCP -LocalPort {port} -Action Allow -Profile Any";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NonInteractive -WindowStyle Hidden -Command \"{cmd}\"",
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi);
            if (p is null) return FirewallResult.Error;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? FirewallResult.Added : FirewallResult.Error;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return FirewallResult.Declined;
        }
        catch
        {
            return FirewallResult.Error;
        }
    }

    // Elevates via UAC to REMOVE the inbound rule for the given port (inverse of TryAddRuleAsync,
    // reusing the same runas elevation). Returns Added when the rule is gone (removed, or already
    // absent — no prompt is raised in that case), Declined on a UAC refusal, Error otherwise.
    public async Task<FirewallResult> TryRemoveRuleAsync(int port)
    {
        try
        {
            if (!RuleExists(port)) return FirewallResult.Added; // nothing to remove; skip elevation

            var cmd = $"Remove-NetFirewallRule -Name '{RulePrefix}{port}' -ErrorAction SilentlyContinue";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NonInteractive -WindowStyle Hidden -Command \"{cmd}\"",
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi);
            if (p is null) return FirewallResult.Error;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? FirewallResult.Added : FirewallResult.Error;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return FirewallResult.Declined;
        }
        catch
        {
            return FirewallResult.Error;
        }
    }
}
