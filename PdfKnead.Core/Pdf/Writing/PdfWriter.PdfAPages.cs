using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// An annotation (6.3): printed and not hidden (hidden ones never reach a page, see <see cref="PdfAPage"/>), and as its only appearance
        /// (/AP /N) what the display draws for it: its normal appearance — of a button the set of its states, of anything else the state it
        /// shows —; where it has none, what the display draws from its properties (a shape, a markup, a note's icon, the text of free text; the
        /// value of a field comes with its settled form, see <see cref="SettleAppearances"/>); where the display draws nothing, an empty
        /// appearance. Colours (/C, /IC) go only where the part asks for an RGB output intent (PDF/A-1).
        /// </summary>
        private void PdfAAnnotation(PdfDictionary annot, Func<PdfDictionary> writable, Source? source, PdfDictionary original)
        {
            var state = _pdfA!;
            var subtype = annot.GetName(PdfName.Subtype)!.Value;
            // the field type up the /Parent chain; a widget linked to another field has it noted (its /Parent points into the new file)
            var button = subtype == "Widget" && (FieldType(annot) ?? _relinkedFieldTypes.GetValueOrDefault(original)) == "Btn";
            // PDF/A-1 flattened: what the annotation drew translucent is in the images over the page — it stays, with an empty appearance
            if (_options.PdfABlankAnnotations?.Contains(original) == true && AnnotationAppearance.RectOf(annot) is { } blankArea)
            {
                if (annot.ContainsKey(PdfName.Get("CA"))) writable().Remove(PdfName.Get("CA"));
                EmptyAppearance(annot, writable, blankArea, button);
                if (annot.GetInteger(F) is not { } blankFlags || ((blankFlags | 4) & ~(1 | 2 | 32 | 256)) != blankFlags)
                    writable().Set(F, PdfInteger.Of(((annot.GetInteger(F) ?? 0) | 4) & ~(1 | 2 | 32 | 256)));
                NoColours(annot, writable);
                return;
            }
            var flags = annot.GetInteger(F) is { } given ? (int)given : (int?)null;
            // PDF/A-1 (6.5.3): no /CA below 1 — the display, like widely used readers, draws an appearance of its own without it; one drawn
            // from the properties below has the opacity inside (read from this dictionary as it was), which the flattening takes care of
            if (state.Part.Part() == 1 && annot.GetNumber(PdfName.Get("CA")) is { } annotOpacity && annotOpacity != 1)
                writable().Remove(PdfName.Get("CA"));
            // (PDF/A-1, 6.5.3, asks pop-ups for their flags as well)
            if (flags is { } f0 ? ((f0 | 4) & ~(1 | 2 | 32 | 256)) != f0 : subtype != "Popup" || state.Part.Part() == 1)
                writable().Set(F, PdfInteger.Of(((flags ?? 0) | 4) & ~(1 | 2 | 32 | 256)));
            var apRaw = annot.GetRaw(Ap);
            // an appearance the writer made itself (a settled field) is in the new file already
            var drawn = apRaw is OutputReference && Look(apRaw, source).Obj is null;
            // an appearance dictionary without a normal appearance: the display draws none from it
            // (also where /N refers to nothing: a broken reference, docs/FEHLERGEDAECHTNIS.md, No. 340)
            if (apRaw is not null && !drawn && !(Look(apRaw, source) is (PdfDictionary withNormal, var normalFrom) && withNormal.GetRaw(N) is { } normalRaw &&
                                                (normalRaw is OutputReference || PdfAResolve(normalRaw, normalFrom) is PdfStream or PdfDictionary))) writable().Remove(Ap);
            if (apRaw is not null && Look(apRaw, source) is (PdfDictionary ap, var apFrom) && ap.GetRaw(N) is { } n &&
                (Look(n, apFrom).Obj is PdfStream or PdfDictionary || n is OutputReference))
            {
                if (ap.ContainsKey(D) || ap.ContainsKey(PdfName.Get("R")))
                {
                    var normal = new PdfDictionary();
                    normal.Set(N, Portable(n, apFrom, source));
                    writable().Set(Ap, normal);
                }
                switch (Look(n, apFrom))
                {
                    case (PdfStream, _) when NumberOf(n, apFrom) is > 0 and var appearance:
                        state.Appearances.Add(appearance);
                        drawn = true;
                        // a button's appearance is a set of states (6.3.3): its one appearance becomes the state it shows
                        if (button)
                        {
                            var shown = annot.GetName(PdfName.Get("AS"))?.Value ?? (annot.Get(PdfName.Get("V")) as PdfName)?.Value ?? "On";
                            var states = new PdfDictionary();
                            states.Set(PdfName.Get(shown), new OutputReference(appearance));
                            var normal = new PdfDictionary();
                            normal.Set(N, states);
                            writable().Set(Ap, normal);
                            writable().Set(PdfName.Get("AS"), PdfName.Get(shown));
                        }
                        break;
                    case (PdfStream, _):
                    // made by the writer itself (a settled field): in the new file already
                    case (null, _) when n is OutputReference:
                        drawn = true;
                        break;
                    case (PdfDictionary states, var statesFrom) when button:
                        foreach (var (_, look) in states.Entries)
                            // a state made by the writer itself (a settled push button as a state, No. 919): in the new file already
                            if (look is OutputReference) drawn = true;
                            else if (NumberOf(look, statesFrom) is > 0 and var number && Look(look, statesFrom).Obj is PdfStream)
                            {
                                state.Appearances.Add(number);
                                drawn = true;
                            }
                        break;
                    case (PdfDictionary states, var statesFrom):
                        // not a button: the state it shows (/AS) as its one appearance (6.3.3), as the display draws it
                        if (annot.GetName(PdfName.Get("AS")) is { } current && states.GetRaw(current) is { } chosen && Look(chosen, statesFrom).Obj is PdfStream)
                        {
                            if (NumberOf(chosen, statesFrom) is > 0 and var chosenNumber) state.Appearances.Add(chosenNumber);
                            var normal = new PdfDictionary();
                            normal.Set(N, Portable(chosen, statesFrom, source));
                            writable().Set(Ap, normal);
                            drawn = true;
                        }
                        break;
                }
            }
            if (!drawn) MissingAppearance(annot, writable, original, subtype, button);
            NoColours(annot, writable);
        }

        /// <summary>
        /// An annotation of some area without a normal appearance gets the one the display draws — PDF/A asks for it (ISO 19005-2, 6.3.3), and so
        /// does PDF 2.0 (ISO 32000-2, 12.5.5; docs/FEHLERGEDAECHTNIS.md, No. 373): drawn from its properties (markup, shapes, lines), the text the
        /// display lays out (free text, fields: <see cref="_fieldReplacements"/>), else an empty one of its area. Pop-ups, links and projections
        /// need none.
        /// </summary>
        private void MissingAppearance(PdfDictionary annot, Func<PdfDictionary> writable, PdfDictionary original, string subtype, bool button)
        {
            if (subtype is "Popup" or "Link" or "Projection") return;
            if (AnnotationAppearance.RectOf(annot) is not { } rect)
            {
                // a rectangle with one extent 0 (a line of an area, 6.3.3), or a widget of PDF/A-1 without an area (6.9): an empty
                // appearance of that rectangle (docs/FEHLERGEDAECHTNIS.md, No. 499)
                if (annot.Get(Rect) is PdfArray { Count: >= 4 } raw && raw.GetNumber(0) is { } x1 && raw.GetNumber(1) is { } y1 && raw.GetNumber(2) is { } x2 &&
                    raw.GetNumber(3) is { } y2 && double.IsFinite(x1 + y1 + x2 + y2) &&
                    (x1 != x2 || y1 != y2 || _pdfA?.Part.Part() == 1 && subtype == "Widget"))
                    EmptyAppearance(annot, writable, new Document.PdfRectangle(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)), button);
                return;
            }
            if (subtype is not ("Widget" or "FreeText") && AnnotationAppearance.Build(annot, rect) is { } built)
            {
                var look = Fresh(built);
                _pdfA?.Appearances.Add(((OutputReference)look).Number);
                var appearance = new PdfDictionary();
                appearance.Set(N, look);
                writable().Set(Ap, appearance);
                // the drawn appearance lies in page space and can reach beyond /Rect (line endings, the icon of a note): a stored one is fitted
                // into /Rect, so /Rect becomes its box
                if (built.Dictionary.Get(PdfName.Get("BBox")) is PdfArray { Count: 4 } box) writable().Set(Rect, box);
            }
            else if (_fieldReplacements.TryGetValue(original, out var text))
            {
                var look = WrittenForm(text.Appearance);
                _pdfA?.Appearances.Add(((OutputReference)look).Number);
                var appearance = new PdfDictionary();
                appearance.Set(N, look);
                writable().Set(Ap, appearance);
            }
            // nothing drawn: an empty appearance of the area (asked for where /Rect is more than a point)
            else if (rect.Width != 0 || rect.Height != 0) EmptyAppearance(annot, writable, rect, button);
        }

        /// <summary>
        /// PDF 2.0 without PDF/A (which does this in <see cref="PdfAAnnotation"/>): an annotation of the sources without a normal appearance gets
        /// one (<see cref="MissingAppearance"/>); null if it has one or is no annotation.
        /// </summary>
        private PdfDictionary? WithAppearance(PdfDictionary annot, Source? source)
        {
            if (annot.GetName(PdfName.Subtype)?.Value is not { } subtype || !(annot.ContainsKey(Rect) || annot.GetName(PdfName.Type)?.Value == "Annot") ||
                annot.GetName(PdfName.Type)?.Value is not (null or "Annot"))
                return null;
            var apRaw = annot.GetRaw(Ap);
            if (apRaw is OutputReference) return null;   // made by the writer (a settled field)
            if (apRaw is not null && Look(apRaw, source) is (PdfDictionary ap, var apFrom) && ap.GetRaw(N) is { } n &&
                (n is OutputReference || Look(n, apFrom).Obj is PdfStream || Look(n, apFrom).Obj is PdfDictionary { Count: > 0 }))
                return null;
            PdfDictionary? copy = null;
            PdfDictionary Writable() => copy ??= CloneWithResolver(annot);
            var others = apRaw is not null && Look(apRaw, source).Obj is PdfDictionary oldAppearances ? oldAppearances : null;
            if (apRaw is not null) Writable().Remove(Ap);
            var button = subtype == "Widget" && (FieldType(annot) ?? _relinkedFieldTypes.GetValueOrDefault(annot)) == "Btn";
            MissingAppearance(annot, Writable, annot, subtype, button);
            // the appearances it had beside the missing normal one stay — down and rollover (/D, /R; ISO 32000-2, 12.5.5, Table 170); they went
            // with the dictionary (docs/FEHLERGEDAECHTNIS.md, No. 882). PDF/A allows /N alone (ISO 19005-2, 6.3.3) and goes the other way.
            if (others is not null && copy?.Get(Ap) is PdfDictionary built)
                foreach (var key in (ReadOnlySpan<PdfName>)[PdfName.Get("D"), PdfName.Get("R")])
                    if (others.GetRaw(key) is { } kept && !built.ContainsKey(key)) built.Set(key, kept);
            return copy;
        }

        /// <summary>PDF/A-1 (6.5.3): no colours (/C, /IC) unless the output intent is RGB — the appearance draws what the colours meant.</summary>
        private void NoColours(PdfDictionary annot, Func<PdfDictionary> writable)
        {
            if (PdfARules.AnnotationColours.Id(_pdfA!.Part) is null || _pdfA.ProfileSpace == IccProfile.RgbSpace) return;
            if (annot.ContainsKey(C)) writable().Remove(C);
            if (annot.ContainsKey(PdfName.Get("IC"))) writable().Remove(PdfName.Get("IC"));
        }

        /// <summary>
        /// An annotation the display draws nothing for (an empty field, a button without appearances, a shape without colours): an empty
        /// appearance of its area shows the same — for a button as its current state.
        /// </summary>
        private void EmptyAppearance(PdfDictionary annot, Func<PdfDictionary> writable, PdfRectangle area, bool button)
        {
            var form = new PdfDictionary();
            form.Set(PdfName.Type, XObject);
            form.Set(PdfName.Subtype, PdfName.Get("Form"));
            form.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(Math.Abs(area.Width)), new PdfReal(Math.Abs(area.Height))]));
            var look = Fresh(new PdfStream(form, []));
            _pdfA?.Appearances.Add(((OutputReference)look).Number);
            var appearance = new PdfDictionary();
            if (button)
            {
                var shown = annot.GetName(PdfName.Get("AS"))?.Value ?? "Off";
                var states = new PdfDictionary();
                states.Set(PdfName.Get(shown), look);
                appearance.Set(N, states);
                writable().Set(PdfName.Get("AS"), PdfName.Get(shown));
            }
            else
            {
                appearance.Set(N, look);
            }
            writable().Set(Ap, appearance);
        }

        /// <summary>
        /// The page boxes for PDF/A (6.1.13): media and crop box as the display uses them (numbers, not references to them — strict readers take
        /// only numbers there), the other boxes as numbers, or left out where they are not valid (they do not change the display).
        /// </summary>
        private void PdfABoxes(PdfDictionary page, PdfPage shown, Source source)
        {
            static PdfArray Numbers(PdfRectangle r) => new([new PdfReal(r.Left), new PdfReal(r.Bottom), new PdfReal(r.Right), new PdfReal(r.Top)]);
            static bool Valid(PdfRectangle r) => Math.Abs(r.Width) is >= 3 and <= 14400 && Math.Abs(r.Height) is >= 3 and <= 14400;
            if (Valid(shown.MediaBox)) page.Set(PdfName.MediaBox, Numbers(shown.MediaBox));
            if (page.ContainsKey(PdfName.CropBox) && Valid(shown.CropBox)) page.Set(PdfName.CropBox, Numbers(shown.CropBox));
            foreach (var key in (string[])["BleedBox", "TrimBox", "ArtBox"])
            {
                if (page.GetRaw(PdfName.Get(key)) is not { } raw) continue;
                // the numbers, also where they are references
                var numbers = PdfAResolve(raw, source) is PdfArray { Count: 4 } array
                    ? Enumerable.Range(0, 4).Select(i => PdfAResolve(array[i], source)?.AsNumber()).ToArray()
                    : [];
                if (numbers.Length == 4 && numbers.All(n => n is { } v && double.IsFinite(v)) &&
                    PdfRectangle.From(new PdfArray(numbers.Select(n => (PdfObject)new PdfReal(n!.Value)))) is { } box && Valid(box))
                    page.Set(PdfName.Get(key), Numbers(box));
                else page.Remove(PdfName.Get(key));
            }
        }

        /// <summary>The field type of a widget, inherited from its parents.</summary>
        private static string? FieldType(PdfDictionary widget)
        {
            PdfDictionary? field = widget;
            for (var depth = 0; depth < 32 && field is not null; depth++)
            {
                if (field.GetName(FT)?.Value is { } type) return type;
                field = field.Get(PdfName.Parent) as PdfDictionary;
            }
            return null;
        }

        /// <summary>
        /// A page: annotations PDF/A forbids (6.3.1; what the display draws of them is in the content, see <see cref="BannedAnnotations"/>) or that
        /// the display does not show (hidden, not to be viewed; 6.3.2) are taken off it; resources with default colour spaces.
        /// </summary>
        private void PdfAPage(PdfDictionary page, Func<PdfDictionary> writable, Source? source)
        {
            // part 4 (PDF 2.0): a page's output intents as the document's must be — none with /DestOutputProfileRef, the PDF/A one with a
            // usable profile, one PDF/A intent, all on one profile object; those that are not are left out (docs/FEHLERGEDAECHTNIS.md, No. 520)
            if (_pdfA!.Part.Part() == 4 && page.GetRaw(PdfName.Get("OutputIntents")) is { } intentsRaw && Look(intentsRaw, source) is (PdfArray intents, var intentsFrom))
            {
                var kept = new PdfArray();
                PdfReference? first = null;
                var pdfa = false;
                for (var i = 0; i < intents.Count && i < 64; i++)
                {
                    if (PdfAResolve(intents[i], intentsFrom) is not PdfDictionary intent || intent.ContainsKey(PdfName.Get("DestOutputProfileRef"))) continue;
                    var isPdfa = intent.GetName(PdfName.Get("S"))?.Value == "GTS_PDFA1";
                    if (isPdfa && pdfa) continue;
                    if (intent.GetRaw(PdfName.Get("DestOutputProfile")) is { } profileRaw)
                    {
                        if (profileRaw is not PdfReference reference || first is not null && reference.Id.Number != first.Id.Number) continue;
                        if (isPdfa)
                        {
                            byte[] data;
                            try
                            {
                                data = PdfAResolve(profileRaw, intentsFrom) is PdfStream profile ? (intentsFrom ?? source)?.Doc.DecodeStream(profile).Data ?? [] : [];
                            }
                            catch (PdfException)
                            {
                                data = [];
                            }
                            if (PdfACheck.UsableProfile(data, _pdfA.Part.Limits().IccMajor) is null) continue;
                        }
                        first ??= reference;
                    }
                    pdfa |= isPdfa;
                    kept.Add(Portable(intents[i], intentsFrom, source));
                }
                if (kept.Count == 0) writable().Remove(PdfName.Get("OutputIntents"));
                else if (kept.Count != intents.Count) writable().Set(PdfName.Get("OutputIntents"), kept);
            }
            if (page.GetRaw(Annots) is { } annotsRaw && Look(annotsRaw, source) is (PdfArray annots, var from))
            {
                var kept = new PdfArray();
                for (var i = 0; i < annots.Count && i < 100_000; i++)
                {
                    if (PdfAResolve(annots[i], from) is PdfDictionary a && (!PdfACheck.AllowedAnnotation(a, _pdfA!.Part) ||
                                                                           (a.GetInteger(F) is { } flags && (flags & (2 | 32)) != 0)))
                        continue;
                    kept.Add(Portable(annots[i], from, source));
                }
                if (kept.Count != annots.Count || annotsRaw is not PdfArray) writable().Set(Annots, kept);
            }
            WithDefaultSpaces(page, writable, source);
        }
    }
}
