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
    /// <summary>Whether a colour space is one of the device-independent families (ICCBased, CalRGB, CalGray, Lab; No. 509).</summary>
    internal static bool Independent(PdfObject? space) =>
        space is PdfArray { Count: >= 2 } array && array.Get(0) is PdfName { Value: "ICCBased" or "CalRGB" or "CalGray" or "Lab" };

    /// <summary>The rules on colour spaces, patterns and inline images.</summary>
    private sealed partial class Run
    {
        private void InlineImage(PdfDictionary image, PdfDictionary? resources)
        {
            if (Either(image, "I", "Interpolate") is PdfBoolean { Value: true })
                Add(PdfARules.ImageInterpolate, Tr.T("Ein Bild soll beim Vergrößern geglättet werden (/Interpolate)."));
            var filters = Either(image, "F", "Filter");
            // from part 2 only the filters listed for inline images (ISO 19005-2, 6.1.10): no Crypt, JBIG2, JPX or unknown names
            // (docs/FEHLERGEDAECHTNIS.md, No. 500); part 1 forbids LZW
            if (_part.Part() != 1 && ContentConformance.InlineFilterFault(image))
                Add(PdfARules.InlineFilters, Tr.T("Ein Inline-Bild ist mit einem Verfahren gepackt, das PDF/A dort nicht zulässt."));
            else if (filters is PdfName { Value: "LZW" or "LZWDecode" } || filters is PdfArray list && list.Any(f => f is PdfName { Value: "LZW" or "LZWDecode" }))
                Add(PdfARules.InlineFilters, Tr.T("Ein Datenstrom ist mit LZW gepackt."));
            if (image.Get(PdfName.Get("Intent")) is PdfName intent && !RenderingIntent(intent.Value))
                Add(PdfARules.RenderingIntent, Tr.T("Eine unbekannte Farbwiedergabe (Rendering Intent) wird benutzt."));
            if (Either(image, "IM", "ImageMask") is PdfBoolean { Value: true }) return;
            var space = Either(image, "CS", "ColorSpace");
            space = space switch
            {
                PdfName { Value: "RGB" } => PdfName.Get("DeviceRGB"),
                PdfName { Value: "CMYK" } => PdfName.Get("DeviceCMYK"),
                PdfName { Value: "G" } => PdfName.Get("DeviceGray"),
                _ => space,
            };
            ColorSpace(space, resources, 0);
        }

        /// <summary>An entry of an inline image by its short or long key.</summary>
        private static PdfObject Either(PdfDictionary image, string shortKey, string longKey) =>
            image.Get(PdfName.Get(shortKey)) is not PdfNull and var value ? value : image.Get(PdfName.Get(longKey));

        private void Pattern(PdfObject pattern, PdfDictionary? resources, int depth)
        {
            switch (pattern)
            {
                case PdfStream tiling:
                    Form(tiling, resources, depth + 1);
                    break;
                case PdfDictionary shading when shading.Get(PdfName.Get("Shading")) is { } sh:
                    // a shading pattern lives in its own space: strict readers apply no default colour space of the page to it
                    var dict = sh as PdfDictionary ?? (sh as PdfStream)?.Dictionary;
                    if (dict is not null) ColorSpace(dict.Get(PdfName.Get("ColorSpace")), null, 0);
                    break;
            }
        }

        /// <summary>The same profile: the same object, or the same profile by its ID — a copy with its ID written and one without are the same
        /// (ISO 19005-4, 6.2.4.2; docs/FEHLERGEDAECHTNIS.md, No. 517).</summary>
        private bool SameProfile(PdfStream profile, byte[] data, PdfStream? other) =>
            other is not null && (ReferenceEquals(profile, other) || profile.Id is { } a && other.Id is { } b && a.Number == b.Number ||
                                  Graphics.Icc.IccProfile.SameProfile(data, StreamData(other)));

        /// <summary>A colour space in use: device spaces must be covered by the output intent or a default space (6.2.4.3).</summary>
        private void ColorSpace(PdfObject? space, PdfDictionary? resources, int depth)
        {
            if (space is null || depth > 8) return;
            switch (space)
            {
                case PdfName name when name.Value is "DeviceRGB" or "DeviceCMYK" or "DeviceGray":
                    Device(name.Value, resources);
                    break;
                // the abbreviations the display reads as device spaces (the writer writes them in full)
                case PdfName { Value: "G" or "RGB" or "CMYK" } abbreviated:
                    Device(abbreviated.Value switch { "G" => "DeviceGray", "RGB" => "DeviceRGB", _ => "DeviceCMYK" }, resources);
                    break;
                case PdfName { Value: "Pattern" }:
                    break;
                case PdfName name:
                    // a resource may name a device space as well (the depth ends a resource naming itself)
                    if (Resource(resources, "ColorSpace", name) is { } named) ColorSpace(named, resources, depth + 1);
                    break;
                case PdfArray { Count: > 0 } array when array.Get(0) is PdfName family:
                    switch (family.Value)
                    {
                        case "Indexed" or "I":
                            ColorSpace(array.Get(1), resources, depth + 1);
                            break;
                        case "Separation":
                            if (array.Get(1) is PdfName colorant) Separation(colorant, array.Get(2), array.Get(3));
                            ColorSpace(array.Get(2), resources, depth + 1);
                            break;
                        case "DeviceN":
                            if (array.Get(1) is PdfArray names && names.Count > _part.Limits().Colourants)
                                Add(PdfARules.DeviceNColourants, Tr.T("Ein Farbraum hat mehr als {0} Farbauszüge.", _part.Limits().Colourants));
                            Colorants(array, resources, depth);
                            ColorSpace(array.Get(2), resources, depth + 1);
                            break;
                        case "Pattern" when array.Count > 1:
                            ColorSpace(array.Get(1), resources, depth + 1);
                            break;
                        case "ICCBased" when array.Get(1) is PdfStream profile:
                            var components = profile.Dictionary.GetInteger(PdfName.Get("N"));
                            if (components is not (1 or 3 or 4)) Add(PdfARules.IccProfileValid, Tr.T("Ein ICC-Farbraum hat eine ungültige Zahl von Farben."));
                            var profileData = StreamData(profile);
                            // PDF/A-1 (6.2.3.2): /N the number of colours of the profile
                            if (_part.Part() == 1 && components != IccComponents(profileData))
                                Add(PdfARules.IccProfileValid, Tr.T("Die Zahl der Farben (/N) eines ICC-Profils passt nicht zu seinem Farbraum."));
                            if (!IccProfileHeaderValid(profileData, _part.Limits().IccMajor))
                                Add(PdfARules.IccProfileValid, Tr.T("Das Profil eines ICC-Farbraums ist kein gültiges Eingabe-, Bildschirm-, Ausgabe- oder Farbraumprofil (Version unter 5)."));
                            // description, copyright and media white point are required (ICC.1, 8.2; docs/FEHLERGEDAECHTNIS.md, No. 1133)
                            else if (Graphics.Icc.CalibratedProfile.MissingRequiredTags(profileData).Length > 0)
                                Add(PdfARules.IccProfileValid, Tr.T("Dem Profil eines ICC-Farbraums fehlt eine Pflichtangabe (Beschreibung, Urheberrecht oder Medienweiß)."));
                            // part 4: not the output intent's CMYK profile, nor the blending space's (6.2.4.2)
                            // (on a page with an output intent of its own that one is the current one, PDF 2.0; No. 520)
                            if (components == 4 && SameProfile(profile, profileData, _pageIntentProfile ?? _intentProfile))
                                Add(PdfARules.IccNotIntent, Tr.T("Ein ICC-Farbraum nutzt das CMYK-Profil der Ausgabebedingung oder des Mischfarbraums."));
                            // the blending space's profile: the writer leaves out the group's space (not that of a soft mask, whose backdrop
                            // colour is given in it)
                            else if (components == 4 && SameProfile(profile, profileData, _blendProfile))
                            {
                                if (DeviceGroups is not null && !_inMask && _blendGroup is not null) DeviceGroups.Add(_blendGroup);
                                else Add(PdfARules.IccNotIntent, Tr.T("Ein ICC-Farbraum nutzt das CMYK-Profil der Ausgabebedingung oder des Mischfarbraums."));
                            }
                            UsesIccCmyk |= components == 4;
                            break;
                    }
                    break;
            }
        }

        /// <summary>Every spot colour of a DeviceN space (not None, not a process colour) is described in its /Colorants (6.2.4.4); the
        /// Separation spaces there count as all others.</summary>
        private void Colorants(PdfArray space, PdfDictionary? resources, int depth)
        {
            if (space.Get(1) is not PdfArray names) return;
            var attributes = space.Get(4) as PdfDictionary;
            var colorants = attributes?.GetDictionary(PdfName.Get("Colorants"));
            var process = attributes?.GetDictionary(PdfName.Get("Process"))?.Get(PdfName.Get("Components")) as PdfArray;
            for (var i = 0; i < names.Count && i < 64; i++)
            {
                if (names.Get(i) is not PdfName name) continue;
                if (!Utf8(name.Value)) Add(PdfARules.NamesUtf8, Tr.T("Der Name einer Sonderfarbe ist kein gültiges UTF-8."));
                // the process colours and None are no spot colours
                if (name.Value is "None" or "Cyan" or "Magenta" or "Yellow" or "Black" || process?.Any(p => p is PdfName n && n == name) == true) continue;
                if (colorants?.HasValue(name) != true) Add(PdfARules.Colorants, Tr.T("Ein DeviceN-Farbraum beschreibt nicht alle seine Sonderfarben (/Colorants)."));
            }
            if (colorants is not null)
                foreach (var (key, _) in colorants.Entries.Take(64))
                    ColorSpace(colorants.Get(key), resources, depth + 1);
            // the process space of an NChannel space is a colour space in use as well
            if (attributes?.GetDictionary(PdfName.Get("Process"))?.Get(PdfName.Get("ColorSpace")) is not PdfNull and var processSpace)
                ColorSpace(processSpace, resources, depth + 1);
        }

        /// <summary>All Separation spaces of one colourant have the same alternate space and tint transform (6.2.4.4).</summary>
        private void Separation(PdfName colorant, PdfObject alternate, PdfObject tint)
        {
            if (!Utf8(colorant.Value)) Add(PdfARules.NamesUtf8, Tr.T("Der Name einer Sonderfarbe ist kein gültiges UTF-8."));
            if (colorant.Value is "All" or "None") return;
            if (!_separations.TryGetValue(colorant.Value, out var first)) _separations[colorant.Value] = (alternate, tint);
            else if (!Same(first.Alternate, alternate) || !Same(first.Tint, tint))
                Add(PdfARules.SeparationsAlike, Tr.T("Eine Sonderfarbe ist an verschiedenen Stellen verschieden beschrieben ({0}).", colorant.Value));
        }

        /// <summary>The same object, or equal in what it says.</summary>
        private bool Same(PdfObject a, PdfObject b)
        {
            if (ReferenceEquals(a, b)) return true;
            try
            {
                return (a, b) switch
                {
                    (PdfStream x, PdfStream y) => PdfSerializer.ToBytes(x.Dictionary).AsSpan().SequenceEqual(PdfSerializer.ToBytes(y.Dictionary)) &&
                                                  StreamData(x).AsSpan().SequenceEqual(StreamData(y)),
                    (PdfStream, _) or (_, PdfStream) => false,
                    _ => PdfSerializer.ToBytes(a).AsSpan().SequenceEqual(PdfSerializer.ToBytes(b)),
                };
            }
            catch (PdfException)
            {
                return false;
            }
        }


        /// <summary>
        /// Whether the colour spaces of a resource dictionary set a device-independent default for a device space (ISO 19005-2, 6.2.4.3;
        /// ISO 19005-1, 6.2.3.3): ICCBased, CalRGB, CalGray or Lab. A default that is itself a device space (<c>/DefaultRGB /DeviceRGB</c>)
        /// changes nothing — before it counted as set (docs/FEHLERGEDAECHTNIS.md, No. 509).
        /// </summary>
        internal static bool IndependentDefault(PdfDictionary? spaces, string key) => Independent(spaces?.Get(PdfName.Get(key)));

        private void Device(string space, PdfDictionary? resources)
        {
            UsesRgb |= space == "DeviceRGB";
            UsesCmyk |= space == "DeviceCMYK";
            var spaces = _withoutOwnResources ? null : resources?.GetDictionary(PdfName.Get("ColorSpace"));
            var intent = _pageIntent ?? _intentSpace;
            if (Debug && space is "DeviceRGB" or "DeviceCMYK" && intent != (space == "DeviceRGB" ? IccProfile.RgbSpace : IccProfile.CmykSpace)
                && !IndependentDefault(spaces, space == "DeviceRGB" ? "DefaultRGB" : "DefaultCMYK"))
                // where the device space is used without a default space: the way the check went (page, form, annotation, pattern …)
                Console.Error.WriteLine($"{space} ohne Standard-Farbraum, Seite {_page}: " + string.Join(" < ", Environment.StackTrace.Split('\n')
                    .Where(l => l.Contains("PdfACheck", StringComparison.Ordinal)).Select(l => l.Trim()).Take(8)));
            switch (space)
            {
                case "DeviceRGB" when intent != IccProfile.RgbSpace && _pageBlend != IccProfile.RgbSpace && !IndependentDefault(spaces, "DefaultRGB"):
                    Add(PdfARules.DeviceRgb, Tr.T("RGB-Geräte-Farben werden ohne passende Ausgabebedingung oder Standard-Farbraum benutzt."));
                    break;
                case "DeviceCMYK" when intent != IccProfile.CmykSpace && _pageBlend != IccProfile.CmykSpace && !IndependentDefault(spaces, "DefaultCMYK"):
                    Add(PdfARules.DeviceCmyk, Tr.T("CMYK-Geräte-Farben werden ohne passende Ausgabebedingung oder Standard-Farbraum benutzt."));
                    break;
                case "DeviceGray" when intent is null && !IndependentDefault(spaces, "DefaultGray"):
                    Add(PdfARules.DeviceGray, Tr.T("Graustufen werden ohne Ausgabebedingung oder Standard-Farbraum benutzt."));
                    break;
            }
        }
    }
}
