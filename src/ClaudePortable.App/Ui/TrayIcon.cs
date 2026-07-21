using System.Runtime.Versioning;
using ClaudePortable.App.Localization;

namespace ClaudePortable.App.Ui;

[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _openItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _quitItem;

    public event EventHandler? OpenRequested;
    public event EventHandler? QuitRequested;

    public TrayIcon()
    {
        _menu = new System.Windows.Forms.ContextMenuStrip();
        _openItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Tray_Open"), image: null, OnOpen);
        _quitItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Tray_Quit"), image: null, OnQuit);
        _menu.Items.Add(_openItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(_quitItem);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ClaudePortable",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (s, e) => OpenRequested?.Invoke(s, e);

        Loc.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _openItem.Text = Loc.T("Tray_Open");
        _quitItem.Text = Loc.T("Tray_Quit");
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        // The .ico is embedded as the exe's application icon; extracting it
        // avoids shipping a loose asset next to the single-file exe.
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // fall through to the generic icon
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void OnOpen(object? sender, EventArgs e) => OpenRequested?.Invoke(sender, e);
    private void OnQuit(object? sender, EventArgs e) => QuitRequested?.Invoke(sender, e);

    public void Dispose()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
