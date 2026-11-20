using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Repairs of the check „barrierefrei?“ the writer makes (docs/ABLAUFPLAN.md, 6.7 step 7; docs/FEHLERGEDAECHTNIS.md, No. 668): the
/// configurations of the layers named (ISO 14289-1, 7.10; -2, 8.7).
/// </summary>
public sealed class AccessibilityRepairTests
{
    /// <summary>Layers with a default configuration without a name, one referenced configuration without and one with the name „Ansicht“.</summary>
    private static PdfDocument Layered() => PdfDocument.Open(CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[7 0 R]/D<</Order[7 0 R]>>/Configs[8 0 R<</Name(Ansicht)/Order[7 0 R]>>]>>>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Properties<</L1 7 0 R>>>>>>",
        [6] = ("", "/OC /L1 BDC 0 0 m 100 100 l S EMC"),
        [7] = "<</Type/OCG/Name(Linien)>>",
        [8] = "<</Order[7 0 R]>>",
    }));

    private static PdfDocument Saved(PdfDocument doc, bool name)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { NameLayerConfigurations = name });
        return PdfDocument.Open(output.ToArray());
    }

    private static List<string> Names(PdfDocument doc)
    {
        var layers = doc.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!;
        var configs = layers.GetArray(PdfName.Get("Configs"))!;
        return
        [
            layers.GetDictionary(PdfName.Get("D"))!.GetString(PdfName.Name)?.ToText() ?? "",
            .. Enumerable.Range(0, configs.Count).Select(i => (configs.Get(i) as PdfDictionary)?.GetString(PdfName.Name)?.ToText() ?? ""),
        ];
    }

    [Fact]
    public void NameLayerConfigurations_EveryConfigurationNamed_TakenNamesKept()
    {
        using var doc = Layered();
        using var saved = Saved(doc, true);
        Assert.Equal(["Standard", "Ansicht 2", "Ansicht"], Names(saved));
        Assert.DoesNotContain(AccessibilityCheck.Check(saved, AccessibilityProfile.PdfUa1).Findings, f => f.Id == "7.10-1");
        // the layer itself stays attached to the page content
        Assert.Equal("Linien", ((PdfDictionary)saved.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!.Get(0)!).GetString(PdfName.Name)!.ToText());
    }

    [Fact]
    public void WithoutTheRepair_ConfigurationsAsTheyWere()
    {
        using var doc = Layered();
        using var saved = Saved(doc, false);
        Assert.Equal(["", "", "Ansicht"], Names(saved));
        Assert.Contains(AccessibilityCheck.Check(saved, AccessibilityProfile.PdfUa1).Findings, f => f.Id == "7.10-1");
    }

    /// <summary>The report for the record (6.7 step 6): what was checked, the headline, the findings with page and rule; HTML escaped, without scripts or outside resources.</summary>
    [Fact]
    public void Report_TextAndHtml_SayWhatWasCheckedAndFound()
    {
        var report = new AccessibilityReport(AccessibilityProfile.PdfUa1, 1,
        [
            new AccessibilityFinding("7.1-9", "06-003", AccessibilitySeverity.Error, "Die Datei hat keinen Titel <dc:title>.", null, AccessibilityRepair.Title),
            new AccessibilityFinding("7.18.5-2", "28-012", AccessibilitySeverity.Error, "Ein Link hat kein /Contents.", 3),
            new AccessibilityFinding("H11", "11-007", AccessibilitySeverity.Check, "Sprache prüfen.", null),
        ], AccessibilityCheck.Stages);
        var when = new DateTimeOffset(2026, 11, 9, 14, 30, 0, TimeSpan.FromHours(1));
        var text = AccessibilityReportWriter.Text(report, "Mustermappe & Co.pdf", when);
        Assert.Contains("Mustermappe & Co.pdf", text);
        Assert.Contains(report.Headline, text);
        Assert.Contains("ISO 14289-1, 7.1-9 · Matterhorn 06-003", text);
        Assert.Contains("Seite 3: Ein Link hat kein /Contents.", text);
        Assert.True(text.IndexOf("Muss behoben werden (2)", StringComparison.Ordinal) < text.IndexOf("Bitte prüfen (1)", StringComparison.Ordinal));
        Assert.Contains(AccessibilityCheck.Stages, text);

        var html = AccessibilityReportWriter.Html(report, "Mustermappe & Co.pdf", when, "de");
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<html lang=\"de\">", html);
        Assert.Contains("Mustermappe &amp; Co.pdf", html);
        Assert.Contains("&lt;dc:title&gt;", html);
        Assert.DoesNotContain("<dc:title>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", html.Replace("http-equiv", ""), StringComparison.OrdinalIgnoreCase);
    }
}
