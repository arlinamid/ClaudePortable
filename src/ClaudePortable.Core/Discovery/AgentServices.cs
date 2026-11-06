using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudePortable.Core.Abstractions;
using Microsoft.Win32;

namespace ClaudePortable.Core.Discovery;

/// <param name="Name">Service name, e.g. "CoworkVMService".</param>
/// <param name="ImagePath">The service binary.</param>
/// <param name="Kind">The app the service belongs to.</param>
public sealed record AgentService(string Name, string ImagePath, AgentProcessKind Kind);

/// <param name="Done">Services that reached the wanted state.</param>
/// <param name="Problems">Human-readable reasons for the others.</param>
public sealed record ServiceOutcome(IReadOnlyList<string> Done, IReadOnlyList<string> Problems);

/// <summary>
/// The LocalSystem services the desktop apps install: CoworkVMService (one per
/// Claude Desktop package, hosts the Cowork VM) and CodexSandboxService. They
/// are recognised by their binary living in a detected install, not by name.
/// They are stopped with their app and started again afterwards. The packages
/// let Authenticated Users start and stop them, so normally no prompt appears;
/// where that right is missing, an elevated sc.exe asks for administrator
/// rights (UAC) - only when the caller allows it, never in a scheduled run.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentServices
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Running services of the apps of the given kinds (all kinds when null).</summary>
    public static IReadOnlyList<AgentService> FindRunning(IReadOnlySet<AgentProcessKind>? kinds = null, IReadOnlyList<AgentInstall>? installs = null)
    {
        installs ??= AgentInstalls.Detect();
        var result = new List<AgentService>();
        foreach (var (name, imagePath) in Registered())
        {
            if (Classify(name, imagePath, installs) is { } service
                && (kinds is null || kinds.Contains(service.Kind))
                && QueryState(name) is State.Running or State.StartPending)
            {
                result.Add(service);
            }
        }
        return result;
    }

    /// <summary>Pure classification, unit-testable: the service belongs to the desktop app whose install holds its binary.</summary>
    internal static AgentService? Classify(string name, string? imagePath, IReadOnlyList<AgentInstall> installs)
    {
        var exe = ExecutablePath(imagePath);
        if (exe is null)
        {
            return null;
        }
        foreach (var install in installs)
        {
            if (AgentProcesses.IsUnder(exe, install.InstallRoot))
            {
                var kind = install.App == AgentApp.ClaudeDesktop ? AgentProcessKind.ClaudeDesktop : AgentProcessKind.CodexDesktop;
                return new AgentService(name, exe, kind);
            }
        }
        return null;
    }

    /// <summary>The binary of a service ImagePath: quoted, or unquoted up to ".exe", arguments dropped.</summary>
    internal static string? ExecutablePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }
        var s = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : s;
    }

    /// <summary>Stop the services and wait until they have stopped.</summary>
    public static Task<ServiceOutcome> StopAsync(IReadOnlyList<AgentService> services, bool allowElevation, IOperationLog? log = null, CancellationToken cancellationToken = default)
        => ChangeAsync(services.Select(s => s.Name).ToList(), stop: true, allowElevation, log, cancellationToken);

    /// <summary>Start services stopped by <see cref="StopAsync"/> again.</summary>
    public static Task<ServiceOutcome> StartAsync(IReadOnlyList<string> names, bool allowElevation, IOperationLog? log = null, CancellationToken cancellationToken = default)
        => ChangeAsync(names, stop: false, allowElevation, log, cancellationToken);

    private static async Task<ServiceOutcome> ChangeAsync(IReadOnlyList<string> names, bool stop, bool allowElevation, IOperationLog? log, CancellationToken cancellationToken)
    {
        log = (log ?? NullOperationLog.Instance).In(LogSections.RunningApps);
        var verb = stop ? "stop" : "start";
        var wanted = stop ? State.Stopped : State.Running;
        var problems = new List<string>();
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Fail(string name, string reason)
        {
            failed.Add(name);
            problems.Add($"Could not {verb} service {name}: {reason}");
        }

        var denied = new List<string>();
        foreach (var name in names)
        {
            var error = Control(name, stop ? NativeMethods.SERVICE_STOP : NativeMethods.SERVICE_START);
            switch (error)
            {
                case 0 or NativeMethods.ERROR_SERVICE_NOT_ACTIVE or NativeMethods.ERROR_SERVICE_ALREADY_RUNNING:
                    log.Debug($"Service {name}: {verb} requested");
                    break;
                case NativeMethods.ERROR_ACCESS_DENIED:
                    denied.Add(name);
                    break;
                default:
                    Fail(name, new Win32Exception(error).Message);
                    break;
            }
        }

        if (denied.Count > 0)
        {
            var refused = allowElevation
                ? RunElevated(verb, denied, log)
                : "administrator rights are needed (not asked for in an unattended run)";
            if (refused is not null)
            {
                denied.ForEach(n => Fail(n, refused));
            }
        }

        var done = new List<string>();
        var deadline = DateTime.UtcNow + StateTimeout;
        foreach (var name in names.Where(n => !failed.Contains(n)))
        {
            var state = QueryState(name);
            while (state != wanted && state is not null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                state = QueryState(name);
            }
            if (state == wanted)
            {
                done.Add(name);
                log.Info($"Service {name} {(stop ? "stopped" : "started")}");
            }
            else
            {
                problems.Add($"Service {name} did not {verb} within {StateTimeout.TotalSeconds:0} s (state: {state?.ToString() ?? "unknown"})");
            }
        }
        foreach (var problem in problems)
        {
            log.Warn(problem);
        }
        return new ServiceOutcome(done, problems);
    }

    /// <summary>Run sc.exe elevated (UAC prompt) for all names at once. Null on success, else why not.</summary>
    private static string? RunElevated(string verb, IReadOnlyList<string> names, IOperationLog log)
    {
        // Service names come from the registry; anything cmd.exe could
        // interpret is refused rather than escaped.
        if (names.Any(n => n.IndexOfAny(['"', '&', '|', '<', '>', '^', '%']) >= 0))
        {
            return "unexpected characters in the service name";
        }
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        var commands = string.Join(" & ", names.Select(n => $"\"{sc}\" {verb} \"{n}\""));
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c {commands}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        log.Info($"Asking for administrator rights to {verb} {string.Join(", ", names)}");
        try
        {
            using var p = Process.Start(psi);
            if (p is not null && !p.WaitForExit(StateTimeout))
            {
                return "sc.exe did not finish";
            }
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
        {
            return "administrator rights were declined";
        }
        catch (Win32Exception ex)
        {
            return ex.Message;
        }
    }

    private static IEnumerable<(string Name, string? ImagePath)> Registered()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null)
        {
            yield break;
        }
        foreach (var name in root.GetSubKeyNames())
        {
            string? imagePath;
            try
            {
                using var key = root.OpenSubKey(name);
                imagePath = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }
            yield return (name, imagePath);
        }
    }

    private enum State
    {
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4,
        ContinuePending = 5,
        PausePending = 6,
        Paused = 7,
    }

    private static State? QueryState(string name)
    {
        var scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var svc = NativeMethods.OpenServiceW(scm, name, NativeMethods.SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return NativeMethods.QueryServiceStatus(svc, out var status) ? (State)status.dwCurrentState : null;
            }
            finally
            {
                NativeMethods.CloseServiceHandle(svc);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    /// <summary>Request a stop or start. Returns the Win32 error, 0 on success.</summary>
    private static int Control(string name, uint access)
    {
        var scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return Marshal.GetLastPInvokeError();
        }
        try
        {
            var svc = NativeMethods.OpenServiceW(scm, name, access);
            if (svc == IntPtr.Zero)
            {
                return Marshal.GetLastPInvokeError();
            }
            try
            {
                var ok = access == NativeMethods.SERVICE_STOP
                    ? NativeMethods.ControlService(svc, NativeMethods.SERVICE_CONTROL_STOP, out _)
                    : NativeMethods.StartServiceW(svc, 0, IntPtr.Zero);
                return ok ? 0 : Marshal.GetLastPInvokeError();
            }
            finally
            {
                NativeMethods.CloseServiceHandle(svc);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    private static class NativeMethods
    {
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const uint SERVICE_START = 0x0010;
        public const uint SERVICE_STOP = 0x0020;
        public const uint SERVICE_CONTROL_STOP = 0x00000001;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_CANCELLED = 1223;
        public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
        public const int ERROR_SERVICE_NOT_ACTIVE = 1062;

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ControlService(IntPtr hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);
    }
}
