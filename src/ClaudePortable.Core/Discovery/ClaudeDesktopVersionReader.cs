using System.Runtime.Versioning;

namespace ClaudePortable.Core.Discovery;

/// <summary>
/// Version and edition of the Claude Desktop install in use. Covers the
/// claude.ai download ("Claude" MSIX), the Microsoft Store build
/// ("AnthropicPBC.Claude") and the legacy Squirrel installer; see
/// <see cref="AgentInstalls"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClaudeDesktopVersionReader
{
    public static string? TryRead() => TryReadInstall()?.Version;

    public static AgentInstall? TryReadInstall()
    {
        try
        {
            return AgentInstalls.PrimaryClaudeDesktop();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
