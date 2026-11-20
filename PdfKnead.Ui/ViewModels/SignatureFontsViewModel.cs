using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// A handwriting font in the „Handschriften“ window: family, file, where it comes from and a sample in the font. Fonts from a folder
/// can be removed, but only after a confirmation in the row („Wirklich entfernen?“).
/// </summary>
public sealed partial class SignatureFontRow(string family, string file, string origin, bool removable, SignatureElement sample,
    Action<SignatureFontRow> remove) : ObservableObject
{
    public string Family { get; } = family;

    public string File { get; } = file;

    public string Origin { get; } = origin;

    /// <summary>The signer's name (or the family) written in the font, as „Unterschrift einfügen“ would place it.</summary>
    public SignatureElement Sample { get; } = sample;

    /// <summary>Whether the font comes from a folder (not built in) and can therefore be removed.</summary>
    public bool Removable { get; } = removable;

    /// <summary>Screen reader name of the button: which font it removes.</summary>
    public string RemoveLabel => Tr.T("„{0}“ entfernen", Family);

    /// <summary>Whether the confirmation is visible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAsk))]
    private bool _asking;

    public bool CanAsk => Removable && !Asking;

    [RelayCommand]
    private void Ask() => Asking = true;

    [RelayCommand]
    private void Keep() => Asking = false;

    [RelayCommand]
    private void Remove() => remove(this);
}

/// <summary>
/// The „Handschriften“ window: the handwriting fonts for signatures — added ones (removable) and those that come with the program —,
/// importing a font file and showing the folder.
/// </summary>
/// <param name="showFolder">Shows a folder in the file manager.</param>
public sealed partial class SignatureFontsViewModel(Func<string, Task>? showFolder) : ObservableObject
{
    /// <summary>Lets the user choose a font file (set by the window); returns the path, or null.</summary>
    public Func<Task<string?>>? PickFontFile { get; set; }

    /// <summary>The own fonts: those in the folders (imported or placed there); they can be removed.</summary>
    public ObservableCollection<SignatureFontRow> Added { get; } = [];

    /// <summary>Fonts built into the program.</summary>
    public ObservableCollection<SignatureFontRow> BuiltIn { get; } = [];

    /// <summary>Whether any font was added (otherwise a hint how to add one).</summary>
    public bool HasAdded => Added.Count > 0;

    /// <summary>Outcome of the last import or removal (empty: nothing yet).</summary>
    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _failed;

    /// <summary>Rereads the folders and fills the lists (when the window opens, since files may have been placed there in the meantime).</summary>
    public void Load()
    {
        SignatureFonts.Refresh();
        Added.Clear();
        BuiltIn.Clear();
        var settings = UserSettings.Current;
        foreach (var file in SignatureFonts.Names)
        {
            var kind = SignatureFonts.Origin(file);
            var origin = kind switch
            {
                SignatureFontOrigin.Embedded => Tr.T("im Programm"),
                SignatureFontOrigin.UserFolder => Tr.T("im Benutzerordner"),
                _ => Tr.T("neben dem Programm"),
            };
            var family = SignatureFonts.Display(SignatureFonts.Get(file));
            var sample = new SignatureElement
            {
                // About 1:1 in the list; the ballpoint-pen texture is computed for this size
                Rect = new PdfRectangle(100, 100, 260, 124),
                Name = settings.SignatureName is { Length: > 0 } name ? name : family,
                FontFile = file,
                Color = settings.SignatureColor,
                Textured = settings.SignatureTextured,
                InkStrength = settings.SignatureInkStrength,
            };
            var row = new SignatureFontRow(family, file, origin, kind != SignatureFontOrigin.Embedded, sample, Remove);
            (row.Removable ? Added : BuiltIn).Add(row);
        }
        OnPropertyChanged(nameof(HasAdded));
    }

    /// <summary>Chooses a file and copies it into the folder (the original stays); it is then available for signatures.</summary>
    [RelayCommand]
    private async Task Import()
    {
        if (PickFontFile is null || await PickFontFile() is not { } path) return;
        var result = await Task.Run(() => SignatureFonts.Import(path));
        Failed = result.Error is not null;
        Message = result switch
        {
            { Error: { } error } => Tr.T(error),
            { AlreadyThere: true } => Tr.T("„{0}“ ist schon da und steht bei „Unterschrift einfügen“ zur Wahl.", result.Family),
            { Fallback: true } => Tr.T("„{0}“ importiert — steht jetzt bei „Unterschrift einfügen“ zur Wahl. Neben dem Programm darf PDF-Knead nicht schreiben; die Kopie liegt deshalb im Benutzerordner und zieht nicht mit um.", result.Family),
            _ => Tr.T("„{0}“ importiert — steht jetzt bei „Unterschrift einfügen“ zur Wahl.", result.Family),
        };
        Load();
    }

    /// <summary>Deletes the font file (after the confirmation in the row); signatures already placed keep their appearance until the program exits.</summary>
    private void Remove(SignatureFontRow row)
    {
        var error = SignatureFonts.Remove(row.File);
        Failed = error is not null;
        Message = error is not null ? Tr.T(error) : Tr.T("„{0}“ entfernt — steht bei „Unterschrift einfügen“ nicht mehr zur Wahl.", row.Family);
        Load();
    }

    /// <summary>Shows the „Schriften-Unterschrift“ folder (creates it if it does not exist yet).</summary>
    [RelayCommand]
    private async Task OpenFolder()
    {
        if (showFolder is null) return;
        if (SignatureFonts.FolderToShow() is { } folder)
        {
            await showFolder(folder);
        }
        else
        {
            Failed = true;
            Message = Tr.T("Der Ordner ließ sich nicht anlegen.");
        }
    }
}
