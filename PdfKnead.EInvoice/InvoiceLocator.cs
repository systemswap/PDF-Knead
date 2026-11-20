using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.EInvoice;

/// <summary>How much a finding of the checker weighs: an „Auffälligkeit“ to look at, or a „Hinweis“ that only informs.</summary>
public enum InvoiceFindingWeight
{
    /// <summary>Informs (something was noticed and deliberately not followed, a fact worth knowing).</summary>
    Note,

    /// <summary>An „Auffälligkeit“: something that deviates and should be looked at — never a judgement on validity.</summary>
    Finding,
}

/// <summary>What a finding of stage 1 is about (KONZEPT.md, section 5.24: finding and reading, cross-checks).</summary>
public enum InvoiceFindingKind
{
    /// <summary>A value holds control, bidi or zero-width characters (stage 2, made visible in the display).</summary>
    HiddenCharacters,

    /// <summary>A payment account holds characters outside A–Z and 0–9 (stage 2).</summary>
    AccountCharacters,

    /// <summary>An IBAN whose check digits do not fit (stage 2).</summary>
    IbanCheckDigits,

    /// <summary>A word of a name, an identifier or a payment detail mixes Latin, Greek or Cyrillic letters (stage 2).</summary>
    MixedScripts,

    /// <summary>More payment groups than the direct debit of the settlement is repeated in (stage 2, FG 826).</summary>
    ManyPaymentGroups,

    /// <summary>A place of the PDF that may hold files is damaged; the rest of it was passed over.</summary>
    SourceDamaged,

    /// <summary>More than one invoice XML in the PDF; which one counts is not decided silently.</summary>
    SeveralInvoices,

    /// <summary>The invoice XML was refused by the hardened reader (the reason is named).</summary>
    Refused,

    // how the invoice is embedded and what the XMP say about it are rules of the specification since stage 4, part h
    // (InvoiceHybridRules: /AF, name tree, file name, XMP values, profile) — no longer findings of stage 1

    /// <summary>
    /// The XMP name an embedded invoice, but the PDF holds no invoice XML (the file is missing, or what is there under that name is none):
    /// without a readable XML no rule can be checked, so stage 1 says it.
    /// </summary>
    XmpWithoutInvoice,

    /// <summary>The invoice XML came in a later revision of the file (added or changed after the first saved state).</summary>
    LaterRevision,

    /// <summary>The PDF is signed, and the invoice XML is not the one of the signed revision (added or changed after signing).</summary>
    OutsideSignature,

    /// <summary>The XML declaration names another encoding than UTF-8; the data was read as UTF-8 nonetheless.</summary>
    DeclaredEncoding,

    /// <summary>The XML refers to outside things (schemaLocation, xi:include, xml:base, processing instructions); none was followed.</summary>
    ReferencesNotFollowed,
}

/// <summary>A finding of the checker: kind, weight and the localised text (never content of the file beyond names and short values).</summary>
public sealed record InvoiceFinding(InvoiceFindingKind Kind, InvoiceFindingWeight Weight, string Text);

/// <summary>
/// The e-invoice of a PDF or of a loose XML file, as found and read in stage 1 (KONZEPT.md, section 5.24): the file that holds it, its data,
/// the read XML and what it is — or why it was refused —, the other invoice XMLs and the other attachments, the XMP invoice data, and the
/// findings of the cross-checks.
/// </summary>
public sealed record InvoiceLocation(
    InvoiceFile? File,
    byte[]? Data,
    InvoiceXmlDocument? Xml,
    InvoiceKind? Kind,
    InvoiceXmlRejection? Refused,
    IReadOnlyList<InvoiceFile> OtherInvoices,
    IReadOnlyList<InvoiceFile> Attachments,
    InvoiceXmp? Xmp,
    IReadOnlyList<InvoiceFinding> Findings)
{
    /// <summary>Whether an invoice XML was found at all (read or refused).</summary>
    public bool HasInvoice => Data is not null;
}

/// <summary>
/// Finds the e-invoice of a PDF and cross-checks what the PDF says about it (KONZEPT.md, section 5.24, stage 1): every embedded file that
/// looks like XML is read with the hardened reader; those of an invoice syntax are the candidates. Several are reported, never resolved
/// silently — the one the XMP names counts, else the one associated in the catalog's /AF, else the first; the file name never decides. How the
/// invoice is embedded and what the XMP say about it are checked by the rules of the specification (<see cref="InvoiceHybridRules"/>, stage 4,
/// part h). Revisions: an invoice XML that is not already in an earlier saved state of the file, or not in the signed revision of a signed PDF,
/// is reported. Nothing is followed outside the PDF, nothing is changed (P4).
/// </summary>
public static class InvoiceLocator
{
    /// <summary>Most revisions compared (the latest ones; each is opened as a document of its own).</summary>
    public const int MaxRevisions = 32;

    /// <summary>Finds and reads the invoice of a PDF.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="limits">Limits of the XML reader; null for the usual ones.</param>
    /// <param name="cancel">Cancels the search.</param>
    public static InvoiceLocation Locate(PdfDocument doc, InvoiceXmlLimits? limits = null, CancellationToken cancel = default)
    {
        limits ??= InvoiceXmlLimits.Default;
        var search = InvoiceSources.Find(doc, cancel);
        var findings = new List<InvoiceFinding>();
        foreach (var note in search.Notes) findings.Add(new(InvoiceFindingKind.SourceDamaged, InvoiceFindingWeight.Finding, note));

        // every file that looks like XML is read; an invoice syntax makes it a candidate
        var candidates = new List<(InvoiceFile File, byte[] Data, InvoiceXmlDocument? Xml, InvoiceKind? Kind, InvoiceXmlRejection? Refused)>();
        var attachments = new List<InvoiceFile>();
        foreach (var file in search.Files)
        {
            cancel.ThrowIfCancellationRequested();
            if (InvoiceSources.Data(doc, file, limits.MaxBytes) is not { } data || !InvoiceXml.LooksLikeXml(data))
            {
                attachments.Add(file);
                continue;
            }
            try
            {
                var xml = InvoiceXml.Read(data, limits, cancel);
                var kind = InvoiceKind.Of(xml);
                if (kind.Syntax is InvoiceSyntax.Unknown) attachments.Add(file);
                else candidates.Add((file, data, xml, kind, null));
            }
            catch (InvoiceXmlException e) when (e.Reason is InvoiceXmlRejection.Cancelled)
            {
                throw new OperationCanceledException(cancel);
            }
            catch (InvoiceXmlException e)
            {
                // a refused XML may be the invoice (a crafted one is refused on purpose): it stays a candidate, with its reason
                candidates.Add((file, data, null, null, e.Reason));
            }
        }

        if (candidates.Count == 0)
        {
            // the XMP promise an invoice the PDF does not hold
            if (search.Xmp is { Namespace.Length: > 0 } promised)
            {
                var named = promised.DocumentFileName;
                var there = named is null ? null : attachments.FirstOrDefault(a => a.UnicodeName == named || a.Name == named);
                findings.Add(new(InvoiceFindingKind.XmpWithoutInvoice, InvoiceFindingWeight.Finding, there is not null
                    ? Tr.T("Die Metadaten (XMP) nennen „{0}“ als eingebettete Rechnung; die Datei ist da, aber keine Rechnungs-XML.", Short(named!))
                    : Tr.T("Die Metadaten (XMP) nennen eine eingebettete Rechnung („{0}“), die Datei fehlt in der PDF.", Short(named ?? "–"))));
            }
            return new InvoiceLocation(null, null, null, null, null, [], attachments, search.Xmp, findings);
        }

        // the one that counts: named by the XMP, else associated in /AF, else read, else the first
        var chosen = candidates
            .OrderByDescending(c => search.Xmp?.DocumentFileName is { } named && (c.File.UnicodeName == named || c.File.Name == named))
            .ThenByDescending(c => c.File.Places.Any(p => p.Place == InvoiceFilePlace.CatalogAf))
            .ThenByDescending(c => c.Kind is not null)
            .ThenBy(c => c.File.ObjectNumber)
            .First();
        var others = candidates.Where(c => !ReferenceEquals(c.File, chosen.File)).Select(c => c.File).ToList();
        if (others.Count > 0)
            findings.Add(new(InvoiceFindingKind.SeveralInvoices, InvoiceFindingWeight.Finding,
                Tr.T("{0} Rechnungs-XML im PDF; angezeigt wird „{1}“ — bitte prüfen, welche gilt.", candidates.Count, Short(chosen.File.DisplayName))));

        AddReaderFindings(findings, chosen.Xml, chosen.Refused);
        CheckRevisions(findings, doc, chosen.Data, cancel);
        return new InvoiceLocation(chosen.File, chosen.Data, chosen.Xml, chosen.Kind, chosen.Refused, others, attachments, search.Xmp, findings);
    }

    /// <summary>Reads a loose invoice XML (an XRechnung sent as a file of its own).</summary>
    /// <param name="data">The bytes of the file.</param>
    /// <param name="limits">Limits of the XML reader; null for the usual ones.</param>
    /// <param name="cancel">Cancels reading.</param>
    public static InvoiceLocation Read(byte[] data, InvoiceXmlLimits? limits = null, CancellationToken cancel = default)
    {
        var findings = new List<InvoiceFinding>();
        try
        {
            var xml = InvoiceXml.Read(data, limits, cancel);
            AddReaderFindings(findings, xml, null);
            return new InvoiceLocation(null, data, xml, InvoiceKind.Of(xml), null, [], [], null, findings);
        }
        catch (InvoiceXmlException e) when (e.Reason is InvoiceXmlRejection.Cancelled)
        {
            throw new OperationCanceledException(cancel);
        }
        catch (InvoiceXmlException e)
        {
            AddReaderFindings(findings, null, e.Reason);
            return new InvoiceLocation(null, data, null, null, e.Reason, [], [], null, findings);
        }
    }

    /// <summary>A short name of a refusal for messages.</summary>
    public static string Describe(InvoiceXmlRejection reason) => reason switch
    {
        InvoiceXmlRejection.TooLarge => Tr.T("zu groß"),
        InvoiceXmlRejection.NotUtf8 => Tr.T("nicht in UTF-8"),
        InvoiceXmlRejection.Doctype => Tr.T("enthält eine DTD (DOCTYPE) — Zeichen eines Angriffs"),
        InvoiceXmlRejection.Malformed => Tr.T("kein wohlgeformtes XML"),
        InvoiceXmlRejection.TooDeep => Tr.T("zu tief verschachtelt"),
        InvoiceXmlRejection.TooManyElements => Tr.T("zu viele Elemente"),
        InvoiceXmlRejection.TooManyAttributes => Tr.T("zu viele Attribute an einem Element"),
        InvoiceXmlRejection.AttributeTooLong => Tr.T("ein Attributwert ist zu lang"),
        InvoiceXmlRejection.NameTooLong => Tr.T("ein Name ist zu lang"),
        InvoiceXmlRejection.TooManyNames => Tr.T("zu viele verschiedene Namen"),
        InvoiceXmlRejection.TooManyNamespaces => Tr.T("zu viele Namensräume"),
        InvoiceXmlRejection.TextTooLong => Tr.T("ein Text ist zu lang"),
        InvoiceXmlRejection.BinaryTooLarge => Tr.T("ein Anhang ist zu groß"),
        InvoiceXmlRejection.TooComplex => Tr.T("zu aufwendig zu lesen"),
        _ => Tr.T("abgebrochen"),
    };

    private static void AddReaderFindings(List<InvoiceFinding> findings, InvoiceXmlDocument? xml, InvoiceXmlRejection? refused)
    {
        if (refused is { } reason)
        {
            findings.Add(new(InvoiceFindingKind.Refused, InvoiceFindingWeight.Finding, Tr.T("Die Rechnungs-XML wurde nicht gelesen: {0}.", Describe(reason))));
            return;
        }
        if (xml is null) return;
        if (xml.Notes.DeclaredEncoding is { } encoding && !encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
            findings.Add(new(InvoiceFindingKind.DeclaredEncoding, InvoiceFindingWeight.Finding,
                Tr.T("Die XML-Deklaration nennt die Kodierung „{0}“; gelesen wurde als UTF-8, wie die Rechnungsformate es verlangen.", Short(encoding))));
        var outside = new List<string>();
        if (xml.Notes.SchemaLocation) outside.Add("xsi:schemaLocation");
        if (xml.Notes.XInclude) outside.Add("xi:include");
        if (xml.Notes.XmlBase) outside.Add("xml:base");
        if (xml.Notes.ProcessingInstructions) outside.Add(Tr.T("Verarbeitungsanweisungen"));
        if (outside.Count > 0)
            findings.Add(new(InvoiceFindingKind.ReferencesNotFollowed, InvoiceFindingWeight.Note,
                Tr.T("Die XML verweist nach außen ({0}); keinem Verweis wurde gefolgt.", string.Join(", ", outside))));
    }

    /// <summary>The profile an XMP conformance level names (Factur-X and ZUGFeRD 2: MINIMUM … XRECHNUNG; ZUGFeRD 1: BASIC, COMFORT, EXTENDED).</summary>
    internal static InvoiceProfile? LevelOf(string level, bool v1)
    {
        var l = string.Join(' ', level.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return l switch
        {
            "MINIMUM" => InvoiceProfile.Minimum,
            "BASIC WL" or "BASICWL" or "BASIC-WL" => InvoiceProfile.BasicWl,
            "BASIC" => v1 ? InvoiceProfile.ZugferdV1Basic : InvoiceProfile.Basic,
            "COMFORT" => v1 ? InvoiceProfile.ZugferdV1Comfort : InvoiceProfile.En16931,
            "EN 16931" or "EN16931" => InvoiceProfile.En16931,
            "EXTENDED" => v1 ? InvoiceProfile.ZugferdV1Extended : InvoiceProfile.Extended,
            "XRECHNUNG" => InvoiceProfile.XRechnung,
            _ => null,
        };
    }

    /// <summary>Whether the profile a level names is the one the XML declares (EXTENDED also stands for its French form EXTENDED-CTC-FR).</summary>
    internal static bool SameProfile(InvoiceProfile declared, InvoiceProfile xml) =>
        declared == xml || (declared == InvoiceProfile.Extended && xml == InvoiceProfile.ExtendedCtcFr);

    /// <summary>
    /// Whether the invoice data were already in the earlier saved states of the file, compared by the decoded data (an unchanged invoice under a
    /// new object number counts as the same). A signed PDF whose signed revision lacks them, or holds other ones, is an „Auffälligkeit“. An
    /// invoice XML replaced in a later revision — an earlier state held another one — is one as well. An invoice XML only added later, the
    /// states before holding none, is a „Hinweis“: generators often attach the XML in an incremental update (38 of the samples of the
    /// collections, 13.11.2026).
    /// </summary>
    private static void CheckRevisions(List<InvoiceFinding> findings, PdfDocument doc, byte[] data, CancellationToken cancel)
    {
        var ends = RevisionEnds(doc);
        if (ends.Count == 0) return;
        // the signed revision: the end of the widest intact signature
        long signedEnd = 0;
        try
        {
            foreach (var s in DigitalSignatures.Find(doc))
                if (s.Intact) signedEnd = Math.Max(signedEnd, s.ByteRange[2] + s.ByteRange[3]);
        }
        catch (PdfException)
        {
        }
        if (signedEnd > 0 && signedEnd < doc.FileSize && StateOf(doc, signedEnd, data, cancel) is RevisionState.Other or RevisionState.None)
            findings.Add(new(InvoiceFindingKind.OutsideSignature, InvoiceFindingWeight.Finding,
                Tr.T("Das PDF ist signiert, aber die Rechnungs-XML gehört nicht zur signierten Fassung — sie kam danach hinzu oder wurde danach geändert.")));
        var replaced = false;
        var opened = false;
        for (var i = 0; i < ends.Count; i++)
        {
            var state = StateOf(doc, ends[i], data, cancel);
            if (state == RevisionState.NotAState) continue;   // the first part of a linearised file, a damaged state
            if (state == RevisionState.Same)
            {
                if (opened) Report(i + 1);
                return;
            }
            opened = true;
            replaced |= state == RevisionState.Other;
        }
        if (opened) Report(ends.Count + 1);

        void Report(int revision) => findings.Add(replaced
            ? new(InvoiceFindingKind.LaterRevision, InvoiceFindingWeight.Finding,
                Tr.T("Die Rechnungs-XML wurde in einer späteren Revision der Datei ersetzt (Revision {0} von {1}); eine frühere Fassung enthält eine andere.", revision, ends.Count + 1))
            : new(InvoiceFindingKind.LaterRevision, InvoiceFindingWeight.Note,
                Tr.T("Die Rechnungs-XML kam mit einer späteren Revision der Datei hinzu (Revision {0} von {1}); die Fassungen davor enthalten keine.", revision, ends.Count + 1)));
    }

    /// <summary>What an earlier saved state holds of the invoice.</summary>
    private enum RevisionState
    {
        /// <summary>The same invoice data.</summary>
        Same,

        /// <summary>Another invoice XML, not the same data.</summary>
        Other,

        /// <summary>No invoice XML.</summary>
        None,

        /// <summary>The state cannot be opened on its own.</summary>
        NotAState,
    }

    /// <summary>What the state of the file up to <paramref name="end"/> holds of the invoice data.</summary>
    private static RevisionState StateOf(PdfDocument doc, long end, byte[] data, CancellationToken cancel)
    {
        try
        {
            using var revision = doc.OpenRevision(end);
            if (revision.WasRepaired) return RevisionState.NotAState;
            var other = false;
            foreach (var file in InvoiceSources.Find(revision, cancel).Files)
            {
                if (InvoiceSources.Data(revision, file, InvoiceXmlLimits.Default.MaxBytes) is not { } earlier) continue;
                if (earlier.AsSpan().SequenceEqual(data)) return RevisionState.Same;
                other |= InvoiceXml.LooksLikeXml(earlier) && IsInvoice(earlier, cancel);
            }
            return other ? RevisionState.Other : RevisionState.None;
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException or ArgumentException)
        {
            return RevisionState.NotAState;
        }
    }

    /// <summary>Whether data are an XML of an invoice syntax (read with the hardened reader; a refused one counts as none).</summary>
    private static bool IsInvoice(byte[] data, CancellationToken cancel)
    {
        try
        {
            return InvoiceKind.Of(InvoiceXml.Read(data, null, cancel)).Syntax is not InvoiceSyntax.Unknown;
        }
        catch (InvoiceXmlException e) when (e.Reason is not InvoiceXmlRejection.Cancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// The ends of the earlier saved states: after each „%%EOF“ with its line end, except one that only white space follows (the file as it
    /// is); the latest <see cref="MaxRevisions"/>. Read in blocks, so a large file is never held twice.
    /// </summary>
    internal static List<long> RevisionEnds(PdfDocument doc)
    {
        var marker = "%%EOF"u8;
        var ends = new List<long>();
        const int block = 1 << 20;
        var size = doc.FileSize;
        for (long at = 0; at < size; at += block - marker.Length)
        {
            var span = doc.FileBytes(at, Math.Min(block, size - at));
            var from = 0;
            while (from < span.Length && span[from..].IndexOf(marker) is var i and >= 0)
            {
                var end = at + from + i + marker.Length;
                // the line end belongs to the state
                var rest = doc.FileBytes(end, Math.Min(2, size - end));
                var k = 0;
                if (k < rest.Length && rest[k] == '\r') k++;
                if (k < rest.Length && rest[k] == '\n') k++;
                end += k;
                if (ends.Count == 0 || ends[^1] < end) ends.Add(end);
                from += i + 1;
            }
            if (at + block >= size) break;
        }
        // a marker that only white space follows ends the file as it is: not an earlier state
        while (ends.Count > 0 && OnlyWhiteSpaceAfter(doc, ends[^1])) ends.RemoveAt(ends.Count - 1);
        return ends.Count > MaxRevisions ? ends[^MaxRevisions..] : ends;
    }

    private static bool OnlyWhiteSpaceAfter(PdfDocument doc, long end)
    {
        var length = doc.FileSize - end;
        if (length > 4096) return false;
        foreach (var b in doc.FileBytes(end, length))
            if (b is not (0 or 9 or 10 or 12 or 13 or 32)) return false;
        return true;
    }

    /// <summary>A value of the file shortened for a message.</summary>
    /// <summary>A name or short value of the file for a message: hidden characters made visible, cut after 80 characters (Bauvorgaben Sicherheit 8).</summary>
    private static string Short(string value) => InvoiceFormat.Plain(value, 80);
}
