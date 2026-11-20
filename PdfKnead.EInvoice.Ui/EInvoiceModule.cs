using PdfKnead.Modules;

namespace PdfKnead.EInvoice.Ui;

/// <summary>
/// Docks the e-invoice checker into the main window (KONZEPT.md, section 5.24, „Aufbau und Entfernen“): one call from the host program
/// before the window is created — the tab „E-Rechnung“ after „Sicher“, which the user can hide in the settings.
/// </summary>
public static class EInvoiceModule
{
    /// <summary>The key of the tab (also what the settings keep when it is hidden).</summary>
    public const string Key = "erechnung";

    /// <summary>Registers the tab (once; a second call does nothing).</summary>
    public static void Register()
    {
        // the invoice data of the XMP go with the invoice file where a save drops the attachments (whether the tab is shown or not)
        InvoiceSources.RegisterXmpData();
        if (UiModules.Tabs.Any(t => t.Key == Key)) return;
        UiModules.Tabs.Add(new ModuleTab
        {
            Key = Key,
            Header = () => Tr.T("E-Rechnung"),
            CreateContent = main => new EInvoiceRibbon { DataContext = new EInvoiceTabViewModel(main) },
            FirstChange = (main, document) => EInvoiceEditHint.Last = EInvoiceEditHint.ShowAsync(main, document),
            PdfAAdvice = EInvoicePdfAAdvice.AskAsync,
            RedactionElsewhere = EInvoiceRedaction.AskAsync,
            CleanupFinding = EInvoiceCleanup.Find,
        });
    }
}
