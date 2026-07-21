using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using ClaudePortable.App.Commands;
using ClaudePortable.App.Localization;
using ClaudePortable.App.Ui.Services;
using Serilog;
using UiApp = ClaudePortable.App.Ui.App;

namespace ClaudePortable.App;

public static class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        ConfigureLogging();
        InitializeLanguage(args);

        if (args.Length == 0 || args.Contains("--gui"))
        {
            return UiApp.RunGui();
        }

        // CLI mode: we're a WinExe so no console was allocated. Attach to
        // the parent terminal's console if there is one; otherwise allocate
        // our own so exit code propagates and output is visible.
        EnsureConsoleForCli();
        return MainCliAsync(args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Resolve the UI language before any command tree or window is built:
    /// an explicit --lang argument wins, then the language saved from the
    /// GUI (settings.json), then the Windows display language (hu -> hu,
    /// anything else -> en). --lang is pre-scanned here because the
    /// System.CommandLine descriptions themselves need the language.
    /// </summary>
    private static void InitializeLanguage(string[] args)
    {
        string? cliLang = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                cliLang = args[i + 1];
            }
            else if (args[i].StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                cliLang = args[i]["--lang=".Length..];
            }
        }

        var saved = new SettingsStore().Load().Language;
        Loc.SetLanguage(Loc.Normalize(cliLang ?? saved));
    }

    private static async Task<int> MainCliAsync(string[] args)
    {
        var root = new RootCommand(Loc.T("Cli_RootDesc"));
        var langOption = new Option<string?>("--lang", Loc.T("Cli_Lang"));
        langOption.FromAmong("en", "hu");
        root.AddGlobalOption(langOption);
        root.AddCommand(BackupCommand.Build());
        root.AddCommand(RestoreCommand.Build());
        root.AddCommand(ListCommand.Build());
        root.AddCommand(DiscoverCommand.Build());
        root.AddCommand(ScheduleCommand.Build());
        root.AddCommand(RotateCommand.Build());
        return await root.InvokeAsync(args).ConfigureAwait(false);
    }

    private static void EnsureConsoleForCli()
    {
        if (AttachConsole(AttachParentProcess))
        {
            // Redirect managed Console streams to the real console so our
            // output mixes cleanly with whatever the parent shell prints.
            // UTF-8 so localized output (Hungarian accents) survives legacy
            // OEM code pages.
            var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var stdOut = Console.OpenStandardOutput();
            if (stdOut != Stream.Null)
            {
                var writer = new StreamWriter(stdOut, utf8) { AutoFlush = true };
                Console.SetOut(writer);
            }
            var stdErr = Console.OpenStandardError();
            if (stdErr != Stream.Null)
            {
                var writer = new StreamWriter(stdErr, utf8) { AutoFlush = true };
                Console.SetError(writer);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"),
            "ClaudePortable",
            "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "claudeportable-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }
}
