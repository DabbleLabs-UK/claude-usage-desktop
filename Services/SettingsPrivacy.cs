using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public static class SettingsPrivacy
{
    public static AppSettings Redact(AppSettings settings) => settings with
    {
        ClaudeCodeOauthToken = null,
        UsageRelaySources = settings.UsageRelaySources?.Select(s => s with { AccessToken = "" }).ToArray(),
    };
}
