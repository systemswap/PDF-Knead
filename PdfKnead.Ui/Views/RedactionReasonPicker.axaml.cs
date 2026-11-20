using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Views;

/// <summary>
/// The reason of redactions (wish of 4.11.2026, docs/OFFENE-PUNKTE.md, „Schwärzungscodes — Ablauf festgelegt“): the country's code set, a
/// code with its meaning shown beneath, an own reason that replaces the code, „Code im Balken anzeigen“ and, in „Suchen und schwärzen“, a
/// code per kind of hit. Warns at once where the code does not fit into a bar (<see cref="Bars"/>) and where an own reason names what is
/// redacted (<see cref="Redacted"/>). Used in „Suchen und schwärzen“ and in the panel of a selected mark.
/// </summary>
public sealed partial class RedactionReasonPicker : UserControl
{
    /// <summary>The reason chosen; null: none. Two-way: set from outside, the boxes show it.</summary>
    public static readonly StyledProperty<ReasonChoice?> ChoiceProperty =
        AvaloniaProperty.Register<RedactionReasonPicker, ReasonChoice?>(nameof(Choice), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>The bars the reason goes into (page space), for the warning where it does not fit; null: no check.</summary>
    public static readonly StyledProperty<IReadOnlyList<PdfRectangle>?> BarsProperty =
        AvaloniaProperty.Register<RedactionReasonPicker, IReadOnlyList<PdfRectangle>?>(nameof(Bars));

    /// <summary>What is redacted, for the warning when an own reason names it; null: no check.</summary>
    public static readonly StyledProperty<IEnumerable<string>?> RedactedProperty =
        AvaloniaProperty.Register<RedactionReasonPicker, IEnumerable<string>?>(nameof(Redacted));

    /// <summary>Whether „Code je Fundart automatisch setzen“ is offered (only in „Suchen und schwärzen“).</summary>
    public static readonly StyledProperty<bool> OffersAutoProperty = AvaloniaProperty.Register<RedactionReasonPicker, bool>(nameof(OffersAuto));

    /// <summary>The entry „no reason“ at the top of the codes.</summary>
    private static readonly RedactionReasonCode None = new("", "", "", "");

    private readonly List<RedactionCodeSet> _sets;
    private bool _showing;

    public RedactionReasonPicker()
    {
        InitializeComponent();
        OwnBox.PropertyChanged += OnOwnChanged;
        _sets = RedactionCodes.Load();
        SetBox.ItemsSource = _sets;
        var settings = Services.UserSettings.Current;
        var id = settings.RedactionCodeSet ?? RedactionCodes.DefaultFor(System.Globalization.CultureInfo.CurrentCulture.Name);
        _showing = true;
        SetBox.SelectedItem = _sets.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? _sets.FirstOrDefault();
        FillCodes();
        InBarBox.IsChecked = settings.RedactionCodeInBar;
        _showing = false;
        Update();
    }

    public ReasonChoice? Choice
    {
        get => GetValue(ChoiceProperty);
        set => SetValue(ChoiceProperty, value);
    }

    public IReadOnlyList<PdfRectangle>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public IEnumerable<string>? Redacted
    {
        get => GetValue(RedactedProperty);
        set => SetValue(RedactedProperty, value);
    }

    public bool OffersAuto
    {
        get => GetValue(OffersAutoProperty);
        set => SetValue(OffersAutoProperty, value);
    }

    /// <summary>The code set whose codes the hits of a pattern get (E-Mail, IBAN …); null unless „Code je Fundart automatisch setzen“ is ticked.</summary>
    public RedactionCodeSet? Auto => OffersAuto && AutoBox.IsChecked == true ? SetBox.SelectedItem as RedactionCodeSet : null;

    /// <summary>The code sets offered (for tests).</summary>
    internal IReadOnlyList<RedactionCodeSet> Sets => _sets;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChoiceProperty && !_showing) Show(change.GetNewValue<ReasonChoice?>());
        else if (change.Property == BarsProperty || change.Property == RedactedProperty) Warn();
        else if (change.Property == OffersAutoProperty) AutoBox.IsVisible = OffersAuto;
    }

    /// <summary>Shows a reason in the boxes: its code in the set that has it (the current one first), else as an own reason.</summary>
    private void Show(ReasonChoice? choice)
    {
        _showing = true;
        try
        {
            if (choice is { Code: { } code })
            {
                var set = new[] { SetBox.SelectedItem as RedactionCodeSet }.Concat(_sets).OfType<RedactionCodeSet>()
                    .FirstOrDefault(s => s.Codes.Any(c => c.Code == code));
                if (set is not null && !ReferenceEquals(set, SetBox.SelectedItem))
                {
                    SetBox.SelectedItem = set;
                    FillCodes();
                }
                CodeBox.SelectedItem = set?.Codes.First(c => c.Code == code) ?? None;
                OwnBox.Text = set is null ? choice.Reason : "";
            }
            else
            {
                CodeBox.SelectedItem = None;
                OwnBox.Text = choice?.Reason ?? "";
            }
            if (choice is not null) InBarBox.IsChecked = choice.InBar;
        }
        finally
        {
            _showing = false;
        }
        Describe();
        Warn();
    }

    private void FillCodes()
    {
        var codes = new List<RedactionReasonCode> { None };
        if (SetBox.SelectedItem is RedactionCodeSet set) codes.AddRange(set.Codes);
        CodeBox.ItemsSource = codes;
        CodeBox.SelectedItem = None;
    }

    private void OnSetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_showing) return;
        FillCodes();
        if (SetBox.SelectedItem is RedactionCodeSet set) Services.UserSettings.Update(s => s with { RedactionCodeSet = set.Id });
        Update();
    }

    private void OnCodeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_showing) Update();
    }

    // the text itself, not TextChanged (raised later): the choice and the warnings follow every keystroke at once
    private void OnOwnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty && !_showing) Update();
    }

    private void OnInBarChanged(object? sender, RoutedEventArgs e)
    {
        if (_showing) return;
        Services.UserSettings.Update(s => s with { RedactionCodeInBar = InBarBox.IsChecked == true });
        Update();
    }

    private void OnAutoChanged(object? sender, RoutedEventArgs e) => Warn();

    /// <summary>The boxes changed: the choice follows (an own reason replaces the code, which is then shown greyed).</summary>
    private void Update()
    {
        var own = RedactionReasons.Clean(OwnBox.Text);
        var inBar = InBarBox.IsChecked == true;
        var choice = own is not null ? new ReasonChoice(own, null, null, inBar)
            : CodeBox.SelectedItem is RedactionReasonCode { Code.Length: > 0 } code ? ReasonChoice.Of(code, inBar) : null;
        _showing = true;
        try
        {
            SetCurrentValue(ChoiceProperty, choice);
        }
        finally
        {
            _showing = false;
        }
        Describe();
        Warn();
    }

    /// <summary>The meaning and legal basis of the code under the boxes; the codes greyed while an own reason replaces them.</summary>
    private void Describe()
    {
        var own = RedactionReasons.Clean(OwnBox.Text) is not null;
        CodeBox.Opacity = own ? 0.5 : 1;
        if (!own && CodeBox.SelectedItem is RedactionReasonCode { Code.Length: > 0 } code)
        {
            MeaningText.Text = code.Meaning + (code.Basis.Length > 0 ? " — " + code.Basis : "");
            MeaningText.IsVisible = MeaningText.Text.Length > 0;
        }
        else MeaningText.IsVisible = false;
    }

    /// <summary>Warns where the code (or own reason) does not fit into a bar, and when an own reason names what is redacted.</summary>
    private void Warn()
    {
        var choice = Choice;
        var text = choice is { InBar: true } ? choice.Code ?? choice.Reason : null;
        var bars = Bars;
        var tooSmall = text is null || bars is null ? 0 : bars.Count(b => !RedactionReasons.FitsInBar(text, b));
        FitWarning.IsVisible = tooSmall > 0;
        FitWarning.Text = tooSmall == 0 ? ""
            : bars!.Count == 1 ? Tr.T("Passt nicht in den Balken — dort wird er weggelassen, im Schwärzungsprotokoll steht er.")
            : Tr.T("Passt in {0} von {1} Balken nicht — dort wird er weggelassen, im Schwärzungsprotokoll steht er.", tooSmall, bars.Count);
        var revealing = choice is { Code: null } && Redacted is { } redacted ? RedactionReasons.Revealing([choice.Reason], redacted) : [];
        RevealWarning.IsVisible = revealing.Count > 0;
        RevealWarning.Text = revealing.Count > 0
            ? Tr.T("Achtung: „{0}“ steht auch im geschwärzten Text — ein Grund oder Name verriete dann, was weg ist.", string.Join("“, „", revealing.Take(3)))
            : "";
    }
}
