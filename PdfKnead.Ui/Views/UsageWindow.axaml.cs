using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Licensing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// The question about the kind of use (LICENSE, section 3 (3)); for an authority or commercial use, then the notice about the eight licensed
/// features with the days of the introductory period left. Returns the choice, or null when closed without one.
/// </summary>
public sealed partial class UsageWindow : Window
{
    private UsageKind _usage;

    public UsageWindow() : this(UsageKind.NotAsked)
    {
    }

    /// <param name="current">The kind of use declared so far (preselected).</param>
    public UsageWindow(UsageKind current)
    {
        InitializeComponent();
        foreach (var (box, kind) in Boxes())
        {
            box.IsChecked = kind == current;
            box.IsCheckedChanged += (_, _) => NextButton.IsEnabled = Chosen() is not UsageKind.NotAsked;
        }
        NextButton.IsEnabled = current is not UsageKind.NotAsked;
    }

    private IEnumerable<(RadioButton Box, UsageKind Kind)> Boxes() =>
    [
        (PrivateBox, UsageKind.Private), (NonProfitBox, UsageKind.NonProfit), (EducationBox, UsageKind.Education),
        (AuthorityBox, UsageKind.Authority), (CommercialBox, UsageKind.Commercial),
    ];

    private UsageKind Chosen() => Boxes().FirstOrDefault(b => b.Box.IsChecked == true).Kind;

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        _usage = Chosen();
        if (_usage is UsageKind.NotAsked) return;
        if (LicensePolicy.IsFreeUse(_usage))
        {
            Close(new UsageChoice(_usage, false));
            return;
        }
        // nothing needs a license (LicensePolicy.Licensed empty): no notice
        if (!Services.Licensing.NeedsLicense(_usage))
        {
            Close(new UsageChoice(_usage, false));
            return;
        }
        // chosen now: the trial would start today unless a commercial use applied before
        var context = Services.Licensing.Context();
        if (context.CommercialSince is null) context = context with { CommercialSince = context.Today };
        var days = LicensePolicy.DaysLeft(context.Today, context.FreeUntil);
        LicensedText.Text = Tr.T("PDF-Knead ist für Sie kostenlos. Für Firmen, Selbstständige und Behörden sind diese Funktionen lizenzpflichtig: {0}.", Services.Licensing.LicensedList());
        PeriodText.Text = days <= 0 ? Tr.T("Die kostenlose Zeit endete am {0:d}; diese Funktionen brauchen jetzt eine Lizenz.", context.FreeUntil)
            : context.IsTrial ? Tr.T("Sie können sie {2} Monate kostenlos testen: bis zum {0:d} (noch {1} Tage). Danach sperren sie sich, bis eine Lizenz eingetragen ist.", context.FreeUntil, days, LicensePolicy.TrialMonths)
            : Tr.T("Bis zum {0:d} dürfen Sie auch diese kostenlos nutzen (noch {1} Tage). Danach sperren sie sich, bis eine Lizenz eingetragen ist.", context.FreeUntil, days);
        ChoicePage.IsVisible = false;
        NoticePage.IsVisible = true;
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        NoticePage.IsVisible = false;
        ChoicePage.IsVisible = true;
    }

    private void OnLicense(object? sender, RoutedEventArgs e) => Close(new UsageChoice(_usage, true));

    private void OnUnderstood(object? sender, RoutedEventArgs e) => Close(new UsageChoice(_usage, false));
}
