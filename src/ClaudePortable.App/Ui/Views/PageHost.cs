using System.Windows.Automation.Peers;


namespace ClaudePortable.App.Ui.Views;

/// <summary>
/// The page area of the main window: a TabControl whose tab strip is hidden
/// (the sidebar drives <see cref="System.Windows.Controls.Primitives.Selector.SelectedIndex"/>).
/// <para>
/// A stock TabControl exposes its pages to UI Automation only through the
/// peers of its generated tab headers. With the header panel removed from the
/// template no headers are generated, so screen readers saw an empty "Tab"
/// and none of the page content. This control reports itself as a plain pane
/// whose automation children are simply the visible page's elements.
/// </para>
/// </summary>
public sealed class PageHost : System.Windows.Controls.TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PageHostAutomationPeer(this);

    private sealed class PageHostAutomationPeer(PageHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PageHost);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    }
}
