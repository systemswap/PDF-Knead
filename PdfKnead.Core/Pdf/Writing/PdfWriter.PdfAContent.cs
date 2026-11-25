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
        /// A stream for PDF/A, where its data must change: LZW and the filters PDF/A does not know are not allowed (6.1.7.2) — unpacked and packed
        /// with Flate, an image filter after them (rare) stays; a content stream whose syntax breaks the standard is written anew (<see cref="ContentConformance"/>). A form without
        /// resources of its own that uses some gets those it is drawn with (6.2.2). Null for streams that stay as they are or cannot be read.
        /// </summary>
        private static void WithoutExternal(PdfDictionary dict)
        {
            dict.Remove(F);
            dict.Remove(PdfName.Get("FFilter"));
            dict.Remove(PdfName.Get("FDecodeParms"));
        }

        /// <summary>
        /// An image PDF/A does not allow as it is: a JPEG 2000 image whose only fault is the missing JP2 wrapper gets one, unchanged inside
        /// (6.2.8.3); a JPEG 2000 image with other faults and an image with a bit depth PDF/A does not list (6.2.8) are written as the display
        /// decodes them (<see cref="DisplayedImage"/>). Null for an image that stays as it is.
        /// </summary>
        private (PdfDictionary Dict, byte[] Data, bool Compress)? PdfAImage(PdfStream stream, Source source, PdfDictionary dict)
        {
            var image = stream.Dictionary;
            var bits = image.GetInteger(PdfName.Get("BitsPerComponent"));
            var badBits = image.GetBoolean(PdfName.Get("ImageMask")) == true ? bits is { } m && m != 1 : bits is { } b && b is not (1 or 2 or 4 or 8 or 16);
            badBits |= image.Get(PdfName.Get("Mask")) is PdfStream mask && mask.Dictionary.GetInteger(PdfName.Get("BitsPerComponent")) is { } maskBits &&
                maskBits != 1;
            var jpxFaults = StreamFilters.Chain(image).Any(f => f.Name == "JPXDecode") && JpxBytes(stream, source) is { } jpx
                ? PdfAJpeg2000.Faults(jpx, image.ContainsKey(PdfName.Get("ColorSpace")))
                : [];
            // a filter PDF/A does not know (6.1.7.2): the image as the display decodes it — where it cannot, its placeholder area
            if (PdfACheck.FilterNames(image).Any(f => !PdfACheck.IsStandardFilter(f, _pdfA!.Part))) return DisplayedImage(stream, source, dict);
            // a DeviceN space with more colourants than the part allows: the image as the display draws it
            if (Wide(image.GetRaw(PdfName.Get("ColorSpace")), source) is not null) return DisplayedImage(stream, source, dict);
            // PDF/A-1 (ISO 19005-1, 6.1.10, 6.2.4, 6.4): PDF 1.4 knows neither JPEG 2000 nor 16 bits, and there are no soft masks
            if (_pdfA!.Part.Part() == 1)
            {
                if (StreamFilters.Chain(image).Any(f => f.Name == "JPXDecode") || bits == 16) return DisplayedImage(stream, source, dict);
                if (image.Get(PdfName.Get("SMask")) is PdfStream softMask && !badBits && jpxFaults.Count == 0)
                {
                    // flattened: what showed through partly is in the images over the page, the image itself is drawn opaque underneath
                    if (WithoutSoftMask(stream, softMask, source, dict) is { } kept) return kept;
                    if (_options.PdfAFlattened)
                    {
                        var opaque = Clone(dict);
                        opaque.Remove(PdfName.Get("SMask"));
                        return (opaque, source.Doc.GetRawStreamData(stream).ToArray(), false);
                    }
                }
            }
            if (!badBits && jpxFaults.Count == 0) return null;
            // (the opacity channel /SMaskInData announces for a bare codestream named in the wrapper, as when saving: No. 996)
            if (!badBits && StreamFilters.Chain(image) is [("JPXDecode", _)] && source.Doc.GetRawStreamData(stream).ToArray() is var raw &&
                ImageDataConformance.BareOpacity(image, raw, source.Doc) is >= 0 and var opacity &&
                PdfAJpeg2000.Wrap(raw, opacity) is { } wrapped && PdfAJpeg2000.Faults(wrapped, image.ContainsKey(PdfName.Get("ColorSpace"))).Count == 0)
                return (Clone(dict), wrapped, false);
            return DisplayedImage(stream, source, dict);
        }

        /// <summary>
        /// An image with a soft mask for PDF/A-1, which allows none (6.4), where the mask changes nothing or only shows and hides: a mask that
        /// is opaque everywhere goes, one that is only fully opaque or fully transparent becomes a mask of one bit at its own size (/Mask: the
        /// display samples both the same way); the image data stay as they are. Null where the mask lets something show through partly — that
        /// cannot be shown without transparency, and the check names it.
        /// </summary>
        private (PdfDictionary Dict, byte[] Data, bool Compress)? WithoutSoftMask(PdfStream stream, PdfStream softMask, Source source, PdfDictionary dict)
        {
            if (Graphics.PdfImageDecoder.MaskOpacity(softMask, source.Doc) is not var (width, height, alpha) ||
                alpha.Any(a => a is not (0 or 255)))
                return null;
            var result = Clone(dict);
            result.Remove(PdfName.Get("SMask"));
            if (alpha.Any(a => a == 0)) result.Set(PdfName.Get("Mask"), BinaryMask(width, height, alpha));
            return (result, source.Doc.GetRawStreamData(stream).ToArray(), false);
        }

        /// <summary>An opacity of only 0 and 255 as a mask of one bit (/Mask as a stream: 0 paints, as in a stencil mask).</summary>
        private PdfObject BinaryMask(int width, int height, byte[] alpha)
        {
            var row = (width + 7) / 8;
            var bits = new byte[row * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (alpha[y * width + x] == 0) bits[y * row + x / 8] |= (byte)(0x80 >> (x % 8));
            var maskDict = new PdfDictionary();
            maskDict.Set(PdfName.Type, PdfName.Get("XObject"));
            maskDict.Set(PdfName.Subtype, PdfName.Get("Image"));
            maskDict.Set(PdfName.Get("Width"), PdfInteger.Of(width));
            maskDict.Set(PdfName.Get("Height"), PdfInteger.Of(height));
            maskDict.Set(PdfName.Get("ImageMask"), PdfBoolean.True);
            maskDict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(1));
            maskDict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            return Fresh(new PdfStream(maskDict, Flate.Encode(bits)));
        }

        /// <summary>The JPEG 2000 data of an image stream (after other filters); null if unreadable.</summary>
        private static byte[]? JpxBytes(PdfStream stream, Source source)
        {
            try
            {
                return source.Doc.DecodeStream(stream).Data;
            }
            catch (PdfException)
            {
                return null;
            }
        }

        /// <summary>
        /// An image as the display decodes it, at its full size: the colours as sRGB, 8 bits, packed with Flate, what shows through as a soft
        /// mask; an image mask as a 1-bit mask of what it paints. An image the display cannot decode is shown as a placeholder area
        /// (<see cref="Graphics.PdfImageDecoder.Placeholder"/>), and so it is written: one pixel of that colour.
        /// </summary>
        private (PdfDictionary Dict, byte[] Data, bool Compress) DisplayedImage(PdfStream stream, Source source, PdfDictionary dict)
        {
            var stencil = stream.Dictionary.GetBoolean(PdfName.Get("ImageMask")) == true;
            var shown = new Graphics.ImageSource(stream, null, null, stencil, 0xFF000000);
            var result = Clone(dict);
            foreach (var key in (string[])["Filter", "DecodeParms", "ColorSpace", "BitsPerComponent", "Decode", "SMask", "Mask", "SMaskInData", "Interpolate",
                                           "Alternates", "OPI", "ImageMask"])
                result.Remove(PdfName.Get(key));
            if (Decoded(shown, source) is not { } decoded)
            {
                var (r, g, b) = Graphics.PdfImageDecoder.Placeholder;
                result.Set(PdfName.Get("Width"), PdfInteger.Of(1));
                result.Set(PdfName.Get("Height"), PdfInteger.Of(1));
                result.Set(PdfName.Get("ColorSpace"), SrgbSpace());
                result.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
                return (result, [r, g, b], true);
            }
            result.Set(PdfName.Get("Width"), PdfInteger.Of(decoded.Width));
            result.Set(PdfName.Get("Height"), PdfInteger.Of(decoded.Height));
            var pixels = decoded.Width * decoded.Height;
            if (stencil)
            {
                // a stencil mask: 0 paints (/Decode [0 1])
                var coverage = decoded.Format == Graphics.ImagePixels.Alpha ? decoded.Pixels : [.. Enumerable.Range(0, pixels).Select(i => decoded.Rgba[i * 4 + 3])];
                var row = (decoded.Width + 7) / 8;
                var mask = new byte[row * decoded.Height];
                Array.Fill(mask, (byte)0xFF);
                for (var y = 0; y < decoded.Height; y++)
                    for (var x = 0; x < decoded.Width; x++)
                        if (coverage[y * decoded.Width + x] >= 128) mask[y * row + x / 8] &= (byte)~(0x80 >> (x % 8));
                result.Set(PdfName.Get("ImageMask"), PdfBoolean.True);
                result.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(1));
                return (result, mask, true);
            }
            var rgba = decoded.Rgba;
            var rgb = new byte[pixels * 3];
            var alpha = new byte[pixels];
            var translucent = false;
            for (var i = 0; i < pixels; i++)
            {
                rgb[i * 3] = rgba[i * 4];
                rgb[i * 3 + 1] = rgba[i * 4 + 1];
                rgb[i * 3 + 2] = rgba[i * 4 + 2];
                alpha[i] = rgba[i * 4 + 3];
                translucent |= alpha[i] != 255;
            }
            result.Set(PdfName.Get("ColorSpace"), SrgbSpace());
            result.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
            // PDF/A-1 has no soft masks (6.4): an opacity that only shows and hides becomes a mask of one bit; partial opacity stays, and the
            // check names it
            if (translucent && _pdfA!.Part.Part() == 1 && alpha.All(a => a is 0 or 255))
                result.Set(PdfName.Get("Mask"), BinaryMask(decoded.Width, decoded.Height, alpha));
            // PDF/A-1 flattened: partial opacity is in the images over the page, the image itself drawn opaque underneath
            else if (translucent && !(_pdfA!.Part.Part() == 1 && _options.PdfAFlattened))
            {
                var maskDict = new PdfDictionary();
                maskDict.Set(PdfName.Type, PdfName.Get("XObject"));
                maskDict.Set(PdfName.Subtype, PdfName.Get("Image"));
                maskDict.Set(PdfName.Get("Width"), PdfInteger.Of(decoded.Width));
                maskDict.Set(PdfName.Get("Height"), PdfInteger.Of(decoded.Height));
                maskDict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
                maskDict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
                maskDict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
                result.Set(PdfName.Get("SMask"), Fresh(new PdfStream(maskDict, Flate.Encode(alpha))));
            }
            return (result, rgb, true);
        }

        /// <summary>The image decoded as the display decodes it; null where the display cannot (it then shows a placeholder).</summary>
        private Graphics.DecodedImage? Decoded(Graphics.ImageSource shown, Source source)
        {
            try
            {
                return Graphics.PdfImageDecoder.DecodeFull(shown, source.Doc, Graphics.PdfImageDecoder.FullSize(shown));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private PdfArray? _srgbSpace;

        /// <summary>An ICC-based sRGB space (the own profile), for images written as the display decodes them.</summary>
        private PdfArray SrgbSpace() => _pdfA!.Rgb ?? (_srgbSpace ??= new PdfArray([IccBased, Fresh(IccStream(CalibratedProfile.Srgb(Pdf20), 3))]));

        private (PdfDictionary Dict, byte[] Data, bool Compress)? PdfAStream(PdfStream stream, Source source, PdfDictionary dict)
        {
            var typed = false;
            var usage = _pdfA!.Usage;
            // LZW and the filters PDF/A does not know (Brotli, Crypt, abbreviations): unpacked, and packed with Flate if they can be read
            var lzw = PdfACheck.FilterNames(stream.Dictionary).Any(f => !PdfACheck.IsStandardFilter(f, _pdfA!.Part));
            // (also a content stream with marked content that becomes artifact content, 6.5 f)
            var rewrite = usage.Rewrites.Contains(stream) || _pdfA.ArtifactMcidsByStream.ContainsKey(stream);
            if (stream.Dictionary.GetName(PdfName.Subtype)?.Value == "Image" && PdfAImage(stream, source, dict) is { } image) return image;
            // an embedded file names its kind as a MIME type (parts 3 and 4: 6.8, ISO 19005-4, 6.9): a PDF as such, anything else as unknown data
            if (PdfARules.AttachmentMime.Id(_pdfA!.Part) is not null && EmbeddedFiles(source).Contains(stream) &&
                (stream.Dictionary.GetName(PdfName.Subtype) is not { } mime || !PdfACheck.MimeType(mime.Value)))
            {
                dict = Clone(dict);
                dict.Set(PdfName.Subtype, PdfName.Get(EmbeddedData(stream, source).AsSpan().StartsWith("%PDF-"u8) ? "application/pdf" : "application/octet-stream"));
                typed = true;
            }
            // a CFF program whose font name is no ASCII (6.1.8)
            if (!lzw && !rewrite && stream.Dictionary.GetName(PdfName.Subtype)?.Value is "Type1C" or "CIDFontType0C")
                try
                {
                    if (CffBuilder.AsciiNames(source.Doc.DecodeStream(stream).Data) is { } named)
                    {
                        var renamed = Clone(dict);
                        renamed.Remove(PdfName.Filter);
                        renamed.Remove(PdfName.DecodeParms);
                        return (renamed, named, true);
                    }
                }
                catch (PdfException)
                {
                }
            // resources it is drawn with but does not have itself (6.2.2): none of its own, or the font the display draws text with whose font
            // the resources lack
            // (a form never drawn: those of the first page that names it, V10; docs/FEHLERGEDAECHTNIS.md, No. 1131)
            var borrowed = usage.Borrowed.TryGetValue(stream.Dictionary, out var resources) ? resources : _pdfAUndrawn.GetValueOrDefault(stream.Dictionary);
            var missing = usage.Missing.GetValueOrDefault(stream);
            if (missing is not null && WithMissingFonts(borrowed ?? stream.Dictionary.GetDictionary(PdfName.Resources), missing) is { } withFonts) borrowed = withFonts;
            // data in another file (6.1.7.1): the display does not read it either — the data in this file stay, the reference goes
            var external = dict.ContainsKey(F) || dict.ContainsKey(PdfName.Get("FFilter")) || dict.ContainsKey(PdfName.Get("FDecodeParms"));
            if (!lzw && !rewrite)
            {
                if (borrowed is null && !external && !typed) return null;
                // only the dictionary changes: the data as stored
                var withResources = Clone(dict);
                if (borrowed is not null) withResources.Set(PdfName.Resources, borrowed);
                WithoutExternal(withResources);
                return (withResources, source.Doc.GetRawStreamData(stream).ToArray(), false);
            }
            try
            {
                var decoded = source.Doc.DecodeStream(stream);
                var result = Clone(dict);
                result.Remove(PdfName.Filter);
                result.Remove(PdfName.DecodeParms);
                WithoutExternal(result);
                if (borrowed is not null) result.Set(PdfName.Resources, borrowed);
                if (decoded.ImageFilter is { } filter)
                {
                    result.Set(PdfName.Filter, filter);
                    if (decoded.ImageParms is { } parms) result.Set(PdfName.DecodeParms, parms);
                    return (result, decoded.Data, false);
                }
                if (!rewrite) return (result, decoded.Data, true);
                // a form, pattern or appearance nested too deep: what moves into forms of its own gets them in its resources
                var drawnWith = borrowed ?? stream.Dictionary.GetDictionary(PdfName.Resources) ?? usage.PageResources.GetValueOrDefault(stream);
                var nests = usage.Deep.Contains(stream) && stream.Dictionary.ContainsKey(PdfName.Get("BBox")) ? new List<(PdfName, byte[])>() : null;
                // PDF/A-4: inline images over 4 096 bytes become image XObjects in the resources the stream draws with, now its own — a glyph
                // procedure's go where its plan says (the font's resources or its own; docs/FEHLERGEDAECHTNIS.md, No. 1131)
                var glyph = GlyphPlanOf(source.Doc, stream);
                // (only a form or a tiling pattern, whose resources these are: a page's content goes through PdfAContent, a glyph procedure
                // through its plan — given resources of its own, it would no longer draw with the font's)
                var placed = _pdfA.Part.Part() == 4 && glyph is null &&
                             (stream.Dictionary.GetName(PdfName.Subtype)?.Value == "Form" || stream.Dictionary.ContainsKey(PdfName.Get("PatternType")))
                    ? new List<(PdfName Name, PdfStream Image)>() : null;
                var data = ContentConformance.Rewrite(decoded.Data, RewriteOptions(drawnWith, source.Doc, missing, nests, MarksFor(stream, null), ArtifactsFor(stream, null),
                    placed, glyph));
                if (nests is { Count: > 0 } || placed is { Count: > 0 }) result.Set(PdfName.Resources, NestedResources(drawnWith, source, nests ?? [], placed));                return (result, data, true);
            }
            catch (PdfException) when (rewrite)
            {
                // a content stream the display cannot read draws nothing: written empty
                var empty = Clone(dict);
                empty.Remove(PdfName.Filter);
                empty.Remove(PdfName.DecodeParms);
                WithoutExternal(empty);
                if (borrowed is not null) empty.Set(PdfName.Resources, borrowed);
                return (empty, [], false);
            }
            catch (PdfException) when (lzw)
            {
                // packed with a filter no reader knows (a name that is no filter): nobody can read the data — written empty, without it
                // (docs/FEHLERGEDAECHTNIS.md, No. 340)
                var unreadable = Clone(dict);
                unreadable.Remove(PdfName.Filter);
                unreadable.Remove(PdfName.DecodeParms);
                WithoutExternal(unreadable);
                return (unreadable, [], false);
            }
            catch (PdfException)
            {
                return null;
            }
        }

        /// <summary>
        /// The content of a page made of several streams as one stream written anew (<see cref="ContentConformance.Rewrite"/>): joined as the
        /// display joins them (a line end between the parts; a part it cannot read is left out), so that an operator split from its operands
        /// across two parts stays whole. A page of one stream only when it is nested too deep (<see cref="PdfACheck.Usage.Deep"/>): what moves into
        /// forms of its own needs them in the page's resources — and for PDF/A-4 when it holds an inline image, which may become an image XObject
        /// of the page (8.9.7; docs/FEHLERGEDAECHTNIS.md, No. 1131). Null if the page has no parts or one that needs nothing of this.
        /// </summary>
        /// <param name="page">The page as read.</param>
        /// <param name="resources">Its resources as read (inherited ones too).</param>
        /// <param name="source">Its source.</param>
        /// <param name="nestedResources">The page's resources with the forms of deep content added, written; null if no content moved.</param>
        private PdfObject? PdfAContent(PdfDictionary page, PdfDictionary? resources, Source source, out PdfObject? nestedResources)
        {
            nestedResources = null;
            var parts = page.Get(PdfName.Contents) switch
            {
                PdfArray array => array,
                PdfStream single when _pdfA!.Usage.Deep.Contains(single) || _pdfA.Part.Part() == 4 && MayHoldInline(single, source) => new PdfArray([single]),
                _ => null,
            };
            if (parts is null) return null;
            // bounded together as one stream (docs/FEHLERGEDAECHTNIS.md, No. 1002), counted while joined (No. 1003)
            var all = new ContentJoiner(parts.Count);
            var missing = new HashSet<(string Category, PdfName Name)>();
            for (var i = 0; i < parts.Count && i < 10_000; i++)
            {
                if (parts.Get(i) is not PdfStream part) continue;
                byte[] decoded;
                try
                {
                    decoded = source.Doc.DecodeStream(part).Data;
                }
                catch (PdfException)
                {
                    decoded = [];
                }
                all.Add(decoded);
                if (_pdfA!.Usage.Missing.GetValueOrDefault(part) is { } names) missing.UnionWith(names);
            }
            var nests = new List<(PdfName, byte[])>();
            var placed = _pdfA!.Part.Part() == 4 ? new List<(PdfName Name, PdfStream Image)>() : null;
            var data = ContentConformance.Rewrite(all.ToArray(), RewriteOptions(resources, source.Doc, missing.Count > 0 ? missing : null, nests, MarksFor(null, page),
                ArtifactsFor(null, page), placed));
            if (nests.Count > 0 || placed is { Count: > 0 }) nestedResources = NestedResources(resources, source, nests, placed);
            var dict = new PdfDictionary();
            dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            return Fresh(new PdfStream(dict, Flate.Encode(data)));
        }

        /// <summary>The test <see cref="ContentConformance.Rewrite"/> asks: is the name missing from the content's resources?</summary>
        private static Func<string, PdfName, bool>? Missing(HashSet<(string Category, PdfName Name)>? missing) =>
            missing is null ? null : (category, name) => missing.Contains((category, name));

        /// <summary>
        /// What <see cref="ContentConformance.Rewrite"/> observes for a content drawn with <paramref name="resources"/> (as read): the limits of the
        /// part, the names they lack, where the codes of their fonts end (to split long strings), and — given <paramref name="nests"/> — where
        /// content nested too deep goes (a form under a name the resources do not have yet). PDF/A-4: given <paramref name="placed"/>, an inline
        /// image over 4 096 bytes becomes an image XObject under a name the resources do not have yet; given <paramref name="glyph"/>, the plan of
        /// a glyph procedure names those that do (docs/FEHLERGEDAECHTNIS.md, No. 1131).
        /// </summary>
        private ContentRewrite RewriteOptions(PdfDictionary? resources, PdfDocument doc, HashSet<(string Category, PdfName Name)>? missing,
            List<(PdfName Name, byte[] Content)>? nests, PdfATagging.PageMarks? marks = null, IReadOnlySet<int>? artifacts = null,
            List<(PdfName Name, PdfStream Image)>? placed = null, Type3GlyphPlan? glyph = null)
        {
            var part = _pdfA!.Part;
            var taken = resources?.GetDictionary(XObject);
            return new ContentRewrite
            {
                Missing = Missing(missing),
                NoPrivateUse = part.Part() == 4,
                InlineLength = part.Part() == 4,
                RealLimit = part.Limits().Real,
                StringLimit = part.Limits().String,
                ArrayLimit = part.Part() == 1 ? 8191 : int.MaxValue,
                CodeEnd = (name, bytes, start, room) => CodeEnd(resources, doc, name, bytes, start, room),
                Codes = (name, text) => WithoutStrayCodes(resources, doc, name, text),
                Colours = name => ColourChangeOf(resources, doc, name),
                Marks = marks,
                ArtifactMcids = artifacts,
                NamedMcid = name => resources?.GetDictionary(PdfName.Get("Properties"))?.GetDictionary(name)?.Get(PdfName.Get("MCID")) as PdfInteger,
                StripMcids = _pdfA.Tags is not null,
                Language = part.Accessible() ? given => PdfACheck.LanguageTag(given, part.Part()) ? given : PdfATagging.CleanLanguage(given, part.Part()) : null,
                Nest = nests is null ? null : content =>
                {
                    var name = ContentNesting.FreeName(nests.Count + 1, n => taken?.ContainsKey(n) == true || nests.Any(x => x.Name == n) ||
                                                                            placed?.Any(x => x.Name == n) == true);
                    nests.Add((name, content));
                    return name;
                },
                PlaceImage = part.Part() != 4 ? null
                    : glyph is not null ? (_, ordinal) => glyph.Ordinals.GetValueOrDefault(ordinal)
                    : placed is null ? null
                    : (image, _) =>
                    {
                        if (PlacedImage(image, resources) is not { } xobject) return null;
                        var name = InlineImageConformance.FreeName(n => taken?.ContainsKey(n) == true || nests?.Any(x => x.Name == n) == true ||
                                                                        placed.Any(x => x.Name == n));
                        placed.Add((name, xobject));
                        return name;
                    },
            };
        }

        /// <summary>
        /// The image XObject a PDF/A-4 copy makes of an inline image (<see cref="InlineImageConformance.AsXObject"/>, a colour space named in
        /// <paramref name="resources"/> as the space itself), noted to be written the way of every image of the copy (<see cref="_pdfAPlaced"/>);
        /// null where its colour space is a name the resources do not give (it stays inline, as the display reads it).
        /// </summary>
        private PdfStream? PlacedImage(InlineImage image, PdfDictionary? resources)
        {
            var xobject = InlineImageConformance.AsXObject(image, resources);
            if (xobject.Dictionary.Get(PdfName.Get("ColorSpace")) is PdfName { Value: not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK") }) return null;
            _pdfAPlaced.Add(xobject);
            return xobject;
        }

        /// <summary>Inline images of PDF/A-4 made image XObjects (<see cref="PlacedImage"/>): written through <see cref="WriteStream"/> with their
        /// source, so that they go the way of every image of the copy (<see cref="PdfAStream"/>: colours, filters, bit depths).</summary>
        private readonly HashSet<PdfStream> _pdfAPlaced = new(ReferenceEqualityComparer.Instance);

        /// <summary>Whether a content stream may hold an inline image (<see cref="InlineImageConformance.MayHold"/>); false where it cannot be read.</summary>
        private static bool MayHoldInline(PdfStream stream, Source source)
        {
            try
            {
                return InlineImageConformance.MayHold(source.Doc.DecodeStream(stream).Data);
            }
            catch (PdfException)
            {
                return false;
            }
        }

        /// <summary>
        /// The end of the last whole code of the font named <paramref name="name"/> in <paramref name="resources"/> within <paramref name="room"/>
        /// bytes from <paramref name="start"/> — as the display reads the codes; without the font at a boundary of two bytes.
        /// </summary>
        private static int CodeEnd(PdfDictionary? resources, PdfDocument doc, PdfName? name, byte[] bytes, int start, int room)
        {
            var even = start + (room & ~1);
            if (name is null || resources?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(name) is not { } dict) return even;
            PdfFont font;
            try
            {
                font = PdfFont.Get(dict, doc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return even;
            }
            var pos = start;
            while (pos < start + room)
            {
                var length = font.ReadCode(bytes, pos, out _);
                if (length <= 0 || pos + length > start + room) break;
                pos += length;
            }
            return pos > start ? pos : even;
        }

        /// <summary>
        /// A string of a composite font with codes outside its code space (a string ending in the middle of a code; ISO 32000-2, 9.7.6.3 leaves
        /// open how to read them, and readers differ — the display draws a glyph, strict readers another or none): without them, followed by a
        /// TJ displacement of the advance the display gave them, so that what follows stays in place (the character spacing of the left-out
        /// codes aside). Null where every code lies in the code space (docs/FEHLERGEDAECHTNIS.md, No. 340).
        /// </summary>
        private static PdfObject[]? WithoutStrayCodes(PdfDictionary? resources, PdfDocument doc, PdfName? name, PdfString text)
        {
            if (name is null || resources?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(name) is not { } dict) return null;
            CompositeFont font;
            try
            {
                if (PdfFont.Get(dict, doc) is not CompositeFont composite) return null;
                font = composite;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
            var bytes = text.Bytes;
            List<byte>? kept = null;
            var advance = 0.0;
            for (var pos = 0; pos < bytes.Length;)
            {
                var length = Math.Max(1, font.ReadCode(bytes, pos, out var code));
                if (!font.InCodespace(code, length))
                {
                    kept ??= [.. bytes.AsSpan(0, pos)];
                    var glyph = font.Glyph(code, length);
                    advance += font.Vertical ? glyph.VerticalAdvance : glyph.Width;
                }
                else kept?.AddRange(bytes.AsSpan(pos, Math.Min(length, bytes.Length - pos)));
                pos += length;
            }
            if (kept is null) return null;
            var rest = new PdfString([.. kept], text.IsHex);
            return advance == 0 || !double.IsFinite(advance) ? [rest] : [rest, new PdfReal(Math.Round(-advance * 1000, 9))];
        }

        /// <summary>
        /// Resources (as read) with the forms deep content moved into (<see cref="ContentNesting"/>) under their names, written: one dictionary
        /// for the content and its forms (a form's content can move further), the values as read.
        /// </summary>
        private PdfObject NestedResources(PdfDictionary? resources, Source source, List<(PdfName Name, byte[] Content)> nests,
            List<(PdfName Name, PdfStream Image)>? placed = null)
        {
            var shared = new PdfDictionary();
            if (resources is not null)
                foreach (var (key, value) in resources.Entries)
                    if (key != XObject) shared.Set(key, value);
            var xobjects = new PdfDictionary();
            if (resources?.GetDictionary(XObject) is { } known)
                foreach (var (key, value) in known.Entries) xobjects.Set(key, value);
            foreach (var (name, content) in nests) xobjects.Set(name, ContentNesting.Form(content, shared));
            // the inline images made image XObjects (PDF/A-4, No. 1131)
            if (placed is not null)
                foreach (var (name, image) in placed) xobjects.Set(name, image);
            shared.Set(XObject, xobjects);
            return Rebase(shared, source);
        }

        /// <summary>
        /// Resources with the font the display draws text with whose font they lack (<see cref="PdfACheck.MissingFont"/>, embedded once) under each
        /// missing name; null if no font is missing.
        /// </summary>
        private PdfDictionary? WithMissingFonts(PdfDictionary? resources, HashSet<(string Category, PdfName Name)> missing)
        {
            if (_pdfA!.MissingFont is not { } font || !missing.Any(m => m.Category == "Font")) return null;
            var result = resources is null ? new PdfDictionary() : CloneWithResolver(resources);
            var fonts = result.GetDictionary(PdfName.Get("Font")) is { } known ? CloneWithResolver(known) : new PdfDictionary();
            foreach (var (category, name) in missing)
                if (category == "Font") fonts.Set(name, font);
            result.Set(PdfName.Get("Font"), fonts);
            return result;
        }

        /// <summary>A page whose content shows text with a font its resources lack: the resources with the display's font (see above).</summary>
        private void PdfAMissingFonts(PdfDictionary page, PdfDictionary original, Source source)
        {
            if (_pdfA!.MissingFont is null) return;
            var parts = original.Get(PdfName.Contents) switch
            {
                PdfStream single => [single],
                PdfArray array => Enumerable.Range(0, Math.Min(array.Count, 10_000)).Select(array.Get).OfType<PdfStream>().ToList(),
                _ => new List<PdfStream>(),
            };
            var missing = new HashSet<(string Category, PdfName Name)>();
            foreach (var part in parts)
                if (_pdfA.Usage.Missing.GetValueOrDefault(part) is { } names) missing.UnionWith(names);
            if (!missing.Any(m => m.Category == "Font")) return;
            // resources the writer assembled (new elements on the page) are extended; otherwise those of the source with the fonts
            if (page.Get(PdfName.Resources) is PdfDictionary assembled && page.GetRaw(PdfName.Resources) is not (PdfReference or OutputReference))
                page.Set(PdfName.Resources, WithMissingFonts(assembled, missing)!);
            else
            {
                var resources = original.GetDictionary(PdfName.Resources) ??
                                (InheritedValue(original, PdfName.Resources) switch
                                {
                                    PdfDictionary d => d,
                                    PdfReference r => source.Doc.GetObject(r.Id.Number) as PdfDictionary,
                                    _ => null,
                                });
                page.Set(PdfName.Resources, Rebase(WithMissingFonts(resources, missing)!, source));
            }
        }
    }
}
