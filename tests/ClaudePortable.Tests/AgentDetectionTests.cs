using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Discovery;

namespace ClaudePortable.Tests;

public class AgentDetectionTests
{
    private const string ClaudeDirectRoot = @"C:\Program Files\WindowsApps\Claude_4.29.0.0_x64__pzs8sxrjxfjjc";
    private const string ClaudeStoreRoot = @"C:\Program Files\WindowsApps\AnthropicPBC.Claude_3.6556.2.0_x64__fnn82j28hfe8t";
    private const string CodexRoot = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0";

    // What AgentRoots.From yields: data folders, plus the folder the Codex
    // app unpacks its app-server and runtimes into. .cache\codex-runtimes is
    // not a root - its processes belong to the app because the app started them.
    private static readonly AgentRoots Roots = new(
        ClaudeDesktop: [@"C:\Users\u\AppData\Roaming\Claude"],
        CodexDesktop: [@"C:\Users\u\AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0", @"C:\Users\u\AppData\Local\OpenAI\Codex"],
        CodexCli: [@"D:\codex-home"]);

    private static readonly IReadOnlyList<AgentInstall> Installs =
    [
        AgentInstalls.FromPackageFullName("Claude_4.29.0.0_x64__pzs8sxrjxfjjc", ClaudeDirectRoot, "Claude")!,
        AgentInstalls.FromPackageFullName("AnthropicPBC.Claude_3.6556.2.0_x64__fnn82j28hfe8t", ClaudeStoreRoot, "Claude")!,
        AgentInstalls.FromPackageFullName("OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0", CodexRoot, "App")!,
        new AgentInstall(AgentApp.ClaudeDesktop, "squirrel", "0.9.3", @"C:\Users\u\AppData\Local\AnthropicClaude", null, @"C:\Users\u\AppData\Local\AnthropicClaude\claude.exe"),
    ];

    [Theory]
    [InlineData("Claude_4.29.0.0_x64__pzs8sxrjxfjjc", AgentApp.ClaudeDesktop, "direct", "4.29.0.0", "Claude_pzs8sxrjxfjjc")]
    [InlineData("AnthropicPBC.Claude_3.6556.2.0_x64__fnn82j28hfe8t", AgentApp.ClaudeDesktop, "store", "3.6556.2.0", "AnthropicPBC.Claude_fnn82j28hfe8t")]
    [InlineData("OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0", AgentApp.CodexDesktop, "store", "26.1021.3705.0", "OpenAI.Codex_2p2nqsd0c76g0")]
    public void PackageFullName_IsParsed(string fullName, AgentApp app, string edition, string version, string family)
    {
        var install = AgentInstalls.FromPackageFullName(fullName, installRoot: null, appId: "App");

        Assert.NotNull(install);
        Assert.Equal(app, install.App);
        Assert.Equal(edition, install.Edition);
        Assert.Equal(version, install.Version);
        Assert.Equal(family, install.PackageFamilyName);
        Assert.Equal($@"shell:AppsFolder\{family}!App", install.LaunchTarget);
    }

    [Theory]
    [InlineData("Microsoft.WindowsTerminal_1.21.0.0_x64__8wekyb3d8bbwe")]
    [InlineData("OpenAI.ChatGPT-Desktop_1.0.0.0_x64__2p2nqsd0c76g0")]
    [InlineData("Claude")]
    [InlineData("Claude_4.29.0.0_x64_pzs8sxrjxfjjc")]
    public void OtherOrMalformedPackages_AreIgnored(string fullName)
    {
        Assert.Null(AgentInstalls.FromPackageFullName(fullName, installRoot: null, appId: null));
    }

    [Fact]
    public void Classify_RecognisesEveryFlavourByPathAndAncestry()
    {
        ProcessEntry[] snapshot =
        [
            new(1, 0, "explorer.exe", @"C:\Windows\explorer.exe"),
            // Claude Desktop (claude.ai build) with a renderer, a Claude Code
            // session it runs from its data folder, and that session's shell.
            new(10, 1, "claude.exe", ClaudeDirectRoot + @"\app\claude.exe"),
            new(11, 10, "claude.exe", ClaudeDirectRoot + @"\app\claude.exe"),
            new(12, 10, "claude.exe", @"C:\Users\u\AppData\Roaming\Claude\claude-code\2.1.309\abc\claude.exe"),
            new(13, 12, "bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"),
            // Codex app (ChatGPT.exe) with its app-server and a runtime node.
            new(20, 1, "ChatGPT.exe", CodexRoot + @"\app\ChatGPT.exe"),
            new(21, 20, "codex.exe", @"C:\Users\u\AppData\Local\OpenAI\Codex\bin\2993767bd1dfb009\codex.exe"),
            new(22, 21, "node.exe", @"C:\Users\u\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"),
            // CLIs started from a terminal.
            new(30, 1, "WindowsTerminal.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\wt.exe"),
            new(31, 30, "claude.exe", @"C:\Users\u\.local\bin\claude.exe"),
            new(32, 30, "codex.exe", @"C:\Users\u\AppData\Roaming\npm\node_modules\@openai\codex\vendor\codex.exe"),
            // A plugin host below a custom %CODEX_HOME%, started by Chrome.
            new(33, 34, "extension-host.exe", @"D:\codex-home\plugins\cache\openai-bundled\chrome\extension-host.exe"),
            new(34, 1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            // The apps' LocalSystem services: no path visible, not ours.
            new(40, 4, "cowork-svc.exe", null),
            new(41, 4, "codex-windows-sandbox-service.exe", null),
        ];

        var result = AgentProcesses.Classify(snapshot, Installs, Roots).ToDictionary(p => p.Process.Pid);

        Assert.Equal(AgentProcessKind.ClaudeDesktop, result[10].Kind);
        Assert.True(result[10].IsRoot);
        Assert.Equal("direct", result[10].Install?.Edition);
        Assert.False(result[11].IsRoot);
        Assert.Equal(AgentProcessKind.ClaudeDesktop, result[12].Kind);
        Assert.Equal(AgentProcessKind.ClaudeDesktop, result[13].Kind);
        Assert.False(result[13].IsRoot);

        Assert.Equal(AgentProcessKind.CodexDesktop, result[20].Kind);
        Assert.True(result[20].IsRoot);
        Assert.Equal(AgentProcessKind.CodexDesktop, result[21].Kind);
        Assert.Equal(AgentProcessKind.CodexDesktop, result[22].Kind);
        Assert.False(result[22].IsRoot);

        Assert.Equal(AgentProcessKind.ClaudeCodeCli, result[31].Kind);
        Assert.True(result[31].IsRoot);
        Assert.Equal(AgentProcessKind.CodexCli, result[32].Kind);
        Assert.Equal(AgentProcessKind.CodexCli, result[33].Kind);
        Assert.True(result[33].IsRoot);

        Assert.DoesNotContain(1, result.Keys);
        Assert.DoesNotContain(34, result.Keys);
        Assert.DoesNotContain(30, result.Keys);
        Assert.DoesNotContain(40, result.Keys);
        Assert.DoesNotContain(41, result.Keys);
    }

    [Fact]
    public void Classify_StoreEditionAndLegacyInstaller()
    {
        ProcessEntry[] snapshot =
        [
            new(50, 1, "claude.exe", ClaudeStoreRoot + @"\app\claude.exe"),
            new(60, 1, "claude.exe", @"C:\Users\u\AppData\Local\AnthropicClaude\app-0.9.3\claude.exe"),
        ];

        var result = AgentProcesses.Classify(snapshot, Installs, Roots).ToDictionary(p => p.Process.Pid);

        Assert.Equal(AgentProcessKind.ClaudeDesktop, result[50].Kind);
        Assert.Equal("store", result[50].Install?.Edition);
        Assert.Equal(AgentProcessKind.ClaudeDesktop, result[60].Kind);
        Assert.Equal("squirrel", result[60].Install?.Edition);
    }

    [Fact]
    public void Roots_ComeFromDiscoveryAndInstalls()
    {
        DiscoveredClaudePath[] paths =
        [
            new("claudeDesktopAppData", AppContext.BaseDirectory, true, "test"),
            new("codexUserProfile", Path.GetTempPath(), true, "test"),
            new("claudeCodeUserProfile", @"Z:\not\there", false, "test"),
        ];

        var roots = AgentRoots.From(paths, Installs);

        Assert.Equal([AppContext.BaseDirectory], roots.ClaudeDesktop.Where(r => !r.Contains("Packages", StringComparison.Ordinal)));
        Assert.Contains(roots.ClaudeDesktop, r => r.EndsWith(@"Packages\Claude_pzs8sxrjxfjjc", StringComparison.Ordinal));
        Assert.Contains(roots.ClaudeDesktop, r => r.EndsWith(@"Packages\AnthropicPBC.Claude_fnn82j28hfe8t", StringComparison.Ordinal));
        Assert.Equal(2, roots.CodexDesktop.Count);
        Assert.Contains(roots.CodexDesktop, r => r.EndsWith(@"Local\OpenAI\Codex", StringComparison.Ordinal));
        Assert.Equal([Path.GetTempPath()], roots.CodexCli);
    }

    [Fact]
    public void Roots_HaveNoCodexHelperFolderWithoutTheCodexApp()
    {
        var roots = AgentRoots.From([], Installs.Where(i => i.App != AgentApp.CodexDesktop).ToList());

        Assert.Empty(roots.CodexDesktop);
    }

    [Fact]
    public void Classify_OrphanedCodexAppHelpersStillBelongToTheApp()
    {
        // The Codex app exited uncleanly: its app-server and computer-use
        // runtime are left behind with a parent that no longer exists.
        // A Codex CLI installed next to it is still the CLI.
        ProcessEntry[] snapshot =
        [
            new(1, 0, "explorer.exe", @"C:\Windows\explorer.exe"),
            new(21, 9999, "codex.exe", @"C:\Users\u\AppData\Local\OpenAI\Codex\bin\0ab0e7a8f3779036\codex.exe"),
            new(22, 9999, "codex-computer-use-swift.exe", @"C:\Users\u\AppData\Local\OpenAI\Codex\runtimes\cua_node\6ef99b3d04073aa7\bin\codex-computer-use-swift.exe"),
            new(23, 21, "node.exe", @"C:\Users\u\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"),
            new(30, 1, "WindowsTerminal.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\wt.exe"),
            new(31, 30, "codex.exe", @"C:\Users\u\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe"),
        ];

        var result = AgentProcesses.Classify(snapshot, Installs, Roots).ToDictionary(p => p.Process.Pid);

        Assert.Equal(AgentProcessKind.CodexDesktop, result[21].Kind);
        Assert.True(result[21].IsRoot);
        Assert.Equal(AgentProcessKind.CodexDesktop, result[22].Kind);
        Assert.True(result[22].IsRoot);
        Assert.Equal(AgentProcessKind.CodexDesktop, result[23].Kind);
        Assert.False(result[23].IsRoot);
        Assert.Equal(AgentProcessKind.CodexCli, result[31].Kind);
    }

    [Theory]
    [InlineData("CoworkVMService", "\"" + ClaudeDirectRoot + "\\app\\resources\\cowork-svc.exe\"", AgentProcessKind.ClaudeDesktop)]
    [InlineData("CoworkVMServiceStore", "\"" + ClaudeStoreRoot + "\\app\\resources\\cowork-svc.exe\"", AgentProcessKind.ClaudeDesktop)]
    [InlineData("CodexSandboxService.OpenAI.Codex", "\"" + CodexRoot + "\\app\\resources\\codex-windows-sandbox-service.exe\"", AgentProcessKind.CodexDesktop)]
    [InlineData("CoworkVMService", CodexRoot + "\\app\\resources\\some-svc.exe --flag", AgentProcessKind.CodexDesktop)]
    public void Services_BelongToTheInstallHoldingTheirBinary(string name, string imagePath, AgentProcessKind kind)
    {
        var service = AgentServices.Classify(name, imagePath, Installs);

        Assert.NotNull(service);
        Assert.Equal(kind, service.Kind);
        Assert.Equal(name, service.Name);
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p")]
    [InlineData("\"C:\\Program Files\\WindowsApps\\Claude_3.0.0.0_x64__pzs8sxrjxfjjc\\app\\resources\\cowork-svc.exe\"")]
    [InlineData("\"C:\\Program Files\\WindowsApps\\Claude_4.29.0.0_x64__pzs8sxrjxfjjc-other\\cowork-svc.exe\"")]
    [InlineData(@"\SystemRoot\System32\drivers\disk.sys")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherServices_AreIgnored(string? imagePath)
    {
        // Includes a service left registered by an older, removed package.
        Assert.Null(AgentServices.Classify("Whatever", imagePath, Installs));
    }

    [Theory]
    [InlineData("\"C:\\A B\\svc.exe\" --run", @"C:\A B\svc.exe")]
    [InlineData(@"C:\A\svc.exe -k x", @"C:\A\svc.exe")]
    [InlineData(@"C:\A\svc.EXE", @"C:\A\svc.EXE")]
    [InlineData("\"unterminated", null)]
    [InlineData("   ", null)]
    public void ServiceImagePath_IsReducedToTheBinary(string imagePath, string? expected)
    {
        Assert.Equal(expected, AgentServices.ExecutablePath(imagePath));
    }

    [Fact]
    public void Classify_SurvivesParentCycles()
    {
        // PID reuse can make a stale parent link point back down the tree.
        ProcessEntry[] snapshot =
        [
            new(70, 71, "claude.exe", @"C:\Users\u\.local\bin\claude.exe"),
            new(71, 70, "cmd.exe", @"C:\Windows\System32\cmd.exe"),
        ];

        var result = AgentProcesses.Classify(snapshot, Installs, Roots);

        Assert.Single(result);
        Assert.Equal(AgentProcessKind.ClaudeCodeCli, result[0].Kind);
    }

    [Theory]
    [InlineData(@"C:\Users\u\.local\bin\claude.exe")]
    [InlineData(@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe")]
    [InlineData(@"C:\Users\u\AppData\Local\Microsoft\WinGet\Packages\Anthropic.ClaudeCode\claude.exe")]
    public void ClaudeCodeCli_IsNeverTakenForClaudeDesktop(string cliPath)
    {
        // The CLI is claude.exe too; a Claude Desktop-only restore must
        // neither be blocked by it nor end it.
        ProcessEntry[] snapshot =
        [
            new(1, 0, "explorer.exe", @"C:\Windows\explorer.exe"),
            new(10, 1, "claude.exe", ClaudeDirectRoot + @"\app\claude.exe"),
            new(30, 1, "WindowsTerminal.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\wt.exe"),
            new(31, 30, "claude.exe", cliPath),
        ];
        var kinds = AgentProcesses.KindsToClose(new HashSet<string> { SourceGroups.ClaudeDesktop }, AgentOperation.Restore);

        var result = AgentProcesses.Classify(snapshot, Installs, Roots);

        Assert.Equal(AgentProcessKind.ClaudeCodeCli, Assert.Single(result, p => p.Process.Pid == 31).Kind);
        Assert.Equal([10], result.Where(p => kinds.Contains(p.Kind)).Select(p => p.Process.Pid));
    }

    [Fact]
    public void Backup_ClosesOnlyTheDesktopAppsOfTheSelectedGroups()
    {
        Assert.Equal(
            [AgentProcessKind.ClaudeDesktop, AgentProcessKind.CodexDesktop],
            AgentProcesses.KindsToClose(null, AgentOperation.Backup).Order());
        Assert.Equal(
            [AgentProcessKind.CodexDesktop],
            AgentProcesses.KindsToClose(new HashSet<string> { SourceGroups.Codex }, AgentOperation.Backup));
        Assert.Equal(
            [AgentProcessKind.ClaudeDesktop],
            AgentProcesses.KindsToClose(new HashSet<string> { SourceGroups.Cowork }, AgentOperation.Backup));
        Assert.Empty(AgentProcesses.KindsToClose(new HashSet<string> { SourceGroups.ClaudeCode }, AgentOperation.Backup));
    }

    [Fact]
    public void Restore_AlsoClosesTheClis()
    {
        Assert.Equal(
            [AgentProcessKind.ClaudeDesktop, AgentProcessKind.ClaudeCodeCli, AgentProcessKind.CodexDesktop, AgentProcessKind.CodexCli],
            AgentProcesses.KindsToClose(null, AgentOperation.Restore).Order());
        Assert.Equal(
            [AgentProcessKind.ClaudeCodeCli],
            AgentProcesses.KindsToClose(new HashSet<string> { SourceGroups.ClaudeCode }, AgentOperation.Restore));
    }

    [Fact]
    public void IsUnder_RespectsFolderBoundaries()
    {
        Assert.True(AgentProcesses.IsUnder(@"C:\Users\u\.codex\plugins\x.exe", @"C:\Users\u\.codex"));
        Assert.True(AgentProcesses.IsUnder(@"c:\users\U\.CODEX\x.exe", @"C:\Users\u\.codex\"));
        Assert.False(AgentProcesses.IsUnder(@"C:\Users\u\.codex-other\x.exe", @"C:\Users\u\.codex"));
        Assert.False(AgentProcesses.IsUnder(null, @"C:\Users\u\.codex"));
    }
}
