using System.Text;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Security;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>Where a link leads (ISO 32000-2, 12.5.6.5 and 12.6.4).</summary>
public abstract record LinkTarget;

/// <summary>A place in this document (/Dest, GoTo with /SD or /D): followed without a question.</summary>
public sealed record InternalLink(PdfDestination Destination) : LinkTarget;

/// <summary>A named action: NextPage, PrevPage, FirstPage, LastPage.</summary>
public sealed record NamedLink(string Action) : LinkTarget;

/// <summary>
/// A URI action: the address resolved (against /URI /Base of the catalogue), its check, whether it may be opened (http, https, mailto,
/// never without a question) and whether the click position is appended (/IsMap).
/// </summary>
public sealed record AddressLink(string Uri, AddressInfo Check, bool IsMap) : LinkTarget
{
    /// <summary>What is opened after the question; null if PDF-Knead does not open this address (only shown and copied).</summary>
    public string? OpenTarget => Check.Target;
}

/// <summary>Everything else (GoToR, Launch, JavaScript, SubmitForm …): shown, never carried out.</summary>
public sealed record OtherLink(string Kind, string? Detail) : LinkTarget;

/// <summary>A link of a page: its areas (quadrilaterals as 8 numbers each, or the rectangle) in page space, and its target.</summary>
public sealed record PageLink(IReadOnlyList<double[]> Areas, PdfRectangle Rect, LinkTarget Target)
{
    /// <summary>Whether a point of the page lies on the link.</summary>
    public bool Contains(double x, double y) => Areas.Any(q => InQuad(q, x, y));

    private static bool InQuad(double[] q, double x, double y)
    {
        // the four corners in any order of winding: inside if on the same side of every edge (convex quadrilateral)
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var (x1, y1, x2, y2) = (q[2 * i], q[2 * i + 1], q[(2 * i + 2) % 8], q[(2 * i + 3) % 8]);
            var cross = (x2 - x1) * (y - y1) - (y2 - y1) * (x - x1);
            if (cross == 0) continue;
            var s = Math.Sign(cross);
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }
}

/// <summary>
/// The links of a page (ISO 32000-2, 12.5.6.5): /Link annotations with their areas — /QuadPoints only where every point lies within /Rect,
/// otherwise /Rect — and their target, /A before /Dest (docs/ABLAUFPLAN.md, 7.3 c; docs/FEHLERGEDAECHTNIS.md, No. 565).
/// </summary>
public static class PageLinks
{
    private const int MaxLinks = 10_000;

    /// <summary>The links of a page; empty if it has none or they cannot be read.</summary>
    public static IReadOnlyList<PageLink> Of(PdfDocument doc, int pageIndex)
    {
        var result = new List<PageLink>();
        if (pageIndex < 0 || pageIndex >= doc.PageCount || doc.Pages[pageIndex].Dictionary.Get(PdfName.Get("Annots")) is not PdfArray annots) return result;
        try
        {
            for (var i = 0; i < annots.Count && result.Count < MaxLinks; i++)
            {
                if (annots.Get(i) is not PdfDictionary annot || annot.GetName(PdfName.Subtype)?.Value != "Link") continue;
                if (annot.GetArray(PdfName.Get("Rect")) is not { Count: 4 } r || PdfRectangle.From(r) is not { } rect) continue;
                var target = TargetOf(doc, annot);
                if (target is null) continue;
                result.Add(new PageLink(Areas(annot, rect), rect, target));
            }
        }
        catch (PdfException)
        {
            // a damaged annotation list: what was read so far
        }
        return result;
    }

    /// <summary>The areas: the quadrilaterals of /QuadPoints if all their points lie within /Rect (Table 176), otherwise the rectangle.</summary>
    private static List<double[]> Areas(PdfDictionary annot, PdfRectangle rect)
    {
        var (left, bottom, right, top) = (Math.Min(rect.Left, rect.Right), Math.Min(rect.Bottom, rect.Top), Math.Max(rect.Left, rect.Right), Math.Max(rect.Bottom, rect.Top));
        if (annot.GetArray(PdfName.Get("QuadPoints")) is { Count: >= 8 } quads && quads.Count % 8 == 0)
        {
            var list = new List<double[]>();
            var inside = true;
            for (var q = 0; q + 8 <= quads.Count && inside && q < 8 * 1000; q += 8)
            {
                var points = new double[8];
                for (var k = 0; k < 8; k++)
                {
                    if (quads.GetNumber(q + k) is not { } v || !double.IsFinite(v)) { inside = false; break; }
                    points[k] = v;
                }
                for (var k = 0; k < 8 && inside; k += 2)
                    inside = points[k] >= left - 0.5 && points[k] <= right + 0.5 && points[k + 1] >= bottom - 0.5 && points[k + 1] <= top + 0.5;
                list.Add(points);
            }
            if (inside && list.Count > 0) return list;
        }
        return [[left, bottom, right, bottom, right, top, left, top]];
    }

    private static LinkTarget? TargetOf(PdfDocument doc, PdfDictionary annot)
    {
        // an action wins over /Dest (12.5.6.5, Table 176)
        if (annot.GetDictionary(PdfName.Get("A")) is { } action)
        {
            var kind = action.GetName(PdfName.Get("S"))?.Value ?? "";
            switch (kind)
            {
                case "GoTo":
                    return PdfOutline.Target(doc, null, action) is { } destination ? new InternalLink(destination) : null;
                case "Named":
                    var name = action.GetName(PdfName.Get("N"))?.Value;
                    return name is "NextPage" or "PrevPage" or "FirstPage" or "LastPage" ? new NamedLink(name) : new OtherLink("Named", name);
                case "URI":
                    return Address(doc, action);
                default:
                    return new OtherLink(kind.Length > 0 ? kind : "?", Detail(action));
            }
        }
        return PdfOutline.Destination(doc, annot.Get(PdfName.Get("Dest"))) is { } dest ? new InternalLink(dest) : null;
    }

    /// <summary>What a foreign action names, for display (a file of GoToR or Launch); null otherwise.</summary>
    private static string? Detail(PdfDictionary action) => action.Get(PdfName.Get("F")) switch
    {
        PdfString s => s.ToText(),
        PdfDictionary spec => (spec.GetString(PdfName.Get("UF")) ?? spec.GetString(PdfName.Get("F")))?.ToText(),
        _ => null,
    };

    /// <summary>
    /// A URI action (12.6.4.8, Tables 210 and 211): the bytes as UTF-8 (in PDF 2.0; invalid UTF-8 read as Latin-1, with a warning), a relative
    /// address resolved against /URI /Base of the catalogue — without a base it names a local file and is never opened.
    /// </summary>
    private static AddressLink Address(PdfDocument doc, PdfDictionary action)
    {
        var bytes = action.GetString(PdfName.Get("URI"))?.Bytes ?? [];
        string uri;
        var extra = new List<string>();
        try
        {
            uri = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            uri = Encoding.Latin1.GetString(bytes);
            extra.Add(Tr.T("Die Adresse ist nicht als UTF-8 gespeichert."));
        }
        uri = uri.Trim();
        var isMap = action.GetBoolean(PdfName.Get("IsMap")) == true;
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            var baseUri = doc.Catalog?.GetDictionary(PdfName.Get("URI"))?.GetString(PdfName.Get("Base"))?.ToText();
            if (baseUri is not null && System.Uri.TryCreate(baseUri, UriKind.Absolute, out var root) && System.Uri.TryCreate(root, uri, out var resolved))
                uri = resolved.AbsoluteUri;
            else
                return new AddressLink(uri, new AddressInfo(uri, null, null, null,
                    [.. extra, Tr.T("Die Adresse ist relativ und zeigt auf eine Datei neben dem Dokument — so etwas öffnet PDF-Knead nicht.")]), isMap);
        }
        var check = AddressCheck.Check(uri);
        return new AddressLink(uri, extra.Count == 0 ? check : check with { Warnings = [.. extra, .. check.Warnings] }, isMap);
    }
}
