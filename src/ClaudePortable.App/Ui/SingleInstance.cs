using System.Runtime.Versioning;

namespace ClaudePortable.App.Ui;

/// <summary>
/// Ensures only one GUI instance runs. A second launch signals the first
/// to show/activate, then exits. CLI mode does not use this guard so
/// scheduled <c>backup</c> / <c>schedule</c> commands can still run while
/// the tray app is open.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SingleInstance
{
    private const string MutexName = @"Local\ClaudePortable.Gui.SingleInstance";
    private const string ActivateEventName = @"Local\ClaudePortable.Gui.Activate";

    /// <summary>
    /// Tries to become the sole GUI owner. Returns <c>false</c> when another
    /// GUI is already running (and has been asked to activate).
    /// </summary>
    public static bool TryAcquire(out Mutex? mutex)
    {
        var created = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            created.Dispose();
            mutex = null;
            SignalActivate();
            return false;
        }

        mutex = created;
        return true;
    }

    public static EventWaitHandle CreateActivateEvent() =>
        new(initialState: false, mode: EventResetMode.AutoReset, name: ActivateEventName);

    private static void SignalActivate()
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            activate.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // First instance may be mid-startup or mid-shutdown; nothing to activate.
        }
    }
}
