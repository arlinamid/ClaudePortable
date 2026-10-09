using System.Text;

namespace ClaudePortable.App.Ui.Services;

/// <summary>
/// TextWriter for engine warnings (e.g. ZipArchiveWriter's "skipping locked
/// file ..."). Lines go to the Logs tab, marshalled to the UI thread because
/// the engines run on the thread pool, and are counted so the status bar can
/// say how many files were skipped.
/// </summary>
public sealed class UiLogWriter : TextWriter
{
    private int _lines;

    public override Encoding Encoding => Encoding.UTF8;

    public int LineCount => Volatile.Read(ref _lines);

    public override void WriteLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        Interlocked.Increment(ref _lines);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            UiLogSink.Instance.Append(value);
        }
        else
        {
            dispatcher.BeginInvoke(() => UiLogSink.Instance.Append(value));
        }
    }

    public override void Write(char value)
    {
        // Engines only write whole lines.
    }
}
