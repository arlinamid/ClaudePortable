using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ClaudePortable.App.Localization;
using ClaudePortable.Targets;

namespace ClaudePortable.App.Commands;

[SupportedOSPlatform("windows")]
public static class ListCommand
{
    public static Command Build()
    {
        var inOption = new Option<DirectoryInfo>(
            aliases: new[] { "--in", "-i" },
            description: Loc.T("Cli_List_In"))
        {
            IsRequired = true,
        };

        var jsonOption = new Option<bool>(
            aliases: new[] { "--json" },
            description: Loc.T("Cli_List_Json"),
            getDefaultValue: () => false);

        var cmd = new Command("list", Loc.T("Cli_List_Desc"))
        {
            inOption,
            jsonOption,
        };

        cmd.SetHandler((inValue, asJson) =>
        {
            var target = new FolderTarget(inValue.FullName);
            var backups = target.ListBackups();

            if (asJson)
            {
                var payload = backups.Select(b => new
                {
                    fileName = b.FileName,
                    fullPath = b.FullPath,
                    sizeBytes = b.SizeBytes,
                    tier = b.Manifest?.RetentionTier.ToString().ToLowerInvariant(),
                    createdAt = b.Manifest?.CreatedAt,
                    hostname = b.Manifest?.Hostname,
                    sha256 = b.Manifest?.Sha256,
                });
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            if (backups.Count == 0)
            {
                Console.WriteLine(Loc.T("Cli_List_NoBackups"));
                return;
            }

            Console.WriteLine($"{Loc.T("Cli_List_HdrTier"),-8} {Loc.T("Cli_List_HdrCreated"),-20} {Loc.T("Cli_List_HdrSize"),10}  {Loc.T("Cli_List_HdrFile")}");
            foreach (var b in backups)
            {
                var tier = b.Manifest?.RetentionTier.ToString().ToLowerInvariant() ?? "?";
                var created = b.Manifest?.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "?";
                Console.WriteLine($"{tier,-8} {created,-20} {b.SizeBytes,10:N0}  {b.FileName}");
            }
        }, inOption, jsonOption);

        return cmd;
    }
}
