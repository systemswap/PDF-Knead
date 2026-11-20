namespace PdfKnead.Services;

/// <summary>
/// The license texts built into the program (window „Über PDF-Knead“): the own license, the overview of the components of other
/// projects (THIRD-PARTY-NOTICES.md) and the license texts that ship with the program — the same files as in the folder „licenses“.
/// </summary>
public static class LicenseTexts
{
    private const string Prefix = "Lizenzen/", Texts = "Lizenzen/Texte/", Own = "Lizenzen/LICENSE", Notices = "Lizenzen/THIRD-PARTY-NOTICES.md";

    /// <summary>Not a resource: the statement about digital signatures (docs/OFFENE-PUNKTE.md, „QES-Tauglichkeit“, point 6).</summary>
    private const string SignatureNote = "hinweis:signaturen";

    /// <summary>An entry of the list: title and resource name.</summary>
    /// <param name="Title">Shown in the list.</param>
    /// <param name="Resource">Name of the embedded text; null for the own license while it does not exist yet.</param>
    public sealed record Entry(string Title, string? Resource);

    /// <summary>The own license first, then the overview, then the license texts by name.</summary>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<Entry> All()
    {
        var names = typeof(LicenseTexts).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToHashSet();
        var entries = new List<Entry>
        {
            new(Tr.T("Lizenz von PDF-Knead"), names.Contains(Own) ? Own : null),
            new(Tr.T("Fremde Bestandteile (Übersicht)"), Notices),
            new(Tr.T("Digitale Unterschriften: was sie leisten"), SignatureNote),
        };
        entries.AddRange(names.Where(n => n.StartsWith(Texts, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new Entry(Path.GetFileNameWithoutExtension(n[Texts.Length..]), n)));
        return entries;
    }

    /// <summary>The text of an entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The text; for the own license before it exists, a note that it follows.</returns>
    public static string Text(Entry entry)
    {
        if (entry.Resource is null) return Tr.T("Der Lizenztext von PDF-Knead folgt.");
        if (entry.Resource == SignatureNote)
            return Tr.T("PDF-Knead erzeugt digitale Signaturen nach der PDF-Norm (PAdES). Ob eine Signatur einer gesetzlich verlangten Form genügt — etwa der Schriftform —, hängt vom verwendeten Zertifikat und davon ab, wo sein Schlüssel liegt; beides wählen Sie selbst.\n\n• Das eigene Zertifikat, das PDF-Knead anlegt, ist selbst ausgestellt: eine einfache elektronische Signatur. Sie zeigt, dass das Dokument seitdem unverändert ist.\n• Ein Zertifikat einer Zertifizierungsstelle bestätigt Ihren Namen: höchstens eine fortgeschrittene elektronische Signatur; mit einem qualifizierten Zertifikat „fortgeschritten mit qualifiziertem Zertifikat“.\n• Nur eine qualifizierte elektronische Signatur — qualifiziertes Zertifikat eines Anbieters auf der EU-Vertrauensliste, Schlüssel auf einer sicheren Signaturerstellungseinheit — kann die handschriftliche Unterschrift ersetzen (eIDAS Art. 25 Abs. 2).\n• Ein elektronisches Siegel stammt von einer Organisation (Behörde, Firma): Es bestätigt Herkunft und Unversehrtheit, ist aber keine Erklärung einer Person und ersetzt keine persönliche Unterschrift. Auch Siegel gibt es einfach, fortgeschritten und qualifiziert.\n\nWelche Stufe ein Zertifikat gibt, zeigt „Zertifikat“ im Reiter Unterschrift, bei vorhandenen Signaturen „Signaturen“. Ohne geladene EU-Vertrauensliste stützt sich die Stufe auf die Angaben des Zertifikats („laut Zertifikat“); mit geladener Liste prüft PDF-Knead, ob der Anbieter zur Unterschriftszeit qualifiziert war. Dies ist keine Rechtsberatung.");
        using var stream = typeof(LicenseTexts).Assembly.GetManifestResourceStream(entry.Resource);
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
