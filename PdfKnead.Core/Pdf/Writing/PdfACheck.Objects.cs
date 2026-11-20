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
    /// <summary>The rules on every object reachable from the trailer: limits, streams, dictionaries wherever they are.</summary>
    private sealed partial class Run
    {
        // ------------------------------------------------------------ All objects reachable from the trailer

        private void Objects()
        {
            var queue = new Queue<int>();
            var seen = new HashSet<int>();
            void Refs(PdfObject obj, int depth)
            {
                if (depth > 64) return;
                switch (obj)
                {
                    case PdfReference r:
                        if (seen.Count < MaxObjects && seen.Add(r.Id.Number)) queue.Enqueue(r.Id.Number);
                        break;
                    case PdfArray a:
                        for (var i = 0; i < a.Count; i++) Refs(a[i], depth + 1);
                        break;
                    case PdfStream s:
                        Refs(s.Dictionary, depth + 1);
                        break;
                    case PdfDictionary d:
                        foreach (var (_, v) in d.Entries) Refs(v, depth + 1);
                        break;
                }
            }
            foreach (var (key, value) in doc.Trailer.Entries)
                if (key.Value is "Root" or "Info") Refs(value, 0);
            var n = 0;
            while (queue.TryDequeue(out var number))
            {
                if ((++n & 1023) == 0) cancel.ThrowIfCancellationRequested();
                var obj = doc.GetObject(number);
                Refs(obj, 0);
                Values(obj, 0);
                switch (obj)
                {
                    case PdfStream stream:
                        Stream(stream);
                        break;
                    case PdfDictionary dict:
                        Dictionary(dict);
                        break;
                }
            }
            if (seen.Count > 8_388_607) Add(PdfARules.ObjectCount, Tr.T("Die Datei hat mehr als 8 388 607 Objekte."));
            // objects nothing refers to are in the file all the same: the rules on syntax, values and filters hold for every object in it
            // (strict validators read each one, docs/FEHLERGEDAECHTNIS.md, No. 340)
            var size = (int)Math.Clamp(doc.Trailer.GetInteger(PdfName.Size) ?? 0, 0, MaxObjects);
            for (var number = 1; number < size; number++)
            {
                if (seen.Contains(number) || !doc.HasObject(number)) continue;
                if ((number & 1023) == 0) cancel.ThrowIfCancellationRequested();
                PdfObject obj;
                try
                {
                    obj = doc.GetObject(number);
                }
                catch (PdfException)
                {
                    continue;
                }
                Values(obj, 0);
                if (obj is PdfStream unreferenced) StreamSyntax(unreferenced);
            }
        }

        /// <summary>What every stream must meet, used or not (6.1.7): its data in the file, packed only with filters the part allows.</summary>
        private void StreamSyntax(PdfStream stream)
        {
            var dict = stream.Dictionary;
            if (dict.ContainsKey(PdfName.Get("F")) || dict.ContainsKey(PdfName.Get("FFilter")) || dict.ContainsKey(PdfName.Get("FDecodeParms")))
                Add(PdfARules.NoExternalStream, Tr.T("Ein Datenstrom verweist auf eine Datei außerhalb des Dokuments."));
            foreach (var filter in FilterNames(dict))
            {
                if (filter is "LZWDecode" or "LZW") Add(PdfARules.StandardFilters, Tr.T("Ein Datenstrom ist mit LZW gepackt."));
                else if (!IsStandardFilter(filter, _part)) Add(PdfARules.StandardFilters, Tr.T("Ein Datenstrom benutzt ein unbekanntes oder unzulässiges Packverfahren ({0}).", filter));
            }
        }

        /// <summary>
        /// The limits of 6.1.13 (PDF/A-1: 6.1.12, the limits of PDF 1.4) for the values directly in an object, and the dictionaries in it.
        /// </summary>
        private void Values(PdfObject obj, int depth)
        {
            // PDF/A-4 has no such limits; the dictionaries inside are still checked
            if (depth > 64) return;
            var limits = _part.Limits();
            switch (obj)
            {
                case PdfString s when s.Bytes.Length > limits.String:
                    Add(PdfARules.StringLength, Tr.T("Eine Zeichenkette ist länger als {0} Byte.", limits.String.ToString("N0", Tr.Culture)));
                    break;
                case PdfName name when System.Text.Encoding.Latin1.GetByteCount(name.Value) > 127:
                    Add(PdfARules.NameLength, Tr.T("Ein Name ist länger als 127 Byte."));
                    break;
                case PdfInteger i when i.Value is > int.MaxValue or < int.MinValue:
                    Add(PdfARules.IntegerRange, Tr.T("Eine Zahl liegt außerhalb der Grenzen."));
                    break;
                case PdfReal r when Math.Abs(r.Value) > limits.Real || !double.IsFinite(r.Value):
                    Add(PdfARules.RealRange, Tr.T("Eine Kommazahl liegt außerhalb der Grenzen."));
                    break;
                case PdfReal r when r.Value != 0 && Math.Abs(r.Value) < 1.175e-38:
                    Add(PdfARules.RealNearZero, Tr.T("Eine Kommazahl liegt näher an null als erlaubt."));
                    break;
                case PdfArray a:
                    if (a.Count > 8191) Add(PdfARules.ArraySize, Tr.T("Eine Liste hat mehr als 8 191 Einträge."));
                    for (var k = 0; k < a.Count; k++) Values(a[k], depth + 1);
                    break;
                case PdfStream s:
                    Values(s.Dictionary, depth + 1);
                    break;
                case PdfDictionary d:
                    if (d.Count > 4095) Add(PdfARules.DictionarySize, Tr.T("Ein Verzeichnis (Dictionary) hat mehr als 4 095 Einträge."));
                    foreach (var (key, v) in d.Entries)
                    {
                        Values(key, depth + 1);
                        Values(v, depth + 1);
                        // every dictionary inside the object (the object itself is checked by the caller)
                        if (v is PdfDictionary inner) Dictionary(inner);
                    }
                    break;
            }
        }

        private void Stream(PdfStream stream)
        {
            var dict = stream.Dictionary;
            StreamSyntax(stream);
            Dictionary(dict);
            if (dict.GetName(PdfName.Type)?.Value == "Metadata") ObjectMetadata(stream);
            switch (dict.GetName(PdfName.Subtype)?.Value)
            {
                case "Image":
                    if (StreamFilters.Chain(dict).Any(f => f.Name == "JPXDecode"))
                        foreach (var (rule, text) in PdfAJpeg2000.Faults(JpxData(stream), dict.ContainsKey(PdfName.Get("ColorSpace")))) Add(rule, text);
                    if (dict.GetBoolean(PdfName.Get("Interpolate")) == true) Add(PdfARules.ImageInterpolate, Tr.T("Ein Bild soll beim Vergrößern geglättet werden (/Interpolate)."));
                    if (dict.ContainsKey(PdfName.Get("Alternates"))) Add(PdfARules.ImageAlternates, Tr.T("Ein Bild hat Ersatzbilder (/Alternates)."));
                    if (dict.ContainsKey(PdfName.Get("OPI"))) Add(PdfARules.ImageOpi, Tr.T("Ein Bild verweist auf ein Original (OPI)."));
                    if (dict.Get(PdfName.Get("Intent")) is PdfName imageIntent && !RenderingIntent(imageIntent.Value))
                        Add(PdfARules.RenderingIntent, Tr.T("Eine unbekannte Farbwiedergabe (Rendering Intent) wird benutzt."));
                    // the stream of /Mask is an image mask whether or not it says so (ISO 32000-1, 8.9.6.3)
                    if (dict.Get(PdfName.Get("Mask")) is PdfStream mask && mask.Dictionary.GetBoolean(PdfName.Get("ImageMask")) != true &&
                        mask.Dictionary.GetInteger(PdfName.Get("BitsPerComponent")) is { } maskBits and not 1)
                        Add(PdfARules.MaskBits, Tr.T("Eine Bildmaske hat eine andere Farbtiefe als 1 Bit (/BitsPerComponent {0}).", maskBits));
                    if (dict.GetInteger(PdfName.Get("BitsPerComponent")) is { } bits)
                    {
                        if (dict.GetBoolean(PdfName.Get("ImageMask")) == true)
                        {
                            if (bits != 1) Add(PdfARules.MaskBits, Tr.T("Eine Bildmaske hat eine andere Farbtiefe als 1 Bit (/BitsPerComponent {0}).", bits));
                        }
                        // PDF 1.4 knows no 16 bits (ISO 19005-1, 6.2.4)
                        else if (bits is not (1 or 2 or 4 or 8 or 16) || bits == 16 && _part.Part() == 1)
                            Add(PdfARules.ImageBits, Tr.T("Ein Bild hat eine unzulässige Farbtiefe (/BitsPerComponent {0}).", bits));
                    }
                    break;
            }
        }

        /// <summary>
        /// The rules on a form or PostScript XObject the content draws (6.2.9; ISO 19005-1, 6.2.5–6.2.7): one only named in resources is not
        /// drawn and not judged.
        /// </summary>
        private void DrawnXObject(PdfDictionary dict)
        {
            switch (dict.GetName(PdfName.Subtype)?.Value)
            {
                case "Form":
                    // PDF/A-1 (6.2.4) names the original of a form with that of an image
                    if (dict.ContainsKey(PdfName.Get("OPI")))
                        Add(_part.Part() == 1 ? PdfARules.ImageOpi : PdfARules.FormOpiPostScript, Tr.T("Eine Form verweist auf ein Original (OPI)."));
                    if (dict.ContainsKey(PdfName.Get("Ref"))) Add(PdfARules.NoReferenceXObject, Tr.T("Eine Form zeigt eine Seite einer anderen Datei (Referenz-XObject)."));
                    if (dict.GetName(PdfName.Get("Subtype2"))?.Value == "PS" || dict.ContainsKey(PdfName.Get("PS")))
                        Add(PdfARules.FormOpiPostScript, Tr.T("Das Dokument enthält PostScript."));
                    break;
                case "PS":
                    Add(PdfARules.NoPostScript, Tr.T("Das Dokument enthält PostScript."));
                    break;
            }
        }

        /// <summary>PDF/A-1 (6.4): a page or form is no transparency group.</summary>
        private void TransparencyGroup(PdfDictionary dict)
        {
            if (dict.GetDictionary(PdfName.Get("Group"))?.GetName(PdfName.Get("S"))?.Value == "Transparency")
                Add(PdfARules.NoTransparencyGroup, Tr.T("Eine Seite oder Form ist eine Transparenzgruppe."));
        }

        /// <summary>
        /// PDF/A-1 (6.4): a graphics state the content uses has no soft mask and draws opaque (one only named in resources draws nothing and is
        /// not judged, as forms are not).
        /// </summary>
        private void OpaqueState(PdfDictionary gs)
        {
            if (gs.Get(PdfName.Get("SMask")) is not (PdfNull or PdfName { Value: "None" }))
                Add(PdfARules.NoSoftMask, Tr.T("Ein Grafikzustand hat eine weiche Maske (/SMask)."));
            foreach (var alpha in (string[])["CA", "ca"])
                if (gs.GetNumber(PdfName.Get(alpha)) is { } value && Math.Abs(value - 1) >= 0.000001)
                    Add(PdfARules.ConstantAlpha, Tr.T("Ein Grafikzustand zeichnet durchscheinend (/{0} nicht 1).", alpha));
        }

        /// <summary>Rules on dictionaries wherever they are: graphics states, actions, file specifications.</summary>
        private void Dictionary(PdfDictionary dict)
        {
            if (_part.Part() == 1 && dict.GetName(PdfName.Type)?.Value == "Page") TransparencyGroup(dict);
            // a transfer function is a function, an array of them or the name Identity (ISO 32000-1, table 58); a /TR naming anything else is
            // no graphics state — the role map of the structure tree maps the table row „TR“ so (Fehlergedächtnis 862)
            if (dict.Get(PdfName.Get("TR")) is { } transfer and not PdfNull && transfer is not PdfName { Value: not "Identity" } && !IsSoftMask(dict))
                Add(PdfARules.NoTransfer, Tr.T("Ein Grafikzustand hat eine Transferfunktion (/TR)."));
            if (dict.Get(PdfName.Get("TR2")) is not (PdfNull or PdfName { Value: "Default" })) Add(PdfARules.NoTransfer2, Tr.T("Ein Grafikzustand hat eine Transferfunktion (/TR2)."));
            if (dict.Get(PdfName.Get("HT")) is PdfDictionary halftone) Halftone(halftone, null, 0);
            else if (dict.Get(PdfName.Get("HT")) is PdfStream halftoneStream) Halftone(halftoneStream.Dictionary, null, 0);   // types 6, 10, 16
            // the origin of the halftone (PDF 1.x: /HTP; PDF 2.0: /HTO)
            if (dict.ContainsKey(PdfName.Get(Four ? "HTO" : "HTP")))
                Add(PdfARules.NoHalftoneOrigin, Tr.T("Ein Grafikzustand legt den Ursprung des Rasters fest (/{0}).", Four ? "HTO" : "HTP"));
            if (dict.Get(PdfName.Get("RI")) is PdfName ri && !RenderingIntent(ri.Value)) Add(PdfARules.RenderingIntent, Tr.T("Eine unbekannte Farbwiedergabe (Rendering Intent) wird benutzt."));
            // (the blend modes PDF/A-1 does not allow only where the content uses the graphics state)
            if (dict.GetName(PdfName.Type)?.Value == "ExtGState" || dict.ContainsKey(PdfName.Get("BM")))
                if (dict.Get(PdfName.Get("BM")) is not PdfNull and var blend && !StandardBlend(blend, _part.Part() == 1 ? PdfAConformance.A2b : _part))
                    Add(PdfARules.BlendModes, Tr.T("Eine Mischart (Blend Mode) wird benutzt, die PDF/A nicht erlaubt."));
            if (dict.ContainsKey(PdfName.Get("S")) && dict.GetName(PdfName.Get("S")) is { } kind && dict.GetName(PdfName.Type)?.Value is null or "Action" &&
                ActionKinds.Contains(kind.Value))
                Action(dict);
            // the action of an annotation or an outline item without a type (/S): an action of no allowed type (docs/FEHLERGEDAECHTNIS.md, No. 340)
            if (HoldsAction(dict) && dict.Get(PdfName.Get("A")) is PdfDictionary untyped && !untyped.ContainsKey(PdfName.Get("S"))) Action(untyped);
            // font names are UTF-8 (6.1.8; part 4: 6.1.7) — every font in the file, also one no page shows (docs/FEHLERGEDAECHTNIS.md, No. 519)
            if (dict.GetName(PdfName.Type)?.Value == "Font" && dict.GetName(PdfName.Get("BaseFont")) is { } fontName && !Utf8(fontName.Value))
                Add(PdfARules.NamesUtf8, Tr.T("Der Name einer Schrift (/BaseFont) ist kein gültiges UTF-8."));
            // structure type names are UTF-8 (6.1.8): of structure elements and in the role map
            if (dict.GetName(PdfName.Type)?.Value == "StructElem" && dict.GetName(PdfName.Get("S")) is { } structureType && !Utf8(structureType.Value))
                Add(PdfARules.NamesUtf8, Tr.T("Der Name eines Strukturtyps ist kein gültiges UTF-8."));
            if (dict.GetDictionary(PdfName.Get("RoleMap")) is { } roles &&
                roles.Entries.Any(e => !Utf8(e.Key.Value) || e.Value is PdfName role && !Utf8(role.Value)))
                Add(PdfARules.NamesUtf8, Tr.T("Der Name eines Strukturtyps ist kein gültiges UTF-8."));
            if (dict.Get(PdfName.Get("ActualText")) is PdfString actual && HasPrivateUse(actual.ToText()))
                Add(PdfARules.ActualTextNoPua, Tr.T("Ein Ersatztext (/ActualText) enthält Zeichen aus dem privaten Bereich von Unicode."));
            if (IsFileSpec(dict))
            {
                var embedded = dict.Get(PdfName.Get("EF")) is PdfDictionary;
                if (embedded && _part.Part() == 1) Add(PdfARules.NoEmbeddedFiles, Tr.T("Das Dokument enthält Anhänge (/EmbeddedFiles), die PDF/A-1 nicht erlaubt."));
                // the embedded files: those its /EF names (/Type /EmbeddedFile is optional)
                if (dict.GetDictionary(PdfName.Get("EF")) is { } files)
                    foreach (var (key, _) in files.Entries)
                        if (files.Get(key) is PdfStream file && _embedded.Add(file)) EmbeddedFile(file);
                if (embedded && (!dict.ContainsKey(PdfName.Get("F")) || !dict.ContainsKey(PdfName.Get("UF"))))
                    Add(PdfARules.AttachmentNames, Tr.T("Bei einem Anhang fehlt der Dateiname (/F und /UF)."));
                // part 3 asks every file specification for its relationship, part 4 those of embedded files
                if ((embedded || _part.Part() == 3) && !dict.ContainsKey(PdfName.Get("AFRelationship")))
                    Add(PdfARules.AttachmentRelationship, Tr.T("Bei einer Dateiangabe fehlt die Beziehung zum Dokument (/AFRelationship)."));
                _fileSpecs.Add(dict);
            }
            if (dict.Get(PdfName.Get("AF")) is PdfArray associated)
                for (var i = 0; i < associated.Count && i < 10_000; i++)
                    if (associated.Get(i) is PdfDictionary spec) _associated.Add(spec);
        }

        /// <summary>An embedded file (6.8; ISO 19005-4, 6.9): in PDF/A-2 and PDF/A-4 a confirmed PDF/A file; its kind as a MIME type.</summary>
        private void EmbeddedFile(PdfStream file)
        {
            if (PdfARules.AttachmentIsPdfA.Id(_part) is not null && !AllowedAttachment(StreamData(file), _part, Depth))
                Add(PdfARules.AttachmentIsPdfA, Tr.T("Das Dokument enthält einen Anhang, der kein PDF/A ist."));
            if (file.Dictionary.GetName(PdfName.Subtype) is not { } mime || !MimeType(mime.Value))
                Add(PdfARules.AttachmentMime, Tr.T("Bei einem Anhang fehlt die Dateiart (/Subtype als MIME-Typ)."));
        }

        /// <summary>
        /// An additional-actions entry (/AA) with a trigger the part does not allow on its owner (<see cref="AllowedTrigger"/>); in parts 2 and 3
        /// the entry itself where none is allowed.
        /// </summary>
        private bool TriggersNotAllowed(PdfDictionary dict, TriggerOwner owner) => dict.Get(PdfName.Get("AA")) switch
        {
            PdfNull => false,
            PdfDictionary triggers => triggers.Keys.Any(k => !AllowedTrigger(k.Value, owner, _part)) || !Four && !AllowedTrigger("", owner, _part),
            _ => !Four && !AllowedTrigger("", owner, _part),
        };

        /// <summary>The colour space of the first PDF/A output intent with a usable profile; null if none.</summary>
        /// <summary>The profile of the PDF/A output intent in a list; null for none.</summary>
        private static PdfStream? IntentProfileOf(PdfArray intents)
        {
            for (var i = 0; i < intents.Count && i < 64; i++)
                if (intents.Get(i) is PdfDictionary intent && intent.GetName(PdfName.Get("S"))?.Value == "GTS_PDFA1" &&
                    intent.Get(PdfName.Get("DestOutputProfile")) is PdfStream profile)
                    return profile;
            return null;
        }

        private uint? IntentSpace(PdfArray intents)
        {
            for (var i = 0; i < intents.Count && i < 64; i++)
                if (intents.Get(i) is PdfDictionary intent && intent.GetName(PdfName.Get("S"))?.Value == "GTS_PDFA1" &&
                    intent.Get(PdfName.Get("DestOutputProfile")) is PdfStream profile)
                    return UsableProfile(doc.GetStreamData(profile), _part.Limits().IccMajor);
            return null;
        }

        /// <summary>The device space a transparency group's colour space stands for (ICC-based by its components, calibrated by kind).</summary>
        /// <summary>
        /// A halftone (6.2.5): of type 1 or 5, without a name; a /TransferFunction only where ISO 32000 asks for it — for colourants other than the
        /// primaries in a type 5 halftone, never for the primaries or a halftone of its own (<paramref name="colorant"/> null).
        /// </summary>
        private void Halftone(PdfDictionary halftone, string? colorant, int depth)
        {
            if (depth > 4) return;
            var type = halftone.GetInteger(PdfName.Get("HalftoneType"));
            if (type is not (1 or 5)) Add(PdfARules.HalftoneType, Tr.T("Ein Grafikzustand hat ein Raster anderer Art als 1 oder 5 (/HalftoneType)."));
            if (halftone.ContainsKey(PdfName.Get("HalftoneName"))) Add(PdfARules.HalftoneName, Tr.T("Ein Raster nennt einen Rasternamen (/HalftoneName)."));
            if (colorant != "Default")
            {
                var primary = colorant is null or "Cyan" or "Magenta" or "Yellow" or "Black";
                var transfer = halftone.ContainsKey(PdfName.Get("TransferFunction"));
                if (primary == transfer)
                    Add(PdfARules.HalftoneTransfer, Tr.T("Ein Raster hat eine Transferfunktion (/TransferFunction), wo ISO 32000 keine vorsieht, oder keine, wo sie verlangt ist."));
            }
            if (type == 5)
                foreach (var (key, _) in halftone.Entries.Take(64))
                    if (key.Value is not ("Type" or "HalftoneType" or "HalftoneName") && halftone.Get(key) is PdfDictionary component)
                        Halftone(component, key.Value, depth + 1);
                    else if (key.Value is not ("Type" or "HalftoneType" or "HalftoneName") && halftone.Get(key) is PdfStream componentStream)
                        Halftone(componentStream.Dictionary, key.Value, depth + 1);
        }

        /// <summary>The profile of a transparency group's colour space where it is ICC-based; null otherwise.</summary>
        private static PdfStream? BlendProfile(PdfDictionary? group) =>
            group?.GetName(PdfName.Get("S"))?.Value == "Transparency" && group.Get(PdfName.Get("CS")) is PdfArray { Count: >= 2 } space &&
            space.Get(0) is PdfName { Value: "ICCBased" } && space.Get(1) is PdfStream profile
                ? profile
                : null;

        private static uint? BlendSpace(PdfObject space) => space switch
        {
            // (a device space is no blending space that allows device colours: it must be device-independent — asked of the reference
            // validator)
            PdfArray { Count: >= 2 } array when array.Get(0) is PdfName { Value: "ICCBased" } && array.Get(1) is PdfStream profile =>
                profile.Dictionary.GetInteger(PdfName.Get("N")) switch
                {
                    1 => IccProfile.GraySpace,
                    3 => IccProfile.RgbSpace,
                    4 => IccProfile.CmykSpace,
                    _ => null,
                },
            PdfArray { Count: >= 1 } array when array.Get(0) is PdfName { Value: "CalRGB" } => IccProfile.RgbSpace,
            PdfArray { Count: >= 1 } array when array.Get(0) is PdfName { Value: "CalGray" } => IccProfile.GraySpace,
            _ => null,
        };
    }
}
