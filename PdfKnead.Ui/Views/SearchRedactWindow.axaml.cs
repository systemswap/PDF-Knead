using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>A search hit that can be ticked.</summary>
public sealed partial class RedactionChoice(RedactionHit hit) : ObservableObject
{
    public RedactionHit Hit { get; } = hit;

    public string Title { get; } = Tr.T("Seite {0}: {1}", hit.Page + 1, hit.Found)
        // found in the second reading of a font (No. 1246): the text in the reading it was found in, marked as such
        + (hit.SecondReading ? " (" + Tr.T("zweite Lesart der Schrift") + ")" : "")
        + (hit.Entity is { } entity ? " (" + DocumentViewModel.EntityLabel(entity) + ")" : "")
        + (hit.Marked ? " — " + Tr.T("schon markiert") : hit.Doubt is { } doubt ? " — " + doubt : "");

    /// <summary>Whether it can be ticked: not when it lies under a mark already (it would be marked twice; report of 4.11.2026).</summary>
    public bool CanChoose { get; } = !hit.Marked;

    /// <summary>Ticked; a doubtful hit (an IBAN whose check digits do not fit) is offered unticked, one already marked too.</summary>
    [ObservableProperty] public partial bool Chosen { get; set; } = hit.Doubt is null && !hit.Marked;
}

/// <summary>
/// “Search and redact”: searches for words (one per line, also loaded from a word list), patterns (e-mail, phone, IBAN and card numbers
/// with their check digits, dates) and persons, companies and the parts of addresses (rules and lists, KONZEPT.md, section 5.23), and the
/// hits are ticked — suggestions of the recognition unticked, with their reason. Result: the selected hits, or null if cancelled.
/// </summary>
public partial class SearchRedactWindow : Window
{
    private readonly Func<IReadOnlyList<string>, RedactionPatterns, bool, Task<List<RedactionHit>>>? _find;
    private List<RedactionChoice> _choices = [];
    private readonly Func<IReadOnlyList<RedactionHit>, Task<Dictionary<RedactionHit, Core.Pdf.Document.PdfRectangle>>>? _barsOf;

    public SearchRedactWindow() => InitializeComponent();

    /// <param name="find">Searches the document (words, patterns, match case).</param>
    /// <param name="unreadable">Pages with text that cannot be read (no search finds it); null if not known.</param>
    /// <param name="misread">Pages with text that reads suspiciously (a search may not find what it shows); null if not known.</param>
    /// <param name="barsOf">The bar each hit would get (for the warning where the code does not fit); null: no warning.</param>
    public SearchRedactWindow(Func<IReadOnlyList<string>, RedactionPatterns, bool, Task<List<RedactionHit>>> find,
        Func<Task<IReadOnlyList<int>>>? unreadable = null, Func<Task<IReadOnlyList<int>>>? misread = null,
        Func<IReadOnlyList<RedactionHit>, Task<Dictionary<RedactionHit, Core.Pdf.Document.PdfRectangle>>>? barsOf = null) : this()
    {
        _find = find;
        _barsOf = barsOf;
        Opened += async (_, _) =>
        {
            WordsBox.Focus();
            if (unreadable is not null) ShowUnreadable(await unreadable());
            if (misread is not null) ShowMisread(await misread());
        };
    }

    /// <summary>Says where text cannot be read: the search does not find it there, it must be redacted by area (also used by tests).</summary>
    internal void ShowUnreadable(IReadOnlyList<int> pages)
    {
        UnreadableText.IsVisible = pages.Count > 0;
        if (pages.Count == 0) return;
        var list = string.Join(", ", pages.Take(10).Select(p => (p + 1).ToString(System.Globalization.CultureInfo.CurrentCulture))) + (pages.Count > 10 ? " …" : "");
        UnreadableText.Text = pages.Count == 1
            ? Tr.T("Auf Seite {0} steht Text, der sich nicht lesen lässt — die Suche findet ihn nicht. Dort bitte mit „Bereich schwärzen“ schwärzen.", list)
            : Tr.T("Auf den Seiten {0} steht Text, der sich nicht lesen lässt — die Suche findet ihn nicht. Dort bitte mit „Bereich schwärzen“ schwärzen.", list);
    }

    /// <summary>
    /// Says where text reads suspiciously (docs/RECHERCHE-VERSTECKEN.md, B-5): many different characters of a font read the same — the search may
    /// not find what they show, there one checks the hits or redacts by area (also used by tests).
    /// </summary>
    internal void ShowMisread(IReadOnlyList<int> pages)
    {
        MisreadText.IsVisible = pages.Count > 0;
        if (pages.Count == 0) return;
        var list = string.Join(", ", pages.Take(10).Select(p => (p + 1).ToString(System.Globalization.CultureInfo.CurrentCulture))) + (pages.Count > 10 ? " …" : "");
        MisreadText.Text = pages.Count == 1
            ? Tr.T("Auf Seite {0} liest sich Text auffällig (viele verschiedene Zeichen lesen sich gleich) — die Suche findet dort vielleicht nicht alles. Bitte dort selbst nachsehen und mit „Bereich schwärzen“ schwärzen.", list)
            : Tr.T("Auf den Seiten {0} liest sich Text auffällig (viele verschiedene Zeichen lesen sich gleich) — die Suche findet dort vielleicht nicht alles. Bitte dort selbst nachsehen und mit „Bereich schwärzen“ schwärzen.", list);
    }

    /// <summary>Runs the search (also used by tests).</summary>
    internal async Task SearchAsync()
    {
        if (_find is null) return;
        var words = (WordsBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var patterns = (EmailBox.IsChecked == true ? RedactionPatterns.Email : 0) | (PhoneBox.IsChecked == true ? RedactionPatterns.Phone : 0) |
                       (IbanBox.IsChecked == true ? RedactionPatterns.Iban : 0) | (CardBox.IsChecked == true ? RedactionPatterns.Card : 0) |
                       (DateBox.IsChecked == true ? RedactionPatterns.Date : 0) | EntityPatterns;
        if (words.Length == 0 && patterns == RedactionPatterns.None)
        {
            StatusText.Text = Tr.T("Bitte Wörter eingeben oder ein Muster wählen.");
            return;
        }
        SearchButton.IsEnabled = false;
        StatusText.Text = Tr.T("Suche …");
        try
        {
            _choices = (await _find(words, patterns, CaseBox.IsChecked == true)).Select(h => new RedactionChoice(h)).ToList();
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
        HitList.ItemsSource = _choices;
        _bars = _barsOf is null ? [] : await _barsOf([.. _choices.Select(c => c.Hit)]);
        foreach (var choice in _choices) choice.PropertyChanged += (_, _) => ChosenChanged();
        ChosenChanged();
        StatusText.Text = _choices.Count switch
        {
            0 => Tr.T("Nichts gefunden."),
            1 => Tr.T("1 Fund"),
            var n => Tr.T("{0} Funde", n),
        };
        if (_choices.Count(c => c.Hit.Marked) is > 0 and var marked)
            StatusText.Text += " — " + (marked == 1 ? Tr.T("1 davon schon markiert") : Tr.T("{0} davon schon markiert", marked));
        ApplyButton.IsEnabled = _choices.Any(c => c.CanChoose);
    }

    /// <summary>What is ticked of persons, companies and the parts of addresses (KONZEPT.md, section 5.23).</summary>
    internal RedactionPatterns EntityPatterns =>
        (PersonBox.IsChecked == true ? RedactionPatterns.Person : 0) | (CompanyBox.IsChecked == true ? RedactionPatterns.Organisation : 0) |
        (StreetBox.IsChecked == true ? RedactionPatterns.Street : 0) | (NumberBox.IsChecked == true ? RedactionPatterns.HouseNumber : 0) |
        (PostcodeBox.IsChecked == true ? RedactionPatterns.PostalCode : 0) | (PlaceBox.IsChecked == true ? RedactionPatterns.Place : 0) |
        (CountryBox.IsChecked == true ? RedactionPatterns.Country : 0) | (PostBoxBox.IsChecked == true ? RedactionPatterns.PostBox : 0) |
        (PlacesBox.IsChecked == true ? RedactionPatterns.PlacesEverywhere : 0) | (ThoroughBox.IsChecked == true ? RedactionPatterns.Thorough : 0);

    private CheckBox[] AddressParts => [StreetBox, NumberBox, PostcodeBox, PlaceBox, CountryBox, PostBoxBox];

    private bool _syncingAddress;

    /// <summary>„Adresse“ ticks or clears all its parts.</summary>
    private void OnAddressChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingAddress || AddressBox.IsChecked is not { } all) return;
        _syncingAddress = true;
        foreach (var part in AddressParts) part.IsChecked = all;
        _syncingAddress = false;
    }

    /// <summary>A part changed: „Adresse“ shows all (ticked), none (empty) or some (the third state).</summary>
    private void OnAddressPartChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingAddress) return;
        _syncingAddress = true;
        var ticked = AddressParts.Count(p => p.IsChecked == true);
        AddressBox.IsChecked = ticked == AddressParts.Length ? true : ticked == 0 ? false : null;
        _syncingAddress = false;
    }

    /// <summary>Returns the selected hits and the reason for them (null: none), with the code set if codes are set per kind of hit.</summary>
    internal MainViewModel.RedactionPick Chosen() => new([.. _choices.Where(c => c.Chosen).Select(c => c.Hit)], ReasonPicker.Choice, ReasonPicker.Auto);

    /// <summary>The bars of the hits found (for the warning where the code does not fit; <see cref="DocumentViewModel.BarsOfAsync"/>).</summary>
    private Dictionary<RedactionHit, Core.Pdf.Document.PdfRectangle> _bars = [];

    /// <summary>
    /// Tells the block „Schwärzungsgrund“ the bars of the hits ticked and what is searched for: it warns where the code does not fit and when
    /// an own reason names what is redacted.
    /// </summary>
    private void ChosenChanged()
    {
        ReasonPicker.Bars = [.. _choices.Where(c => c.Chosen && _bars.ContainsKey(c.Hit)).Select(c => _bars[c.Hit])];
        ReasonPicker.Redacted = [.. (WordsBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            .. _choices.Select(c => c.Hit.Found)];
    }

    internal IReadOnlyList<RedactionChoice> Choices => _choices;

    private async void OnSearch(object? sender, RoutedEventArgs e) => await SearchAsync();

    /// <summary>Adds the terms of a word list to the words (also used by tests); returns how many were new.</summary>
    internal int AddWords(IReadOnlyList<string> terms)
    {
        var present = (WordsBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var known = new HashSet<string>(present, StringComparer.Ordinal);
        var added = terms.Where(known.Add).ToList();
        WordsBox.Text = string.Join("\n", present.Concat(added));
        return added.Count;
    }

    /// <summary>The words as entered, for saving as a list (also used by tests).</summary>
    internal IReadOnlyList<string> Words => (WordsBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly FilePickerFileType ListFiles = new(Tr.T("Wortliste")) { Patterns = ["*.txt", "*.csv", "*.tsv"] };

    private async void OnLoadList(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Wortliste laden"),
            AllowMultiple = false,
            FileTypeFilter = [ListFiles, FilePickerFileTypes.All],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var terms = Core.Pdf.Text.WordList.Read(await File.ReadAllBytesAsync(path));
            var added = AddWords(terms);
            StatusText.Text = added == 1 ? Tr.T("1 Begriff aus der Liste übernommen.") : Tr.T("{0} Begriffe aus der Liste übernommen.", added);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = Tr.T("Die Wortliste lässt sich nicht lesen: {0}", ex.Message);
        }
    }

    private async void OnSaveList(object? sender, RoutedEventArgs e)
    {
        if (Words.Count == 0)
        {
            StatusText.Text = Tr.T("Bitte zuerst Wörter eingeben.");
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Wortliste speichern"),
            SuggestedFileName = Tr.T("Wortliste") + ".txt",
            DefaultExtension = "txt",
            FileTypeChoices = [ListFiles],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            var bytes = Core.Pdf.Text.WordList.Write(Words);
            await Task.Run(() => Core.Pdf.Writing.PdfFileSaver.SaveFile(path, output => output.Write(bytes)));
            StatusText.Text = Tr.T("Wortliste gespeichert.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Tr.T("Die Wortliste lässt sich nicht speichern: {0}", ex.Message);
        }
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Close(Chosen());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
