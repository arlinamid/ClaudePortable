using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;

namespace ClaudePortable.Core.Discovery;

public enum AgentProcessKind
{
    /// <summary>Claude Desktop and everything it runs (Cowork, the bundled Claude Code sessions, helpers).</summary>
    ClaudeDesktop,

    /// <summary>A Claude Code CLI session started outside Claude Desktop (terminal, IDE).</summary>
    ClaudeCodeCli,

    /// <summary>The Codex desktop app (ChatGPT.exe / Codex.exe) and its app-server, runtimes and helpers.</summary>
    CodexDesktop,

    /// <summary>A Codex CLI session started outside the desktop app.</summary>
    CodexCli,
}

public enum AgentOperation
{
    Backup,
    Restore,
    RepairPaths,
}

/// <param name="Pid">Process id.</param>
/// <param name="ParentPid">Parent process id at snapshot time (may be stale).</param>
/// <param name="Name">Image name, e.g. "claude.exe".</param>
/// <param name="Path">Full image path; null when the process belongs to another user or to SYSTEM.</param>
public sealed record ProcessEntry(int Pid, int ParentPid, string Name, string? Path);

/// <param name="IsRoot">True for the top process of an app's tree (closing it closes the helpers too).</param>
/// <param name="Install">The install the process was started from, when known (used to reopen the app).</param>
public sealed record AgentProcess(ProcessEntry Process, AgentProcessKind Kind, bool IsRoot, AgentInstall? Install);

/// <param name="Closed">Install launch targets of the apps that were closed, to reopen them afterwards.</param>
/// <param name="StillRunning">Processes that could not be closed.</param>
/// <param name="Problems">Human-readable reasons, e.g. "AgentPortable runs inside Claude Desktop".</param>
/// <param name="StoppedServices">Names of the apps' services that were stopped, to start them again afterwards.</param>
public sealed record CloseOutcome(IReadOnlyList<string> Closed, IReadOnlyList<AgentProcess> StillRunning, IReadOnlyList<string> Problems, IReadOnlyList<string> StoppedServices)
{
    /// <summary>Nothing was running, nothing to reopen.</summary>
    public static CloseOutcome None { get; } = new([], [], [], []);
}

/// <summary>
/// Finds the processes of every Claude / Codex flavour by where they were
/// started from, not by image name: the Codex desktop app runs as ChatGPT.exe,
/// Claude Desktop starts its own claude.exe copies of Claude Code, and the
/// same names are used by the CLIs. Closing an app closes its whole process
/// tree, so background helpers (Codex app-server, Claude Code sessions inside
/// Claude Desktop, Cowork helpers) go with it and release their files.
/// <para>
/// The LocalSystem services the apps install (CoworkVMService,
/// CodexSandboxService) are not processes we can see; <see cref="CloseAsync"/>
/// stops them through <see cref="AgentServices"/> together with their app.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentProcesses
{
    /// <summary>Which kinds of process must be closed before an operation touches the given groups.</summary>
    public static IReadOnlySet<AgentProcessKind> KindsToClose(IReadOnlySet<string>? groups, AgentOperation operation)
    {
        bool Has(string g) => groups is null || groups.Contains(g);
        var kinds = new HashSet<AgentProcessKind>();
        switch (operation)
        {
            case AgentOperation.Backup:
                // The CLIs are left alone: their state is append-only JSONL
                // and SQLite, which the backup snapshots consistently. Only
                // the desktop apps hold LevelDB / IndexedDB stores that are
                // half-written while they run.
                if (Has(SourceGroups.ClaudeDesktop) || Has(SourceGroups.Cowork))
                {
                    kinds.Add(AgentProcessKind.ClaudeDesktop);
                }
                if (Has(SourceGroups.Codex))
                {
                    kinds.Add(AgentProcessKind.CodexDesktop);
                }
                break;
            case AgentOperation.Restore:
                if (Has(SourceGroups.ClaudeDesktop))
                {
                    kinds.Add(AgentProcessKind.ClaudeDesktop);
                }
                if (Has(SourceGroups.ClaudeCode))
                {
                    kinds.Add(AgentProcessKind.ClaudeCodeCli);
                }
                if (Has(SourceGroups.Codex))
                {
                    kinds.Add(AgentProcessKind.CodexDesktop);
                    kinds.Add(AgentProcessKind.CodexCli);
                }
                break;
            case AgentOperation.RepairPaths:
                kinds.Add(AgentProcessKind.CodexDesktop);
                kinds.Add(AgentProcessKind.CodexCli);
                break;
        }
        return kinds;
    }

    /// <summary>Running agent processes of the given kinds (all kinds when null).</summary>
    public static IReadOnlyList<AgentProcess> Find(IReadOnlySet<AgentProcessKind>? kinds = null, IReadOnlyList<AgentInstall>? installs = null)
    {
        installs ??= AgentInstalls.Detect();
        var all = Classify(Snapshot(), installs, AgentRoots.Current(installs));
        return kinds is null ? all : all.Where(p => kinds.Contains(p.Kind)).ToList();
    }

    /// <summary>Display name for a kind, in English (the App layer localizes).</summary>
    public static string Describe(AgentProcessKind kind) => kind switch
    {
        AgentProcessKind.ClaudeDesktop => "Claude Desktop",
        AgentProcessKind.ClaudeCodeCli => "Claude Code",
        AgentProcessKind.CodexDesktop => "Codex app",
        AgentProcessKind.CodexCli => "Codex CLI",
        _ => kind.ToString(),
    };

    /// <summary>"Claude Desktop (PID 1, 2), Codex app (PID 3)" for messages and logs; helpers are not listed.</summary>
    public static string Summarize(IEnumerable<AgentProcess> processes)
        => string.Join(", ", processes
            .GroupBy(p => p.Kind)
            .Select(g => $"{Describe(g.Key)} (PID {string.Join(", ", g.Where(p => p.IsRoot).DefaultIfEmpty(g.First()).Select(p => p.Process.Pid))})"));

    /// <summary>
    /// Close the given processes: the main window first (lets the app save),
    /// then the whole tree is ended after <paramref name="grace"/>. A tree
    /// that contains AgentPortable itself (started from a terminal inside
    /// Claude Desktop, say) is never ended; it is reported instead. Once an
    /// app is gone, its services are stopped too (with a UAC prompt where the
    /// service does not let the user stop it and <paramref name="allowElevation"/>
    /// is set); start them again with <see cref="AgentServices.StartAsync"/>.
    /// </summary>
    public static async Task<CloseOutcome> CloseAsync(
        IReadOnlyList<AgentProcess> processes,
        TimeSpan grace,
        IOperationLog? log = null,
        bool allowElevation = false,
        CancellationToken cancellationToken = default)
    {
        log = (log ?? NullOperationLog.Instance).In(LogSections.RunningApps);
        var snapshot = Snapshot();
        var ancestors = AncestorsOf(Environment.ProcessId, snapshot);
        var problems = new List<string>();
        var closed = new List<string>();
        var targets = new List<(Process Process, AgentProcess Agent)>();
        var watch = Stopwatch.StartNew();

        foreach (var agent in processes)
        {
            log.Debug($"{Describe(agent.Kind)}: PID {agent.Process.Pid} {agent.Process.Name}{(agent.IsRoot ? " (main)" : string.Empty)} - {agent.Process.Path}");
        }

        // Close tree roots only; their descendants go with them. A process
        // whose root is not in the list (e.g. a lone CLI) is its own root.
        var pids = processes.Select(p => p.Process.Pid).ToHashSet();
        var parentOf = snapshot.ToDictionary(e => e.Pid, e => e.ParentPid);
        foreach (var agent in processes)
        {
            if (parentOf.TryGetValue(agent.Process.Pid, out var parent) && pids.Contains(parent) && parent != agent.Process.Pid)
            {
                continue;
            }
            if (ancestors.Contains(agent.Process.Pid))
            {
                var problem = $"{Describe(agent.Kind)} (PID {agent.Process.Pid}) was not closed: AgentPortable itself was started from inside it. Close it yourself, or start AgentPortable from the Start menu.";
                problems.Add(problem);
                log.Warn(problem);
                continue;
            }
            try
            {
                var p = Process.GetProcessById(agent.Process.Pid);
                targets.Add((p, agent));
                if (agent.IsRoot && agent.Install?.LaunchTarget is { } launch && !closed.Contains(launch, StringComparer.OrdinalIgnoreCase))
                {
                    closed.Add(launch);
                }
                var helpers = processes.Count(x => x.Kind == agent.Kind) - 1;
                log.Info($"Closing {Describe(agent.Kind)} (PID {agent.Process.Pid}, {agent.Process.Name}{(helpers > 0 ? $", with {helpers} helper process(es)" : string.Empty)})");
                p.CloseMainWindow();
            }
            catch (ArgumentException)
            {
                log.Debug($"PID {agent.Process.Pid} had already exited");
            }
            catch (InvalidOperationException)
            {
                log.Debug($"PID {agent.Process.Pid} had already exited");
            }
        }

        var deadline = DateTime.UtcNow + grace;
        foreach (var (p, agent) in targets)
        {
            var left = deadline - DateTime.UtcNow;
            try
            {
                if (left > TimeSpan.Zero)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(left);
                    try
                    {
                        await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }
                if (!p.HasExited)
                {
                    // Electron apps hide to the tray on WM_CLOSE, so most of
                    // them end here. Their stores are crash-safe (LevelDB
                    // log, SQLite WAL), and nothing is mid-write once the
                    // window had its chance to flush.
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    log.Info($"{Describe(agent.Kind)} (PID {agent.Process.Pid}) ended with its helpers (it did not exit on its own within {grace.TotalSeconds:0} s)");
                }
                else
                {
                    log.Info($"{Describe(agent.Kind)} (PID {agent.Process.Pid}) closed");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                var problem = $"Could not end {Describe(agent.Kind)} (PID {agent.Process.Pid}): {ex.Message}";
                problems.Add(problem);
                log.Warn(problem);
            }
            finally
            {
                p.Dispose();
            }
        }

        // Give Windows a moment to release file handles of the ended tree.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
        var kinds = processes.Select(p => p.Kind).ToHashSet();
        var still = Find(kinds).Where(p => !ancestors.Contains(p.Process.Pid) && !IsDescendantOfAny(p.Process.Pid, ancestors, Snapshot())).ToList();
        foreach (var p in still.Where(p => p.IsRoot))
        {
            log.Warn($"Still running: {Describe(p.Kind)} PID {p.Process.Pid} - {p.Process.Path}");
        }

        // An app that is still running keeps its service: stopping it would
        // pull the Cowork VM / sandbox from under the open app.
        IReadOnlyList<string> stoppedServices = [];
        var serviceKinds = kinds.Where(k => !still.Any(p => p.Kind == k)).ToHashSet();
        IReadOnlyList<AgentService> services = serviceKinds.Count == 0 ? [] : AgentServices.FindRunning(serviceKinds);
        if (services.Count > 0)
        {
            var stopped = await AgentServices.StopAsync(services, allowElevation, log, cancellationToken).ConfigureAwait(false);
            stoppedServices = stopped.Done;
            problems.AddRange(stopped.Problems);
        }

        log.Info($"Closing took {OperationLogExtensions.Duration(watch.Elapsed)}; {closed.Count} app(s) and {stoppedServices.Count} service(s) will be restarted afterwards");
        return new CloseOutcome(closed, still, problems, stoppedServices);
    }

    /// <summary>Start apps again after a backup. Returns problems, if any.</summary>
    public static IReadOnlyList<string> Reopen(IEnumerable<string> launchTargets, IOperationLog? log = null)
    {
        log = (log ?? NullOperationLog.Instance).In(LogSections.RunningApps);
        var problems = new List<string>();
        foreach (var target in launchTargets)
        {
            try
            {
                var psi = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                    ? new ProcessStartInfo("explorer.exe", target) { UseShellExecute = false }
                    : new ProcessStartInfo(target) { UseShellExecute = true };
                using var _ = Process.Start(psi);
                log.Info($"Reopened {target}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                var problem = $"Could not reopen '{target}': {ex.Message}";
                problems.Add(problem);
                log.Warn(problem);
            }
        }
        return problems;
    }

    /// <summary>
    /// Pure classification, unit-testable: decide for every process whether it
    /// belongs to an agent, and which. Desktop apps are recognised by their
    /// install folders and data folders; anything started by a desktop app
    /// belongs to that app, wherever its binary lives.
    /// </summary>
    internal static IReadOnlyList<AgentProcess> Classify(
        IReadOnlyList<ProcessEntry> snapshot,
        IReadOnlyList<AgentInstall> installs,
        AgentRoots roots)
    {
        var byPid = snapshot.ToDictionary(e => e.Pid);
        var direct = new Dictionary<int, (AgentProcessKind Kind, AgentInstall? Install)>();
        foreach (var e in snapshot)
        {
            if (ClassifyOne(e, installs, roots) is { } k)
            {
                direct[e.Pid] = k;
            }
        }

        var owned = new Dictionary<int, (AgentProcessKind Kind, AgentInstall? Install)>();
        foreach (var e in snapshot)
        {
            // Walk up: the nearest desktop-app ancestor wins (a codex.exe or
            // node.exe started by the Codex app is part of the app). The
            // walk is bounded by the snapshot, and PID reuse can make parent
            // links cyclic, hence the visited set.
            (AgentProcessKind Kind, AgentInstall? Install)? owner = null;
            var seen = new HashSet<int> { e.Pid };
            var cursor = e;
            while (byPid.TryGetValue(cursor.ParentPid, out var parent) && seen.Add(parent.Pid))
            {
                if (direct.TryGetValue(parent.Pid, out var pk) && pk.Kind is AgentProcessKind.ClaudeDesktop or AgentProcessKind.CodexDesktop)
                {
                    owner = pk;
                    break;
                }
                cursor = parent;
            }

            if (owner is { } o)
            {
                owned[e.Pid] = (o.Kind, direct.TryGetValue(e.Pid, out var own) && own.Kind == o.Kind ? own.Install ?? o.Install : o.Install);
            }
            else if (direct.TryGetValue(e.Pid, out var self))
            {
                owned[e.Pid] = self;
            }
        }

        var result = new List<AgentProcess>();
        foreach (var e in snapshot)
        {
            if (!owned.TryGetValue(e.Pid, out var mine))
            {
                continue;
            }
            var parentIsSameKind = e.ParentPid != e.Pid
                && owned.TryGetValue(e.ParentPid, out var parent)
                && parent.Kind == mine.Kind;
            result.Add(new AgentProcess(e, mine.Kind, IsRoot: !parentIsSameKind, mine.Install));
        }
        return result;
    }

    private static (AgentProcessKind, AgentInstall?)? ClassifyOne(ProcessEntry e, IReadOnlyList<AgentInstall> installs, AgentRoots roots)
    {
        if (e.Path is null)
        {
            // Another user's or a SYSTEM process (the apps' services): not ours to close.
            return null;
        }
        foreach (var install in installs)
        {
            if (IsUnder(e.Path, install.InstallRoot))
            {
                return (install.App == AgentApp.ClaudeDesktop ? AgentProcessKind.ClaudeDesktop : AgentProcessKind.CodexDesktop, install);
            }
        }
        if (roots.ClaudeDesktop.Any(r => IsUnder(e.Path, r)))
        {
            return (AgentProcessKind.ClaudeDesktop, null);
        }
        if (roots.CodexDesktop.Any(r => IsUnder(e.Path, r)))
        {
            return (AgentProcessKind.CodexDesktop, null);
        }
        if (roots.CodexCli.Any(r => IsUnder(e.Path, r)) || NameIs(e, "codex.exe"))
        {
            return (AgentProcessKind.CodexCli, null);
        }
        if (NameIs(e, "claude.exe"))
        {
            return (AgentProcessKind.ClaudeCodeCli, null);
        }
        return null;
    }

    private static bool NameIs(ProcessEntry e, string name)
        => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string? path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }
        var r = root.TrimEnd('\\', '/') + "\\";
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<int> AncestorsOf(int pid, IReadOnlyList<ProcessEntry> snapshot)
    {
        var byPid = snapshot.ToDictionary(e => e.Pid);
        var result = new HashSet<int>();
        var cursor = pid;
        while (byPid.TryGetValue(cursor, out var e) && e.ParentPid != cursor && result.Add(e.ParentPid))
        {
            cursor = e.ParentPid;
        }
        return result;
    }

    private static bool IsDescendantOfAny(int pid, HashSet<int> roots, IReadOnlyList<ProcessEntry> snapshot)
        => AncestorsOf(pid, snapshot).Overlaps(roots);

    /// <summary>All processes with their parent and (when accessible) full image path.</summary>
    public static IReadOnlyList<ProcessEntry> Snapshot()
    {
        var result = new List<ProcessEntry>();
        var handle = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (handle == NativeMethods.InvalidHandle)
        {
            return result;
        }
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(handle, ref entry))
            {
                return result;
            }
            do
            {
                var pid = (int)entry.th32ProcessID;
                if (pid != 0)
                {
                    result.Add(new ProcessEntry(pid, (int)entry.th32ParentProcessID, entry.szExeFile, TryGetImagePath(pid)));
                }
            }
            while (NativeMethods.Process32NextW(handle, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
        return result;
    }

    private static string? TryGetImagePath(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    private static class NativeMethods
    {
        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}

/// <summary>
/// Folders whose binaries belong to an agent besides the install roots: the
/// data folders WindowsPathDiscovery resolved (so %CODEX_HOME%,
/// CLAUDE_CONFIG_DIR and Store redirection are honoured) and folders of the
/// detected installs. Helpers an app starts (the Codex app-server, Claude
/// Code sessions, runtimes) belong to the app by ancestry; the roots catch
/// them too once the app has exited and left them behind.
/// </summary>
public sealed record AgentRoots(
    IReadOnlyList<string> ClaudeDesktop,
    IReadOnlyList<string> CodexDesktop,
    IReadOnlyList<string> CodexCli)
{
    [SupportedOSPlatform("windows")]
    public static AgentRoots Current(IReadOnlyList<AgentInstall>? installs = null)
        => From(new WindowsPathDiscovery().Discover(), installs ?? AgentInstalls.Detect());

    /// <summary>
    /// Claude Desktop runs Claude Code / Cowork binaries it downloaded into
    /// its data folder; a packaged app may run helpers from its package data
    /// folder (e.g. the Chrome native host). The Codex app unpacks its
    /// app-server and runtimes (bin\&lt;hash&gt;\codex.exe, runtimes\cua_node)
    /// into %LOCALAPPDATA%\OpenAI\Codex, which outlive the app when it exits
    /// uncleanly; the Codex CLI installs to %LOCALAPPDATA%\Programs\OpenAI\Codex
    /// instead. The Codex CLI runs plugin hosts from below its state root.
    /// </summary>
    public static AgentRoots From(IReadOnlyList<DiscoveredClaudePath> paths, IReadOnlyList<AgentInstall> installs)
    {
        string[] Existing(params string[] keys) => paths
            .Where(p => p.Exists && keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && Directory.Exists(p.Path))
            .Select(p => p.Path)
            .ToArray();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(localAppData, "Packages");
        string[] PackageData(AgentApp app) => installs
            .Where(i => i.App == app && i.PackageFamilyName is not null)
            .Select(i => Path.Combine(packages, i.PackageFamilyName!))
            .ToArray();
        string[] CodexAppHelpers() => installs.Any(i => i.App == AgentApp.CodexDesktop)
            ? [Path.Combine(localAppData, "OpenAI", "Codex")]
            : [];

        return new AgentRoots(
            ClaudeDesktop: [.. Existing("claudeDesktopAppData", "claudeDesktop3pAppData"), .. PackageData(AgentApp.ClaudeDesktop)],
            CodexDesktop: [.. PackageData(AgentApp.CodexDesktop), .. CodexAppHelpers()],
            CodexCli: Existing("codexUserProfile"));
    }
}
