using System.Windows.Markup;
using Binding = System.Windows.Data.Binding;
using BindingMode = System.Windows.Data.BindingMode;

namespace ClaudePortable.App.Localization;

/// <summary>
/// XAML markup extension: <c>Text="{loc:Loc Nav_Status}"</c> produces a
/// one-way binding to <see cref="Loc.Instance"/>'s indexer, so the text
/// updates live when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
