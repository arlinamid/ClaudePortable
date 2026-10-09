using ClaudePortable.App.Localization;
using ClaudePortable.Core.Archive;

namespace ClaudePortable.App.Ui.ViewModels;

/// <summary>One "include this agent" checkbox (see SourceGroups).</summary>
public sealed class GroupToggle : ViewModelBase
{
    private readonly Action? _changed;
    private bool _isChecked;

    public GroupToggle(string id, bool isChecked, Action? changed = null)
    {
        Id = id;
        _isChecked = isChecked;
        _changed = changed;
    }

    public string Id { get; }

    public string Label => GroupLabels.For(Id);

    public string Hint => GroupLabels.HintFor(Id);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetField(ref _isChecked, value))
            {
                _changed?.Invoke();
            }
        }
    }

    public void RefreshLanguage()
    {
        Raise(nameof(Label));
        Raise(nameof(Hint));
    }

    /// <summary>Selection for the engines: null when every group is ticked.</summary>
    public static IReadOnlySet<string>? ToSelection(IEnumerable<GroupToggle> toggles)
    {
        var list = toggles.ToList();
        var ticked = list.Where(t => t.IsChecked).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ticked.Count == list.Count ? null : ticked;
    }
}

/// <summary>Localized display names for source groups and discovery keys.</summary>
public static class GroupLabels
{
    public static string For(string groupId) => groupId switch
    {
        SourceGroups.ClaudeDesktop => Loc.T("Group_ClaudeDesktop"),
        SourceGroups.Cowork => Loc.T("Group_Cowork"),
        SourceGroups.ClaudeCode => Loc.T("Group_ClaudeCode"),
        SourceGroups.Codex => Loc.T("Group_Codex"),
        _ => groupId,
    };

    public static string HintFor(string groupId) => groupId switch
    {
        SourceGroups.ClaudeDesktop => Loc.T("Group_ClaudeDesktop_Hint"),
        SourceGroups.Cowork => Loc.T("Group_Cowork_Hint"),
        SourceGroups.ClaudeCode => Loc.T("Group_ClaudeCode_Hint"),
        SourceGroups.Codex => Loc.T("Group_Codex_Hint"),
        _ => string.Empty,
    };

    public static string ForSourceKey(string key) => key switch
    {
        "claudeDesktopAppData" => Loc.T("Source_ClaudeDesktopAppData"),
        "claudeDesktopLocalAppData" => Loc.T("Source_ClaudeDesktopLocalAppData"),
        "claudeCodeUserProfile" => Loc.T("Source_ClaudeCode"),
        "codexUserProfile" => Loc.T("Source_Codex"),
        "codexDesktopAppData" => Loc.T("Source_CodexApp"),
        "agentsUserProfile" => Loc.T("Source_Agents"),
        _ => key,
    };

    public static string Join(IEnumerable<string> groupIds) => string.Join(", ", groupIds.Select(For));
}
