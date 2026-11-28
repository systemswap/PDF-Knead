using Avalonia;
using Avalonia.Controls;
using PdfKnead.Core.Localization;

namespace PdfKnead.Views;

/// <summary>A language of a document in the picker: its flag and name in the language itself, then the tag („Deutsch – de“).</summary>
public sealed record DocumentLanguage(Language Language)
{
    /// <summary>The entry „Andere Sprache …“: the own tag is typed below.</summary>
    public bool IsOther => Language.Code.Length == 0;

    /// <summary>„Deutsch – de“; for the other entry its name only.</summary>
    public string Display => IsOther ? Language.Name : $"{Language.Name} – {Language.Code}";
}

/// <summary>
/// The language of a document (/Lang, a language tag) chosen from a list instead of typed (decided on 5.11.2026): the common languages
/// with their flag — drawn as for the program's language, a grey box with the tag where the flag is no set of stripes —, their name in the
/// language itself and the tag; „Andere Sprache …“ opens a box for any other tag. Used in „Eigenschaften“, „Neues Dokument“ and
/// „Als PDF/A speichern“.
/// </summary>
public sealed partial class LanguagePicker : UserControl
{
    /// <summary>The language tag („de“, „en-GB“); empty for none. Two-way: set from outside, the picker shows it.</summary>
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<LanguagePicker, string?>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private static Flag Stripes(params string[] colors) => new(FlagKind.Stripes, Colors: colors);
    private static Flag Vertical(params string[] colors) => new(FlagKind.Stripes, Vertical: true, Colors: colors);
    private static readonly Flag UnionJack = new(FlagKind.UnionJack);

    /// <summary>The common languages of documents, then „Andere Sprache …“.</summary>
    public static IReadOnlyList<DocumentLanguage> Languages { get; } =
    [
        .. new (string Tag, string Name, Flag Flag)[]
        {
            ("de", "Deutsch", Stripes("#000000", "#DD0000", "#FFCE00")),
            ("de-DE", "Deutsch (Deutschland)", Stripes("#000000", "#DD0000", "#FFCE00")),
            ("de-AT", "Deutsch (Österreich)", Stripes("#ED2939", "#FFFFFF", "#ED2939")),
            ("de-CH", "Deutsch (Schweiz)", Flag.None),
            ("en", "English", UnionJack),
            ("en-GB", "English (UK)", UnionJack),
            ("en-US", "English (US)", Flag.None),
            ("fr", "Français", Vertical("#002395", "#FFFFFF", "#ED2939")),
            ("it", "Italiano", Vertical("#009246", "#FFFFFF", "#CE2B37")),
            ("es", "Español", Stripes("#AA151B", "#F1BF00", "#AA151B")),
            ("pt", "Português", Flag.None),
            ("pt-BR", "Português (Brasil)", Flag.None),
            ("nl", "Nederlands", Stripes("#AE1C28", "#FFFFFF", "#21468B")),
            ("pl", "Polski", Stripes("#FFFFFF", "#DC143C")),
            ("cs", "Čeština", Flag.None),
            ("sk", "Slovenčina", Flag.None),
            ("hu", "Magyar", Stripes("#CE2939", "#FFFFFF", "#477050")),
            ("ro", "Română", Vertical("#002B7F", "#FCD116", "#CE1126")),
            ("bg", "Български", Stripes("#FFFFFF", "#00966E", "#D62612")),
            ("hr", "Hrvatski", Flag.None),
            ("sl", "Slovenščina", Flag.None),
            ("da", "Dansk", Flag.None),
            ("sv", "Svenska", Flag.None),
            ("no", "Norsk", Flag.None),
            ("fi", "Suomi", Flag.None),
            ("et", "Eesti", Stripes("#0072CE", "#000000", "#FFFFFF")),
            ("lv", "Latviešu", Flag.None),
            ("lt", "Lietuvių", Stripes("#FDB913", "#006A44", "#C1272D")),
            ("el", "Ελληνικά", Flag.None),
            ("tr", "Türkçe", Flag.None),
            ("ru", "Русский", Stripes("#FFFFFF", "#0039A6", "#D52B1E")),
            ("uk", "Українська", Stripes("#0057B7", "#FFD700")),
            ("ar", "العربية", Flag.None),
            ("he", "עברית", Flag.None),
            ("hi", "ह\u093Fन\u094Dद\u0940", Flag.None),
            ("zh", "中文", Flag.None),
            ("ja", "日本語", Flag.None),
            ("ko", "한국어", Flag.None),
            ("la", "Latina", Flag.None),
        }.Select(l => new DocumentLanguage(new Language(l.Tag, l.Name, l.Flag, new Dictionary<string, string>()))),
        new DocumentLanguage(new Language("", Tr.T("Andere Sprache …"), Flag.None, new Dictionary<string, string>())),
    ];

    private bool _showing;

    public LanguagePicker()
    {
        InitializeComponent();
        Box.ItemsSource = Languages;
        Box.SelectionChanged += (_, _) => OnChosen();
        OwnBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_showing) Value = OwnBox.Text?.Trim() ?? "";
        };
        Show(Value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !_showing) Show(change.GetNewValue<string?>());
    }

    /// <summary>The entry of a tag (ignoring case); null for none in the list.</summary>
    internal static DocumentLanguage? Find(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : Languages.FirstOrDefault(l => !l.IsOther && l.Language.Code.Equals(tag.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows a tag: its entry, or „Andere Sprache …“ with the tag in the box below.</summary>
    private void Show(string? tag)
    {
        _showing = true;
        try
        {
            var entry = Find(tag);
            var other = entry is null && !string.IsNullOrWhiteSpace(tag);
            Box.SelectedItem = entry ?? (other ? Languages[^1] : null);
            OwnBox.Text = other ? tag!.Trim() : "";
            OwnBox.IsVisible = other;
        }
        finally
        {
            _showing = false;
        }
    }

    private void OnChosen()
    {
        if (_showing || Box.SelectedItem is not DocumentLanguage chosen) return;
        if (chosen.IsOther)
        {
            OwnBox.IsVisible = true;
            OwnBox.Focus();
            return;
        }
        OwnBox.IsVisible = false;
        _showing = true;
        try
        {
            Value = chosen.Language.Code;
        }
        finally
        {
            _showing = false;
        }
    }
}
