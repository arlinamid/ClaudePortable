using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ClaudePortable.Core.Discovery;

public enum AgentApp
{
    ClaudeDesktop,
    CodexDesktop,
}

/// <summary>
/// One installed copy of a desktop agent app.
/// </summary>
/// <param name="Edition">
/// How it was installed: "direct" (MSIX from the vendor's website, signed by
/// the vendor), "store" (Microsoft Store, Store-signed) or "squirrel" (the
/// legacy per-user Claude installer). Version numbers are only comparable
/// within one edition: the Store build of Claude uses its own numbering.
/// </param>
/// <param name="InstallRoot">Folder the app's binaries live in; processes started from below it belong to the app.</param>
/// <param name="PackageFamilyName">MSIX package family (e.g. Claude_pzs8sxrjxfjjc); null for non-packaged installs.</param>
/// <param name="LaunchTarget">What reopens the app: "shell:AppsFolder\&lt;PFN&gt;!&lt;AppId&gt;" for MSIX, an exe path otherwise.</param>
public sealed record AgentInstall(
    AgentApp App,
    string Edition,
    string Version,
    string InstallRoot,
    string? PackageFamilyName,
    string? LaunchTarget);

/// <summary>
/// Finds every installed Claude Desktop and Codex desktop app, whichever way
/// it was installed. Both vendors ship more than one MSIX package (Claude:
/// "Claude" from claude.ai and "AnthropicPBC.Claude" from the Microsoft
/// Store; Codex: "OpenAI.Codex", whose app is now branded ChatGPT), and
/// older Claude builds used a per-user Squirrel installer.
/// <para>
/// MSIX packages are read from the per-user AppModel repository in HKCU,
/// which lists exactly the packages registered for this user and needs
/// neither admin rights nor a PowerShell round trip.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentInstalls
{
    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static readonly (string PackageName, AgentApp App, string Edition)[] KnownPackages =
    [
        ("Claude", AgentApp.ClaudeDesktop, "direct"),
        ("AnthropicPBC.Claude", AgentApp.ClaudeDesktop, "store"),
        ("OpenAI.Codex", AgentApp.CodexDesktop, "store"),
    ];

    /// <summary>Edition assumed for backups that did not record one: the old reader only saw the "Claude" package.</summary>
    public const string LegacyClaudeEdition = "direct";

    public static IReadOnlyList<AgentInstall> Detect()
    {
        var result = new List<AgentInstall>();
        result.AddRange(DetectPackages());
        if (DetectSquirrelClaude() is { } squirrel)
        {
            result.Add(squirrel);
        }
        return result;
    }

    /// <summary>
    /// The Claude Desktop install whose data is in use: a running one first
    /// (two editions can be installed side by side and share %APPDATA%\Claude),
    /// otherwise the vendor's own build, then the Store build, then Squirrel.
    /// </summary>
    public static AgentInstall? PrimaryClaudeDesktop(IReadOnlyList<AgentInstall>? installs = null)
        => Primary(installs ?? Detect(), AgentApp.ClaudeDesktop);

    public static AgentInstall? PrimaryCodexDesktop(IReadOnlyList<AgentInstall>? installs = null)
        => Primary(installs ?? Detect(), AgentApp.CodexDesktop);

    private static AgentInstall? Primary(IReadOnlyList<AgentInstall> installs, AgentApp app)
    {
        var candidates = installs.Where(i => i.App == app).ToList();
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }
        var running = AgentProcesses.Snapshot();
        return candidates.FirstOrDefault(c => running.Any(p => AgentProcesses.IsUnder(p.Path, c.InstallRoot)))
            ?? candidates.OrderBy(c => EditionRank(c.Edition)).First();
    }

    private static int EditionRank(string edition) => edition switch
    {
        "direct" => 0,
        "store" => 1,
        _ => 2,
    };

    /// <summary>
    /// Parse a package full name "Name_Version_Arch_ResourceId_PublisherId"
    /// (package names cannot contain '_') into an install, or null when it is
    /// not one of the known agent packages.
    /// </summary>
    internal static AgentInstall? FromPackageFullName(string fullName, string? installRoot, string? appId)
    {
        var parts = fullName.Split('_');
        if (parts.Length != 5)
        {
            return null;
        }
        var name = parts[0];
        var known = KnownPackages.FirstOrDefault(k => string.Equals(k.PackageName, name, StringComparison.OrdinalIgnoreCase));
        if (known.PackageName is null)
        {
            return null;
        }
        var familyName = $"{name}_{parts[4]}";
        var root = string.IsNullOrEmpty(installRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", fullName)
            : installRoot;
        return new AgentInstall(
            known.App,
            known.Edition,
            parts[1],
            root,
            familyName,
            appId is null ? null : $@"shell:AppsFolder\{familyName}!{appId}");
    }

    private static IEnumerable<AgentInstall> DetectPackages()
    {
        RegistryKey? repo;
        try
        {
            repo = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            yield break;
        }
        if (repo is null)
        {
            yield break;
        }

        using (repo)
        {
            foreach (var fullName in repo.GetSubKeyNames())
            {
                string? root;
                using (var key = repo.OpenSubKey(fullName))
                {
                    root = key?.GetValue("PackageRootFolder") as string;
                }
                var probe = FromPackageFullName(fullName, root, appId: null);
                if (probe is null)
                {
                    continue;
                }
                yield return FromPackageFullName(fullName, root, ReadFirstAppId(probe.InstallRoot))!;
            }
        }
    }

    /// <summary>Application Id of the package's main app (AppxManifest.xml), used to relaunch it.</summary>
    private static string? ReadFirstAppId(string installRoot)
    {
        try
        {
            var manifest = Path.Combine(installRoot, "AppxManifest.xml");
            if (!File.Exists(manifest))
            {
                return null;
            }
            var doc = XDocument.Load(manifest);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application")?.Attribute("Id")?.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Legacy per-user installer: %LOCALAPPDATA%\AnthropicClaude\app-&lt;version&gt;\claude.exe.</summary>
    private static AgentInstall? DetectSquirrelClaude()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }
            var newest = Directory.EnumerateDirectories(root, "app-*")
                .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : null))
                .Where(x => x.Version is not null && File.Exists(Path.Combine(x.Dir, "claude.exe")))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (newest.Dir is null)
            {
                return null;
            }
            var launcher = Path.Combine(root, "claude.exe");
            return new AgentInstall(
                AgentApp.ClaudeDesktop,
                "squirrel",
                newest.Version!.ToString(),
                root,
                PackageFamilyName: null,
                File.Exists(launcher) ? launcher : Path.Combine(newest.Dir, "claude.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
