using System.Globalization;
using System.Windows.Data;
using ClaudePortable.App.Localization;

namespace ClaudePortable.App.Ui.Converters;

/// <summary>
/// Read-only true/false status as localized "Yes" / "No" text. Used instead of
/// disabled check boxes, which look clickable and have no accessible name.
/// </summary>
public sealed class BoolToYesNoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Loc.T("Common_Yes") : Loc.T("Common_No");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
