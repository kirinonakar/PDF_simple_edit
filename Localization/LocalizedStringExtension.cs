using Microsoft.UI.Xaml.Markup;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Localization;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocalizedStringExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => LocalizationService.Get(Key);
}
