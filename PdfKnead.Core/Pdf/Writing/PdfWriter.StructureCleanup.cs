using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        private static readonly PdfName[] DescriptionKeys = [PdfName.Get("ActualText"), PdfName.Get("Alt"), PdfName.Get("E"), PdfName.Get("MCID")];

        /// <summary>
        /// „Bereinigen: Struktur“ in the content (docs/ABLAUFPLAN.md, 6.6 c; docs/FEHLERGEDAECHTNIS.md, No. 650): the property lists of marked
        /// content — written into the content (BDC, DP) or named in /Properties of the resources, of the pages and of the forms they draw — lose
        /// /ActualText, /Alt, /E and /MCID. Before only the tree went: a replacement text in the content (<c>/Span &lt;&lt;/ActualText (…)&gt;&gt;
        /// BDC</c>) stayed — and with it a name or what else it held (privacy). Each form once (a copy where it changes); only the operators
        /// concerned change (<see cref="MarkedContentIds.WithPropertyLists"/>). Pages without such lists stay as they are.
        /// </summary>
        private IReadOnlyList<PageItem> WithoutMarkedContentDescriptions(IReadOnlyList<PageItem> pages)
        {
            if (!_options.Cleanup.HasFlag(CleanupItems.Structure)) return pages;
            var forms = new Dictionary<PdfStream, PdfStream>(ReferenceEqualityComparer.Instance);
            List<PageItem>? result = null;
            for (var i = 0; i < pages.Count; i++)
            {
                if ((i & 15) == 0) _cancel.ThrowIfCancellationRequested();
                var item = pages[i];
                var resources = item.Page.Resources;
                var stripped = StrippedResources(item.Source, resources, forms, 0);
                byte[] content;
                try
                {
                    using var data = PageInterpreter.PageContent(item.Source, item.Page.Dictionary);
                    content = data.Span.ToArray();
                }
                catch (PdfException)
                {
                    result?.Add(item);
                    continue;
                }
                var written = MarkedContentIds.WithPropertyLists(content, WithoutDescriptions);
                if (ReferenceEquals(stripped, resources) && ReferenceEquals(written, content))
                {
                    result?.Add(item);
                    continue;
                }
                var dict = Clone(item.Page.Dictionary);
                if (!ReferenceEquals(written, content)) dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), written));
                var newResources = stripped ?? new PdfDictionary();
                dict.Set(PdfName.Resources, newResources);
                result ??= [.. pages.Take(i)];
                result.Add(item.WithContent(dict, newResources));
            }
            return result ?? pages;
        }

        /// <summary>A property list without its descriptions and its MCID; null if it has none of them.</summary>
        private static PdfDictionary? WithoutDescriptions(PdfDictionary list)
        {
            if (!DescriptionKeys.Any(list.ContainsKey)) return null;
            var copy = Clone(list);
            foreach (var key in DescriptionKeys) copy.Remove(key);
            return copy;
        }

        /// <summary>Resources with their named property lists and the forms they hold without descriptions; the resources themselves if nothing changes.</summary>
        private static PdfDictionary? StrippedResources(Document.PdfDocument doc, PdfDictionary? resources, Dictionary<PdfStream, PdfStream> forms, int depth)
        {
            if (resources is null || depth > 8) return resources;
            PdfDictionary? copy = null;
            PdfDictionary Copy() => copy ??= Clone(resources);
            if (resources.GetDictionary(PdfName.Get("Properties")) is { } properties)
            {
                PdfDictionary? lists = null;
                foreach (var (name, _) in properties.Entries.Take(10_000))
                    if (properties.Get(name) is PdfDictionary list && WithoutDescriptions(list) is { } plain) (lists ??= Clone(properties)).Set(name, plain);
                if (lists is not null) Copy().Set(PdfName.Get("Properties"), lists);
            }
            if (resources.GetDictionary(PdfName.Get("XObject")) is { } xobjects)
            {
                PdfDictionary? changed = null;
                foreach (var (name, _) in xobjects.Entries.Take(10_000))
                    if (xobjects.Get(name) is PdfStream form && form.Dictionary.GetName(PdfName.Subtype)?.Value == "Form" &&
                        StrippedForm(doc, form, forms, depth + 1) is var plain && !ReferenceEquals(plain, form))
                        (changed ??= Clone(xobjects)).Set(name, plain);
                if (changed is not null) Copy().Set(PdfName.Get("XObject"), changed);
            }
            return copy ?? resources;
        }

        /// <summary>A form without the descriptions of its marked content (a copy, once per form); the form itself if it has none.</summary>
        private static PdfStream StrippedForm(Document.PdfDocument doc, PdfStream form, Dictionary<PdfStream, PdfStream> forms, int depth)
        {
            if (forms.TryGetValue(form, out var known)) return known;
            forms[form] = form;   // a form drawing itself ends here
            byte[] data;
            try
            {
                data = form.RawData is { } raw && !form.Dictionary.ContainsKey(PdfName.Filter) ? raw : doc.DecodeStream(form).Data;
            }
            catch (PdfException)
            {
                return form;
            }
            var resources = form.Dictionary.GetDictionary(PdfName.Resources);
            var stripped = StrippedResources(doc, resources, forms, depth);
            var written = MarkedContentIds.WithPropertyLists(data, WithoutDescriptions);
            if (ReferenceEquals(stripped, resources) && ReferenceEquals(written, data)) return form;
            var dict = new PdfDictionary { Resolver = form.Dictionary.Resolver };
            foreach (var (key, value) in form.Dictionary.Entries)
                if (key.Value is not ("Length" or "Filter" or "DecodeParms")) dict.Set(key, value);
            if (stripped is not null) dict.Set(PdfName.Resources, stripped);
            return forms[form] = new PdfStream(dict, written);
        }
    }
}
