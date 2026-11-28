using System.Globalization;
using Avalonia.Data.Converters;
using PdfKnead.Core.Security;

namespace PdfKnead.Converters;

/// <summary>
/// Shows a name from an opened file isolated from the text around it (<see cref="DisplayNames.Isolated"/>): an embedding or override it leaves
/// open cannot reach over the text beside it — for bookmarks, metadata, form fields, authors of comments
/// (<c>Text="{Binding Title, Converter={x:Static cv:IsolatedName.Instance}}"</c>; docs/FEHLERGEDAECHTNIS.md, No. 1217). Only for text that is
/// shown, never for a value that is edited.
/// </summary>
public sealed class IsolatedName : IValueConverter
{
    public static readonly IsolatedName Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name ? DisplayNames.Isolated(name) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
