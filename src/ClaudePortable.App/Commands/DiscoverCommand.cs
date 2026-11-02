using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;
using ClaudePortable.App.Localization;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Targets;

namespace ClaudePortable.App.Commands;

[SupportedOSPlatform("windows")]
public static class DiscoverCommand
{
    public static Command Build()
    {
        var jsonOption = new Option<bool>(
            aliases: new[] { "--json" },
            description: Loc.T("Cli_List_Json"),
            getDefaultValue: () => false);

        var cmd = new Command("discover", Loc.T("Cli_Discover_Desc"))
        {
            jsonOption,
        };

        cmd.SetHandler(asJson =>
        {
            var paths = new WindowsPathDiscovery().Discover();
            var syncClients = new SyncClientDiscovery().Discover();
            var installs = AgentInstalls.Detect();
            var running = AgentProcesses.Find(installs: installs).Where(p => p.IsRoot).ToList();
            // Links are only known after enumerating the sources; a backup
            // adds their targets on top of these.
            var external = ExternalReferences.Discover(
                paths,
                Array.Empty<ClaudePortable.Core.Manifest.BackupLink>(),
                CoworkProjectDiscovery.Discover().Select(c => c.Path).ToList());

            if (asJson)
            {
                var payload = new
                {
                    apps = installs.Select(i => new { App = i.App.ToString(), i.Edition, i.Version, i.InstallRoot, i.PackageFamilyName }),
                    running = running.Select(p => new { Kind = p.Kind.ToString(), p.Process.Pid, p.Process.Path }),
                    claudePaths = paths.Select(p => new { p.Key, p.Path, p.Exists, p.Source }),
                    external = external.Select(e => new { e.Path, e.Groups, e.ReferencedBy, e.SkipReason }),
                    syncClients = syncClients.Select(s => new { s.Name, s.Path, s.IsAvailable, s.Source }),
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            Console.WriteLine(Loc.T("Cli_Discover_Installs"));
            if (installs.Count == 0)
            {
                Console.WriteLine(Loc.T("Cli_Discover_NoInstalls"));
            }
            foreach (var i in installs)
            {
                Console.WriteLine($"  {i.App,-14} {i.Edition,-9} {i.Version,-16} {i.InstallRoot}");
            }

            Console.WriteLine();
            Console.WriteLine(Loc.T("Cli_Discover_Running"));
            if (running.Count == 0)
            {
                Console.WriteLine(Loc.T("Cli_Discover_None"));
            }
            foreach (var p in running)
            {
                Console.WriteLine($"  {AgentLabels.For(p.Kind),-20} PID {p.Process.Pid,-7} {p.Process.Path}");
            }

            Console.WriteLine();
            Console.WriteLine(Loc.T("Cli_Discover_ClaudePaths"));
            foreach (var p in paths)
            {
                Console.WriteLine($"  [{(p.Exists ? Loc.T("Cli_Found") : Loc.T("Cli_Miss")),5}] {p.Key,-28} {p.Path}");
                Console.WriteLine(Loc.F("Cli_Discover_Source", p.Source));
            }

            Console.WriteLine();
            Console.WriteLine(Loc.T("Cli_Discover_External"));
            if (external.Count == 0)
            {
                Console.WriteLine(Loc.T("Cli_Discover_None"));
            }
            foreach (var e in external)
            {
                Console.WriteLine($"  [{(e.SkipReason is null ? Loc.T("Cli_Ok") : Loc.T("Cli_Skip")),5}] {e.Path}");
                Console.WriteLine(Loc.F("Cli_Discover_ReferencedBy", string.Join("; ", e.ReferencedBy)));
                if (e.SkipReason is not null)
                {
                    Console.WriteLine(Loc.F("Cli_Discover_SkipReason", e.SkipReason));
                }
            }

            Console.WriteLine();
            Console.WriteLine(Loc.T("Cli_Discover_SyncClients"));
            if (syncClients.Count == 0)
            {
                Console.WriteLine(Loc.T("Cli_Discover_None"));
            }
            foreach (var s in syncClients)
            {
                Console.WriteLine($"  [{(s.IsAvailable ? Loc.T("Cli_Ok") : Loc.T("Cli_Miss")),5}] {s.Name,-26} {s.Path}");
                Console.WriteLine(Loc.F("Cli_Discover_Source", s.Source));
            }
        }, jsonOption);

        return cmd;
    }
}
