using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using PdfKnead.Core.Localization;

namespace PdfKnead.Localization;

/// <summary>
/// Translated text in XAML: <c>Text="{l:T 'PDF hierher ziehen'}"</c>. The German text
/// is the key (<see cref="Tr"/>). Bound to <see cref="Tr.Version"/>, so the text changes
/// immediately when the language is switched.
/// </summary>
/// <remarks>
/// Write texts containing curly braces (e.g. “{name}”) as an element:
/// <c>&lt;ToolTip.Tip&gt;&lt;l:T Text="Platzhalter: {name} …" /&gt;&lt;/ToolTip.Tip&gt;</c>.
/// Uses a compiled binding without reflection, as required for Native AOT.
/// </remarks>
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string text) => Text = text;

    public string Text { get; set; } = "";

    /// <summary>Binding to <see cref="Tr.Version"/>, e.g. as the second part of a MultiBinding with <see cref="Translate"/>.</summary>
    public static BindingBase LanguageVersion { get; } =
        CompiledBinding.Create<Tr, int>(tr => tr.Version, source: Tr.Instance, mode: BindingMode.OneWay);

    /// <summary>
    /// Translates the first value of a MultiBinding (German text); the second is
    /// <see cref="LanguageVersion"/> and triggers the update when the language is switched.
    /// </summary>
    public static IMultiValueConverter Translate { get; } = new TranslateFirst();

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CompiledBinding.Create<Tr, int>(tr => tr.Version, source: Tr.Instance, mode: BindingMode.OneWay,
            converter: new FixedText(Text));

    private sealed class FixedText(string german) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Tr.T(german);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class TranslateFirst : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Count > 0 && values[0] is string german ? Tr.T(german) : values.Count > 0 ? values[0] : null;
    }
}
