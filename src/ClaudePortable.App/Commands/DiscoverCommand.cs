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

            if (asJson)
            {
                var payload = new
                {
                    claudePaths = paths.Select(p => new { p.Key, p.Path, p.Exists, p.Source }),
                    syncClients = syncClients.Select(s => new { s.Name, s.Path, s.IsAvailable, s.Source }),
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            Console.WriteLine(Loc.T("Cli_Discover_ClaudePaths"));
            foreach (var p in paths)
            {
                Console.WriteLine($"  [{(p.Exists ? Loc.T("Cli_Found") : Loc.T("Cli_Miss")),5}] {p.Key,-28} {p.Path}");
                Console.WriteLine(Loc.F("Cli_Discover_Source", p.Source));
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
