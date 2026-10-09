using System.CommandLine;
using ClaudePortable.App.Localization;
using ClaudePortable.Core.Archive;

namespace ClaudePortable.App.Commands;

/// <summary>
/// Shared --include / --skip options selecting source groups
/// (claude-desktop, cowork, claude-code, codex; aliases claude, all).
/// Both accept comma-separated lists or repeated values.
/// </summary>
public static class GroupOptions
{
    public static Option<string[]> CreateInclude() => new(new[] { "--include" }, Loc.T("Cli_Groups_Include"))
    {
        AllowMultipleArgumentsPerToken = true,
    };

    public static Option<string[]> CreateSkip() => new(new[] { "--skip" }, Loc.T("Cli_Groups_Skip"))
    {
        AllowMultipleArgumentsPerToken = true,
    };

    /// <summary>
    /// Resolve the selection, printing a localized error and setting exit
    /// code 1 on invalid input. Returns false when the command must stop.
    /// </summary>
    public static bool TryResolve(string[]? include, string[]? skip, out IReadOnlySet<string>? selection)
    {
        try
        {
            selection = SourceGroups.Resolve(include, skip);
            return true;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(Loc.F("Cli_Groups_Invalid", ex.Message));
            Environment.ExitCode = 1;
            selection = null;
            return false;
        }
    }

    /// <summary>Arguments that reproduce a selection on a `backup` command line (empty for everything).</summary>
    public static string[] ToArguments(IReadOnlySet<string>? selection)
        => selection is null
            ? Array.Empty<string>()
            : ["--include", string.Join(',', SourceGroups.All.Where(selection.Contains))];
}
