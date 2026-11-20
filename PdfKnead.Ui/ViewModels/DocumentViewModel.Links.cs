using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.ViewModels;

/// <summary>
/// Links in the view (ISO 32000-2, 12.5.6.5; decision J of 3.11.2026): internal destinations are followed without a question, web and mail
/// addresses always go through the question of the main window, everything else is only named (docs/ABLAUFPLAN.md, 7.3 d;
/// docs/FEHLERGEDAECHTNIS.md, No. 566).
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>A web or mail address of a link was clicked: the main window asks before anything is opened (with the target to open).</summary>
    public event Action<AddressLink, string?>? AddressLinkActivated;

    private readonly Dictionary<PdfDictionary, IReadOnlyList<PageLink>> _links = new(ReferenceEqualityComparer.Instance);

    /// <summary>The links of the page at a position of the view (read once per page).</summary>
    internal IReadOnlyList<PageLink> LinksAt(int position)
    {
        if (position < 0 || position >= Pages.Count) return [];
        var page = Pages[position];
        if (_links.TryGetValue(page.Page.Dictionary, out var known)) return known;
        var links = PageLinks.Of(page.Item.Source, page.Page.Index);
        _links[page.Page.Dictionary] = links;
        return links;
    }

    /// <summary>What a link leads to, for its tooltip.</summary>
    internal static string Describe(LinkTarget target) => target switch
    {
        InternalLink i => Tr.T("Seite {0}", i.Destination.PageIndex + 1),
        NamedLink { Action: "NextPage" } => Tr.T("Nächste Seite"),
        NamedLink { Action: "PrevPage" } => Tr.T("Vorige Seite"),
        NamedLink { Action: "FirstPage" } => Tr.T("Erste Seite"),
        NamedLink => Tr.T("Letzte Seite"),
        AddressLink a => string.Join("\n", [a.Uri, .. a.Check.Warnings]),
        OtherLink o => Tr.T("Wird aus Sicherheitsgründen nicht ausgeführt: {0}", o.Detail is null ? o.Kind : $"{o.Kind} ({o.Detail})"),
        _ => "",
    };

    /// <summary>
    /// Follows a link of the page at <paramref name="position"/>; (x, y) is the click in page space (for /IsMap, the position relative to the
    /// upper left corner of the link is appended as „?x,y“).
    /// </summary>
    internal void FollowLink(int position, PageLink link, double x, double y)
    {
        var source = position >= 0 && position < Pages.Count ? Pages[position].Item.Source : Document;
        switch (link.Target)
        {
            case InternalLink internalLink:
                if (PositionIn(source, internalLink.Destination.PageIndex) is { } target) ScrollTo(target, internalLink.Destination);
                break;
            case NamedLink named:
                var to = named.Action switch
                {
                    "NextPage" => position + 1,
                    "PrevPage" => position - 1,
                    "FirstPage" => 0,
                    _ => Pages.Count - 1,
                };
                if (to >= 0 && to < Pages.Count) ScrollToPage(to);
                break;
            case AddressLink address:
                var open = address.OpenTarget is { } t && address.IsMap
                    ? t + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"?{(int)(x - link.Rect.Left)},{(int)(link.Rect.Top - y)}")
                    : address.OpenTarget;
                AddressLinkActivated?.Invoke(address, open);
                break;
            case OtherLink other:
                Notify(Describe(other));
                break;
        }
    }

    /// <summary>The position of a page of a source document in the view; null if it is not (any more) shown.</summary>
    private int? PositionIn(PdfDocument source, int pageIndex)
    {
        for (var i = 0; i < Pages.Count; i++)
            if (ReferenceEquals(Pages[i].Item.Source, source) && Pages[i].Page.Index == pageIndex) return i;
        return null;
    }
}
