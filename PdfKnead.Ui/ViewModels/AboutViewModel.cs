using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Platform;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>The window „Über PDF-Knead“: version and the license texts (the own license first, see <see cref="LicenseTexts"/>).</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    public AboutViewModel()
    {
        Entries = LicenseTexts.All();
        Selected = Entries[0];
    }

    /// <summary>The list on the left.</summary>
    public IReadOnlyList<LicenseTexts.Entry> Entries { get; }

    /// <summary>„PDF-Knead 1.1.0“ (with „beta“ in between).</summary>
    public string Title => $"PDF-Knead {AppVersion.Display}";

    /// <summary>The selected entry.</summary>
    [ObservableProperty]
    private LicenseTexts.Entry? _selected;

    /// <summary>Its text.</summary>
    [ObservableProperty]
    private string _text = "";

    partial void OnSelectedChanged(LicenseTexts.Entry? value) => Text = value is null ? "" : LicenseTexts.Text(value);
}
