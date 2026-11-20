using System.Globalization;
using Avalonia.Data.Converters;

namespace PdfKnead.Converters;

/// <summary>
/// Returns true if an enum value matches the name given as the parameter:
/// <c>IsVisible="{Binding RibbonTab, Converter={x:Static c:EnumIs.Instance}, ConverterParameter=Start}"</c>.
/// Used for the visibility of pages and for highlighting the selected tab. Bound two ways (a radio button), a check sets the value named.
/// </summary>
public sealed class EnumIs : IValueConverter
{
    public static readonly EnumIs Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum e && parameter is string name &&
        string.Equals(e.ToString(), name, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && Nullable.GetUnderlyingType(targetType) is var inner && (inner ?? targetType).IsEnum
            ? Enum.Parse(inner ?? targetType, name)
            : Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>Maps true to semi-transparent (opacity 0.55, e.g. entries whose file is missing) and anything else to fully opaque.</summary>
public sealed class BoolToOpacity : IValueConverter
{
    public static readonly BoolToOpacity Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 0.55 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>RGB pixels drawn by the core (<see cref="PdfKnead.Core.Pdf.Writing.RasterArea"/>) as an image to show; null for none.</summary>
public sealed class RasterAreaToBitmap : IValueConverter
{
    public static readonly RasterAreaToBitmap Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PdfKnead.Core.Pdf.Writing.RasterArea { Width: > 0, Height: > 0 } area || area.Rgb.Length < (long)area.Width * area.Height * 3) return null;
        var rgba = new byte[area.Width * area.Height * 4];
        for (int i = 0, o = 0; o < rgba.Length; i += 3, o += 4)
        {
            rgba[o] = area.Rgb[i];
            rgba[o + 1] = area.Rgb[i + 1];
            rgba[o + 2] = area.Rgb[i + 2];
            rgba[o + 3] = 255;
        }
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(area.Width, area.Height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
            for (var y = 0; y < area.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(rgba, y * area.Width * 4, frame.Address + y * frame.RowBytes, area.Width * 4);
        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Avalonia.Data.BindingOperations.DoNothing;
}
