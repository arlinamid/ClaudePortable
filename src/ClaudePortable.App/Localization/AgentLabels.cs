using System.Runtime.Versioning;
using ClaudePortable.Core.Discovery;

namespace ClaudePortable.App.Localization;

/// <summary>Localized names for running agent processes, for dialogs and CLI output.</summary>
[SupportedOSPlatform("windows")]
public static class AgentLabels
{
    public static string For(AgentProcessKind kind) => kind switch
    {
        AgentProcessKind.ClaudeDesktop => Loc.T("Agent_ClaudeDesktop"),
        AgentProcessKind.ClaudeCodeCli => Loc.T("Agent_ClaudeCodeCli"),
        AgentProcessKind.CodexDesktop => Loc.T("Agent_CodexDesktop"),
        AgentProcessKind.CodexCli => Loc.T("Agent_CodexCli"),
        _ => kind.ToString(),
    };

    /// <summary>"Claude Desktop (PID 1, 2), Codex app (PID 3)".</summary>
    public static string Summarize(IEnumerable<AgentProcess> processes)
        => string.Join(", ", processes
            .GroupBy(p => p.Kind)
            .Select(g => $"{For(g.Key)} (PID {string.Join(", ", g.Where(p => p.IsRoot).DefaultIfEmpty(g.First()).Select(p => p.Process.Pid))})"));
}
