namespace ClaudeUsage.Models;

// No account identifier or provider credentials are exposed to desktop/LAN clients.
public record OpenRouterSpendSnapshot(string Status, AgentSpendData? Data);
