using System.Xml;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfACheck
{
    /// <summary>The rules on content streams and the resources they use.</summary>
    private sealed partial class Run
    {
        // ------------------------------------------------------------ Content: device colours, fonts, operators, nesting

        private sealed class State
        {
            public int RenderMode;
            public PdfDictionary? Font;

            /// <summary>Overprint for stroking and filling, and the overprint mode (6.2.4.2: they combine across graphics states).</summary>
            public bool Overprint, OverprintFill;
            public int OverprintMode;

            /// <summary>The colour for stroking and filling is ICC-based CMYK (DeviceCMYK counts with an ICC-based default space).</summary>
            public bool StrokeIccCmyk, FillIccCmyk;

            /// <summary>The stream has set a colour space for stroking and filling; until then it paints with the colour it starts with.</summary>
            public bool StrokeSet, FillSet;

            public State Copy() => (State)MemberwiseClone();
        }

        // the resources of the page checked: a form, glyph or pattern without its own draws with them, wherever it is invoked (ISO 32000-2,
        // 7.8.3, sentence 18; docs/FEHLERGEDAECHTNIS.md, No. 791) — as the display reads it
        private PdfDictionary? _pageResources;

        private void Form(PdfStream form, PdfDictionary? inherited, int depth)
        {
            var own = form.Dictionary.GetDictionary(PdfName.Resources);
            var resources = own ?? (inherited is null ? null : _pageResources ?? inherited);
            // every form is written anew (one the display cannot read draws nothing: written empty)
            Rewrites?.Add(form);
            byte[] data;
            try
            {
                data = doc.GetStreamData(form);
            }
            catch (PdfException)
            {
                return;
            }
            if (!_walked.Add((form, resources))) return;
            var outerStreams = _streams;
            _streams = [form];
            // PDF/A-4: a transparency group with a colour space of its own is the blending space inside the form
            var (outerBlend, outerBlendProfile, outerBlendGroup) = (_pageBlend, _blendProfile, _blendGroup);
            var (outerOwner, outerResources) = (_owner, _outer);
            // only an isolated group has a blending space of its own; a non-isolated one inherits that of its nearest isolated ancestor or the
            // page (ISO 32000-2, 11.4.7 and 11.6.6; docs/FEHLERGEDAECHTNIS.md, No. 518)
            var isolated = form.Dictionary.GetDictionary(PdfName.Get("Group"))?.GetBoolean(PdfName.Get("I")) == true;
            if (isolated && BlendProfile(form.Dictionary.GetDictionary(PdfName.Get("Group"))) is { } formProfile)
                (_blendProfile, _blendGroup) = (formProfile, form.Dictionary.GetDictionary(PdfName.Get("Group")));
            // a transparency group is transparency on the page (6.2.10); PDF/A-1 (6.4) has none
            if (form.Dictionary.GetDictionary(PdfName.Get("Group"))?.GetName(PdfName.Get("S"))?.Value == "Transparency") _transparency = true;
            if (_part.Part() == 1) TransparencyGroup(form.Dictionary);
            (_owner, _outer) = own is not null ? (form.Dictionary, inherited) : (null, null);
            if (Four && isolated && form.Dictionary.GetDictionary(PdfName.Get("Group")) is { } formGroup && formGroup.GetName(PdfName.Get("S"))?.Value == "Transparency" &&
                BlendSpace(formGroup.Get(PdfName.Get("CS"))) is { } blend)
                _pageBlend = blend;
            // the group's own colour space, checked within the group (PDF/A-4: a device space there is its own blending space)
            if (form.Dictionary.GetDictionary(PdfName.Get("Group")) is { } group) GroupSpace(group, resources, outerBlendProfile);
            var outerInitial = _initialPaint;
            _initialPaint = InitialPaint.None;
            // default colour spaces count only from the form's own resources: strict readers do not look into inherited ones (6.2.4.3)
            var outerOwnResources = _withoutOwnResources;
            _withoutOwnResources = own is null;
            var (faults, named) = Content(data, resources, depth);
            _withoutOwnResources = outerOwnResources;
            _formInitial[form] = _initialPaint;
            _initialPaint = outerInitial;
            (_pageBlend, _blendProfile, _blendGroup) = (outerBlend, outerBlendProfile, outerBlendGroup);
            (_owner, _outer) = (outerOwner, outerResources);
            _streams = outerStreams;
            Faulty(faults, form);
            if (own is null && named) Borrowing(form.Dictionary, inherited);
        }

        /// <summary>The glyph procedures of a Type 3 font: content streams of their own, drawn with the font's resources or else those of the text.</summary>
        private void Type3(PdfDictionary font, PdfDictionary? resources, int depth)
        {
            if (!_walked.Add((font, resources)) || font.GetDictionary(PdfName.Get("CharProcs")) is not { } procs) return;
            var own = font.GetDictionary(PdfName.Resources);
            var named = false;
            var n = 0;
            var (outerOwner, outerResources) = (_owner, _outer);
            (_owner, _outer) = own is not null ? (font, resources) : (null, null);
            // a glyph paints with the colour of the text (or none, d1): not the colour a stream starts with
            var outerInitial = _initialPaint;
            foreach (var (key, _) in procs.Entries)
            {
                if (++n > 4096) break;
                if (procs.Get(key) is not PdfStream proc) continue;
                // a glyph procedure may carry resources of its own, which then apply
                var procOwn = proc.Dictionary.GetDictionary(PdfName.Resources);
                Rewrites?.Add(proc);
                var outerStreams = _streams;
                _streams = [proc];
                var outerOwnResources = _withoutOwnResources;
                _withoutOwnResources = procOwn is null && own is null;
                var (faults, uses) = Content(StreamData(proc), procOwn ?? own ?? _pageResources ?? resources, depth + 1);
                _withoutOwnResources = outerOwnResources;
                _streams = outerStreams;
                Faulty(faults, proc);
                named |= uses && procOwn is null;
            }
            (_owner, _outer) = (outerOwner, outerResources);
            _initialPaint = outerInitial;
            if (own is null && named) Borrowing(font, resources);
        }

        /// <summary>Which of the colours a stream starts with it paints with.</summary>
        [Flags]
        private enum InitialPaint
        {
            None = 0,
            Fill = 1,
            Stroke = 2,
        }

        /// <summary>
        /// The stream walked paints with the colour it starts with, without setting a colour space first — in a page or an appearance the
        /// initial DeviceGray (ISO 32000-1, 8.4, Table 52), used as any other device space (6.2.4.3); a form starts with the colour of the
        /// content that draws it.
        /// </summary>
        private InitialPaint _initialPaint;

        /// <summary>Per form walked: the colours it is given that it paints with.</summary>
        private readonly Dictionary<PdfStream, InitialPaint> _formInitial = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Walks the content of a page from the initial graphics state: painting with its colour uses DeviceGray.
        /// </summary>
        private (ContentFaults Faults, bool Named) TopContent(byte[] data, PdfDictionary? resources)
        {
            _initialPaint = InitialPaint.None;
            var result = Content(data, resources, 0);
            if (_initialPaint != InitialPaint.None) Device("DeviceGray", resources);
            _initialPaint = InitialPaint.None;
            return result;
        }

        /// <summary>An appearance stream, drawn from the initial graphics state: painting with its colour uses DeviceGray.</summary>
        private void TopForm(PdfStream form)
        {
            Form(form, null, 1);
            if (_formInitial.GetValueOrDefault(form) != InitialPaint.None) Device("DeviceGray", form.Dictionary.GetDictionary(PdfName.Resources));
        }

        /// <summary>Painting with the current stroke or fill colour: the colour the stream started with where it set none.</summary>
        private void Paints(State state, bool fill, bool stroke)
        {
            if (fill && !state.FillSet) _initialPaint |= InitialPaint.Fill;
            if (stroke && !state.StrokeSet) _initialPaint |= InitialPaint.Stroke;
        }

        private void Action(PdfDictionary action)
        {
            if (AllowedAction(action, _part)) return;
            var kind = action.GetName(PdfName.Get("S"))?.Value;
            if (kind == "Named")
                Add(PdfARules.NamedActions, Tr.T("Das Dokument enthält eine unzulässige benannte Aktion ({0}).", action.GetName(PdfName.Get("N"))?.Value ?? "?"));
            else Add(PdfARules.Actions, Tr.T("Das Dokument enthält eine unzulässige Aktion ({0}).", kind ?? "?"));
        }

        /// <summary>
        /// The colour space of a transparency group, checked as the space it is in sees it: the blending space around the group (<paramref
        /// name="outerProfile"/>), not the group's own — its space is not a colour inside it.
        /// </summary>
        private void GroupSpace(PdfDictionary group, PdfDictionary? resources, PdfStream? outerProfile)
        {
            if (group.GetName(PdfName.Get("S"))?.Value != "Transparency" || group.Get(PdfName.Get("CS")) is PdfNull) return;
            var inner = _blendProfile;
            _blendProfile = outerProfile;
            ColorSpace(group.Get(PdfName.Get("CS")), resources, 0);
            _blendProfile = inner;
        }

        /// <summary>Walks a content stream.</summary>
        /// <returns>The faults of its syntax, and whether it names resources (a font, an image, a graphics state …).</returns>
        /// <summary>The marked content open in the content walked: whether each has a replacement text (/ActualText).</summary>
        private Stack<bool> _marks = new();

        /// <summary>How many of the open marked contents have a replacement text: characters in them need no Unicode of their own (6.2.11.7.3).</summary>
        private int _actualText;

        /// <summary>
        /// The marked content of the page being walked whose structure element (or one around it) has a replacement text (/ActualText): its
        /// characters stand in that text (6.2.11.7.3); null where level a is not checked.
        /// </summary>
        private HashSet<int>? _mcidsWithText;

        /// <summary>The marked contents (MCID) of a page whose structure element or one of its parents has /ActualText (through the parent tree).</summary>
        private HashSet<int> McidsWithText(PdfDictionary page)
        {
            var result = new HashSet<int>();
            if (NumberTree.KeyOf(page.Get(PdfName.Get("StructParents"))) is not { } key ||
                doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"))?.GetDictionary(PdfName.Get("ParentTree")) is not { } tree ||
                NumberTree.Value(tree, key) is not PdfArray elements)
                return result;
            for (var mcid = 0; mcid < elements.Count && mcid < 100_000; mcid++)
            {
                var element = elements.Get(mcid) as PdfDictionary;
                for (var depth = 0; element is not null && depth < 64; depth++, element = element.Get(PdfName.Get("P")) as PdfDictionary)
                    if (element.ContainsKey(PdfName.Get("ActualText")))
                    {
                        result.Add(mcid);
                        break;
                    }
            }
            return result;
        }

        /// <summary>Marked content begins (BDC, BMC) with these properties.</summary>
        private void Marked(PdfDictionary? properties)
        {
            var withText = properties?.ContainsKey(PdfName.Get("ActualText")) == true ||
                           properties?.GetInteger(PdfName.Get("MCID")) is { } mcid && _mcidsWithText?.Contains((int)mcid) == true;
            _marks.Push(withText);
            if (withText) _actualText++;
        }

        private (ContentFaults Faults, bool Named) Content(byte[] data, PdfDictionary? resources, int depth)
        {
            // marked content does not reach beyond its stream (a form drawn inside it is inside it as well)
            var outerMarks = _marks;
            _marks = new Stack<bool>();
            try
            {
                return ContentWalk(data, resources, depth);
            }
            finally
            {
                foreach (var withText in _marks)
                    if (withText) _actualText--;
                _marks = outerMarks;
            }
        }

        private (ContentFaults Faults, bool Named) ContentWalk(byte[] data, PdfDictionary? resources, int depth)
        {
            if (depth > 16) return (ContentFaults.None, false);
            var parser = new ContentParser(data);
            var states = new Stack<State>();
            var state = new State();
            var nesting = 0;
            string? op;
            var count = 0;
            var faults = ContentFaults.None;
            var named = false;
            var before = 0;
            while ((op = parser.Next()) is not null)
            {
                if ((++count & 4095) == 0) cancel.ThrowIfCancellationRequested();
                var operands = parser.Operands;
                // (a smoothed inline image is reported by InlineImage; the writer writes such content anew)
                faults |= ContentConformance.Faults(op, operands, data, before, parser.Position, op == "BI" ? parser.InlineImage : null, _part.Limits().Real,
                    _part.Limits().String, _part.Part() == 1 ? 8191 : int.MaxValue);
                before = parser.Position;
                named |= NamesResource(op, operands);
                switch (op)
                {
                    case "q":
                        states.Push(state.Copy());
                        if (++nesting <= MaxNesting) break;
                        if (Deep is not null) Deep.UnionWith(_streams);
                        else Add(PdfARules.SaveNesting, Tr.T("Grafikzustände sind tiefer als 28 Ebenen geschachtelt."));
                        break;
                    case "Q":
                        if (states.TryPop(out var saved)) state = saved;
                        nesting = Math.Max(0, nesting - 1);
                        break;
                    case "rg" or "RG":
                        Device("DeviceRGB", resources);
                        SetIccCmyk(state, op == "rg", false);
                        break;
                    case "k" or "K":
                        Device("DeviceCMYK", resources);
                        SetIccCmyk(state, op == "k", IccCmyk(PdfName.Get("DeviceCMYK"), resources));
                        break;
                    case "g" or "G":
                        Device("DeviceGray", resources);
                        SetIccCmyk(state, op == "g", false);
                        break;
                    case "cs" or "CS" when operands.Count > 0 && operands[^1].Name is { } space:
                        ColorSpace(space, resources, 0);
                        SetIccCmyk(state, op == "cs", IccCmyk(space, resources));
                        break;
                    case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s" or "sh":
                        // overprint mode 1 with ICC-based CMYK (6.2.4.2), for the painting that uses it
                        var fills = op is not ("S" or "s");
                        var strokes = op is "S" or "s" or "B" or "B*" or "b" or "b*";
                        if (op != "sh") Paints(state, fills, strokes);
                        if (state.OverprintMode == 1 && (fills && state.FillIccCmyk && state.OverprintFill || strokes && state.StrokeIccCmyk && state.Overprint))
                            Add(PdfARules.OverprintMode, Tr.T("Überdrucken mit Modus 1 (/OPM) wird zusammen mit ICC-basiertem CMYK benutzt."));
                        if (op == "sh" && operands.Count > 0 && operands[^1].Name is { } shading)
                        {
                            var shadingObject = Resource(resources, "Shading", shading);
                            if (shadingObject is PdfDictionary sd) ColorSpace(sd.Get(PdfName.Get("ColorSpace")), resources, 0);
                            else if (shadingObject is PdfStream ss) ColorSpace(ss.Dictionary.Get(PdfName.Get("ColorSpace")), resources, 0);
                        }
                        break;
                    case "scn" or "SCN" when operands.Count > 0 && operands[^1].Name is { } pattern:
                        if (Resource(resources, "Pattern", pattern) is { } p) Pattern(p, resources, depth);
                        break;
                    case "ri" when operands.Count > 0 && operands[^1].Name is { } intent:
                        if (!RenderingIntent(intent.Value)) Add(PdfARules.RenderingIntent, Tr.T("Eine unbekannte Farbwiedergabe (Rendering Intent) wird benutzt."));
                        break;
                    case "gs" when operands.Count > 0 && operands[^1].Name is { } gsName:
                        if (Resource(resources, "ExtGState", gsName) is PdfDictionary gs)
                        {
                            Dictionary(gs);
                            if (_part.Part() == 1)
                            {
                                OpaqueState(gs);
                                if (gs.Get(PdfName.Get("BM")) is not PdfNull and var blend && !StandardBlend(blend, _part))
                                    Add(PdfARules.BlendModes, Tr.T("Eine Mischart (Blend Mode) wird benutzt, die PDF/A nicht erlaubt."));
                            }
                            if (gs.GetBoolean(PdfName.Get("OP")) is { } op1)
                            {
                                state.Overprint = op1;
                                if (!gs.ContainsKey(PdfName.Get("op"))) state.OverprintFill = op1;
                            }
                            if (gs.GetBoolean(PdfName.Get("op")) is { } op2) state.OverprintFill = op2;
                            if (gs.GetInteger(PdfName.Get("OPM")) is { } mode) state.OverprintMode = (int)mode;
                            if (gs.Get(PdfName.Get("SMask")) is PdfDictionary mask && mask.Get(PdfName.Get("G")) is PdfStream maskForm)
                            {
                                var outerMask = _inMask;
                                _inMask = true;
                                Form(maskForm, resources, depth + 1);
                                _inMask = outerMask;
                            }
                            // transparency on the page (6.2.10): constant alpha below 1, a soft mask, a blend mode other than Normal
                            if (gs.GetNumber(PdfName.Get("CA")) is < 1 || gs.GetNumber(PdfName.Get("ca")) is < 1 || gs.Get(PdfName.Get("SMask")) is PdfDictionary ||
                                gs.Get(PdfName.Get("BM")) is PdfName { Value: not ("Normal" or "Compatible") } or PdfArray)
                                _transparency = true;
                            if (gs.Get(PdfName.Get("Font")) is PdfArray { Count: > 0 } gsFont && gsFont.Get(0) is PdfDictionary f) state.Font = f;
                        }
                        break;
                    case "Tr" when operands.Count > 0 && operands[^1].IsNumber:
                        state.RenderMode = (int)operands[^1].Number;
                        break;
                    // a font without its size: the display does nothing with it (the writer leaves it out), strict readers still look for the name
                    case "Tf" when operands.Count == 1 && operands[0].Name is { } onlyName:
                        if (MissingNames is null) Resource(resources, "Font", onlyName);
                        break;
                    case "Tf" when operands.Count > 1 && operands[0].Name is { } fontName:
                        state.Font = Resource(resources, "Font", fontName) as PdfDictionary;
                        if (FontCodes is null && !Utf8(fontName.Value)) Add(PdfARules.NamesUtf8, Tr.T("Der Name einer Schrift im Seiteninhalt ist kein gültiges UTF-8."));
                        break;
                    // a text operator without its text draws nothing, but strict readers count its font as used for rendering (the writer
                    // leaves such operators out, ContentConformance.WithoutText)
                    case "Tj" or "TJ" or "'" or "\"" when state.Font is { } textless && ContentConformance.WithoutText(op, operands):
                        if (FontCodes is not null) break;
                        if (_fontsChecked.Add(textless)) FontRules(textless);
                        Glyphs(textless, new PdfString([]), state.RenderMode == 3);   // as shown with empty text
                        break;
                    case "Tj" or "TJ" or "'" or "\"" when state.Font is { } font && operands.Count > 0:
                        // the rendering modes that fill (0, 2, 4, 6) and stroke (1, 2, 5, 6) the glyphs
                        Paints(state, state.RenderMode is 0 or 2 or 4 or 6, state.RenderMode is 1 or 2 or 5 or 6);
                        if (state.OverprintMode == 1 && state.FillIccCmyk && state.OverprintFill)
                            Add(PdfARules.OverprintMode, Tr.T("Überdrucken mit Modus 1 (/OPM) wird zusammen mit ICC-basiertem CMYK benutzt."));
                        var kind = font.GetName(PdfName.Subtype)?.Value;
                        if (kind == "Type3") Type3(font, resources, depth);
                        if (FontCodes is not null)
                        {
                            if (kind is not ("Type0" or "Type3")) Codes(font, operands[^1].Object);
                            else if (kind == "Type0" && _fontsChecked.Add(font)) NoteCollection(font);
                            NoteShown(font, operands[^1].Object, state.RenderMode == 3);
                            break;
                        }
                        if (_fontsChecked.Add(font)) FontRules(font);
                        Glyphs(font, operands[^1].Object, state.RenderMode == 3);
                        break;
                    case "Do" when operands.Count > 0 && operands[^1].Name is { } name:
                        if (Resource(resources, "XObject", name) is PdfStream xobject)
                        {
                            DrawnXObject(xobject.Dictionary);
                            // PDF/A-1 (6.4): no soft mask
                            if (_part.Part() == 1 && xobject.Dictionary.ContainsKey(PdfName.Get("SMask")))
                                Add(PdfARules.NoImageSoftMask, Tr.T("Ein Bild oder eine Form hat eine weiche Maske (/SMask)."));
                            // a PostScript XObject: the display draws nothing; the writer leaves it out with its "Do" (6.2.9)
                            if (xobject.Dictionary.GetName(PdfName.Subtype)?.Value == "PS" && MissingNames is not null)
                                foreach (var stream in _streams)
                                    (MissingNames.TryGetValue(stream, out var names) ? names : MissingNames[stream] = []).Add(("XObject", name));
                            if (xobject.Dictionary.GetName(PdfName.Subtype)?.Value == "Form")
                            {
                                Form(xobject, resources, depth + 1);
                                // the form paints with the colours it is given: those this stream has, or else those it started with
                                var given = _formInitial.GetValueOrDefault(xobject);
                                Paints(state, given.HasFlag(InitialPaint.Fill), given.HasFlag(InitialPaint.Stroke));
                            }
                            // an image mask paints with the fill colour
                            else if (xobject.Dictionary.GetBoolean(PdfName.Get("ImageMask")) == true) Paints(state, true, false);
                            else if (xobject.Dictionary.GetName(PdfName.Subtype)?.Value == "Image" && xobject.Dictionary.GetBoolean(PdfName.Get("ImageMask")) != true)
                            {
                                if (xobject.Dictionary.Get(PdfName.Get("ColorSpace")) is null or PdfNull && StreamFilters.Chain(xobject.Dictionary).Any(f => f.Name == "JPXDecode"))
                                {
                                    // a JPEG 2000 image with the colour of its data: an enumerated space counts as the device space (FG 995)
                                    if (PdfAJpeg2000.DeviceSpace(JpxData(xobject)) is { } device) Device(device, resources);
                                }
                                else ColorSpace(xobject.Dictionary.Get(PdfName.Get("ColorSpace")), resources, 0);
                            }
                            // an image with a soft mask is transparency on the page (6.2.10)
                            if (xobject.Dictionary.ContainsKey(PdfName.Get("SMask")) || xobject.Dictionary.GetInteger(PdfName.Get("SMaskInData")) is > 0) _transparency = true;
                        }
                        break;
                    case "BDC" or "DP" when operands.Count > 1 && operands[1].Name is { } property:
                        var propertyList = Resource(resources, "Properties", property) as PdfDictionary;
                        if (op == "BDC") Marked(propertyList);
                        if (_part.Accessible()) Language(propertyList?.Get(PdfName.Get("Lang")));
                        break;
                    case "BDC" or "DP" when operands.Count > 1 && operands[1].Object is PdfDictionary properties:
                        if (properties.Get(PdfName.Get("ActualText")) is PdfString actual && HasPrivateUse(actual.ToText()))
                            Add(PdfARules.ActualTextNoPua, Tr.T("Ein Ersatztext (/ActualText) enthält Zeichen aus dem privaten Bereich von Unicode."));
                        if (op == "BDC") Marked(properties);
                        if (_part.Accessible()) Language(properties.Get(PdfName.Get("Lang")));
                        break;
                    case "BDC" or "BMC":
                        Marked(null);
                        break;
                    case "EMC":
                        if (_marks.TryPop(out var withText) && withText) _actualText--;
                        break;
                    case "BI" when parser.InlineImage is { } inline:
                        if (Either(inline.Dictionary, "IM", "ImageMask") is PdfBoolean { Value: true }) Paints(state, true, false);
                        if (_part.Part() == 4 && !LengthStated(inline))
                            Add(PdfARules.InlineLength, Tr.T("Ein eingebettetes Bild (Inline-Bild) nennt die Länge seiner Daten (/L) nicht oder falsch."));
                        InlineImage(inline.Dictionary, resources);
                        break;
                }
            }
            return (faults, named);
        }

        /// <summary>
        /// PDF/A-4 (6.1.9): /L (or /Length) of an inline image is the length of its data as written — the bytes between the white space
        /// after ID and EI (a white space before EI may count or not).
        /// </summary>
        private static bool LengthStated(InlineImage inline)
        {
            if (Either(inline.Dictionary, "L", "Length")?.AsNumber() is not { } stated) return false;
            var data = inline.Data;
            var trimmed = data.Length;
            while (trimmed > 0 && Parsing.PdfLexer.IsWhitespace(data[trimmed - 1])) trimmed--;
            return stated == data.Length || stated == trimmed;
        }

        /// <summary>A colour space set for filling or stroking: whether it is ICC-based CMYK.</summary>
        private static void SetIccCmyk(State state, bool fill, bool icc)
        {
            if (fill) (state.FillIccCmyk, state.FillSet) = (icc, true);
            else (state.StrokeIccCmyk, state.StrokeSet) = (icc, true);
        }

        /// <summary>Whether a colour space (by name) is ICC-based CMYK — DeviceCMYK when the resources give it an ICC-based default space.</summary>
        private static bool IccCmyk(PdfName space, PdfDictionary? resources)
        {
            var spaces = resources?.GetDictionary(PdfName.Get("ColorSpace"));
            var value = space.Value == "DeviceCMYK" ? spaces?.Get(PdfName.Get("DefaultCMYK")) : spaces?.Get(space);
            return value is PdfArray { Count: > 1 } array && array.Get(0) is PdfName { Value: "ICCBased" } && array.Get(1) is PdfStream profile &&
                   profile.Dictionary.GetInteger(PdfName.Get("N")) == 4;
        }

        /// <summary>
        /// An operator names a resource its content does not have (6.2.2: the resources a content stream uses are defined in the dictionary
        /// associated with it). Where resources are borrowed (none of its own), the writer gives them; this is a name not even there.
        /// </summary>
        /// <summary>
        /// A named resource as the display finds it: in the resources of the content, else in those of the content around it (a form or pattern
        /// whose own resources lack the name: the display reads the page's). Strict readers count such a name as inherited (6.2.2-2): the writer
        /// gives the content the entry (<see cref="Borrowed"/>), the check reports it. Null if the name is nowhere (part 4: 6.2.2-3).
        /// </summary>
        private PdfObject? Resource(PdfDictionary? resources, string category, PdfName name)
        {
            var key = PdfName.Get(category);
            // an entry of the wrong kind (a string where a font should be) is no resource: as if it were missing (docs/FEHLERGEDAECHTNIS.md, No. 340)
            if (resources?.GetDictionary(key)?.Get(name) is { } found and not PdfNull && OfItsKind(category, found)) return found;
            if (MissingNames is not null)
            {
                // the writer draws it as the display does (ContentConformance.Rewrite); text with a missing font in the display's font
                foreach (var stream in _streams)
                    (MissingNames.TryGetValue(stream, out var names) ? names : MissingNames[stream] = []).Add((category, name));
                if (Debug) Console.Error.WriteLine($"PDF/A fehlt: {category} {name.Value} in {string.Join(", ", _streams.Select(st => st.Id?.ToString() ?? "direkt"))}");
                return category == "Font" ? MissingFont : null;
            }
            // found only in the resources around it (the display does not look there): strict readers count it as inherited (6.2.2-2)
            if (_owner is not null && _outer?.GetDictionary(key)?.Get(name) is { } inherited and not PdfNull)
                Add(PdfARules.OwnResources, Tr.T("Ein Inhalt nennt eine Ressource, die nur die Ressourcen um ihn herum haben ({0}).", name.Value));
            Missing(resources, category, name);
            return null;
        }

        /// <summary>Whether a resource is of the kind its category holds (ISO 32000-2, 7.8.3, Table 34).</summary>
        internal static bool OfItsKind(string category, PdfObject resource) => category switch
        {
            "Font" or "ExtGState" or "Properties" => resource is PdfDictionary,
            // an XObject is a stream of type Image, Form or PS (ISO 32000-2, 8.8): a metadata stream named as one draws nothing — the copy wrote
            // the Do, a reference validator found the name undefined (docs/FEHLERGEDAECHTNIS.md, No. 516)
            "XObject" => resource is PdfStream { Dictionary: var x } && x.GetName(PdfName.Subtype)?.Value is "Image" or "Form" or "PS",
            "Pattern" or "Shading" => resource is PdfDictionary or PdfStream,
            "ColorSpace" => resource is PdfName or PdfArray,
            _ => true,
        };

        private void Missing(PdfDictionary? resources, string category, PdfName name)
        {
            // part 4 only (6.2.2-3): with a resource dictionary of its own, a name it lacks does not count as inherited (6.2.2-2) — asked of
            // the reference validator
            var entry = resources?.GetDictionary(PdfName.Get(category))?.Get(name);
            if (entry is null or PdfNull || !OfItsKind(category, entry))
                Add(PdfARules.ResourcesDefined, Tr.T("Ein Inhalt verweist auf eine Ressource, die fehlt ({0}).", name.Value));
        }

        /// <summary>Whether an operator uses an entry of the resources.</summary>
        private static bool NamesResource(string op, IReadOnlyList<Operand> operands) => op switch
        {
            "Do" or "Tf" or "gs" or "sh" => true,
            "BDC" or "DP" => operands.Count >= 2 && operands[1].Name is not null,
            "cs" or "CS" => operands.Count > 0 && operands[^1].Name?.Value is { } space && space is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern"),
            "scn" or "SCN" => operands.Count > 0 && operands[^1].Name is not null,
            _ => false,
        };
    }
}
