using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using ClaudePortable.App.Localization;
using ClaudePortable.App.Ui.Services;
using ClaudePortable.App.Ui.ViewModels;

namespace ClaudePortable.App.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private bool _languageBoxReady;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        SourceInitialized += OnSourceInitialized;

        // Live regions (WCAG 4.1.3): LiveSetting on the TextBlock only marks it;
        // screen readers announce a change once LiveRegionChanged is raised.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Status))
            {
                AnnounceLiveRegion(StatusText);
            }
        };
        vm.ScheduledTasks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScheduledTasksViewModel.StatusLine))
            {
                AnnounceLiveRegion(ScheduleStatusText);
            }
        };

        LanguageBox.SelectedIndex = Loc.LanguageCode == Loc.Hungarian ? 1 : 0;
        _languageBoxReady = true;
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageBoxReady || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code })
        {
            return;
        }
        if (code == Loc.LanguageCode)
        {
            return;
        }
        Loc.SetLanguage(code);
        new SettingsStore().SaveLanguage(code);
        (DataContext as MainViewModel)?.OnLanguageChanged();
    }

    /// <summary>
    /// Opens the per-row ⋮ ContextMenu on left-click (ContextMenu is right-click by default).
    /// </summary>
    private void OnScheduleRowActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AnnounceLiveRegion(UIElement element)
    {
        // After the binding has pushed the new text into the element.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, () =>
        {
            var peer = UIElementAutomationPeer.FromElement(element)
                ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    public void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Opt the title bar into Windows' dark mode so it matches the app background.
        // Supported on Windows 10 1903+ (attribute 19) and Win10 2004+ / Win11 (attribute 20).
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int useDark = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int valueSize);
}
