using System.Windows.Data;
using System.Windows.Markup;
using UseControl.Admin.ViewModels.Infrastructure;

namespace UseControl.Admin.Infrastructure;

/// <summary>
/// {l:Tr Ui.Nav.Status} in XAML: the text in the current language, updated live when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = TranslationSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
