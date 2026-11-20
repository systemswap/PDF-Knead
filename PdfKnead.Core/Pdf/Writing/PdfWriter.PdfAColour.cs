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
        /// An ICC-based colour space as the display reads it (ISO 19005-2, 6.2.4.2): a profile the display cannot read (or whose number of
        /// colours differs) becomes what the display draws instead — the /Alternate, or the device space of as many colours. In PDF/A-4 a CMYK
        /// profile equal to the output intent's becomes DeviceCMYK, which that intent defines (6.2.4.2-3). A profile of version 4 made of tone
        /// curves and a matrix becomes the same profile of version 2.1 where the part allows only version 2 (PDF/A-1, 6.2.3.2;
        /// <see cref="CalibratedProfile.AsVersion2"/>): the same colours. Another profile the display reads but PDF/A does not allow (a device class,
        /// colour space or version it does not list) stays: replacing it would change the colours; the check names it. Any other array stays as
        /// it is.
        /// </summary>
        private PdfObject IccSpace(PdfArray array, Source? source)
        {
            if (array.Count < 2 || array.Get(0) is not PdfName { Value: "ICCBased" } || Look(array[1], source) is not (PdfStream profile, var from)) return array;
            var n = (int)(profile.Dictionary.GetInteger(PdfName.N) ?? 0);
            byte[] data;
            try
            {
                data = (from ?? source)?.Doc.DecodeStream(profile).Data ?? [];
            }
            catch (PdfException)
            {
                data = [];
            }
            if (IccProfile.Parse(data) is not { } read || read.Channels != n)
            {
                if (profile.Dictionary.GetRaw(PdfName.Get("Alternate")) is { } alternate && PdfAResolve(alternate, from) is not (null or PdfNull) and var resolved)
                    return resolved is PdfArray alternateArray ? PdfAFix(Portable(alternateArray, from, source), source, 0) : Portable(resolved, from, source);
                return PdfName.Get(n switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" });
            }
            if (n == 4 && _pdfA!.Part.Part() == 4 && _pdfA.IntentProfile is { } intent &&
                (from is not null && ReferenceEquals(from, intent.Source) && array[1] is PdfReference r && r.Id.Number == intent.Number ||
                 IccProfile.SameProfile(data, intent.Data)))   // (the same profile by its ID as well, No. 517)
                return PdfName.Get("DeviceCMYK");
            if (data[8] > _pdfA!.Part.Limits().IccMajor)
            {
                if (!_olderProfiles.TryGetValue(profile, out var older))
                    _olderProfiles[profile] = older = CalibratedProfile.AsVersion2(data) is { } version2 ? Fresh(IccStream(version2, n)) : null;
                if (older is not null) return new PdfArray([IccBased, older]);
            }
            return array;
        }

        /// <summary>Profiles of a version the part does not allow, written as version 2 once each (null: they cannot be).</summary>
        private readonly Dictionary<PdfStream, PdfObject?> _olderProfiles = new(ReferenceEqualityComparer.Instance);

        /// <summary>The first description of each spot colour by name (alternate space and tint transform), and the names given to others.</summary>
        private readonly Dictionary<string, (string Fingerprint, PdfArray Space, Source? Source)> _separations = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Name, string Fingerprint), PdfName> _renamedSeparations = [];

        /// <summary>
        /// Spot colours of the same name alike (ISO 19005-2, 6.2.4.4): the first description of a name stands; a Separation space that names it
        /// with another alternate space or tint transform gets a name of its own (the same for every place with that description) — the colours
        /// drawn stay, only the plate they would separate to differs. DeviceN spaces rename their colourants with the descriptions in their
        /// /Colorants.
        /// </summary>
        private PdfArray AlikeColorants(PdfArray array, Source? source)
        {
            if (array.Count < 4 || array.Get(0) is not PdfName family) return array;
            if (family.Value == "Separation" && array[1] is PdfName name && name.Value is not ("All" or "None"))
            {
                var fingerprint = Fingerprint(array[2], source, 0) + "|" + Fingerprint(array[3], source, 0);
                var given = SpotName(name, fingerprint, array, source);
                if (given == name) return array;
                var copy = new PdfArray { Resolver = array.Resolver };
                for (var i = 0; i < array.Count; i++) copy.Add(i == 1 ? given : array[i]);
                return copy;
            }
            if (family.Value != "DeviceN" || array.Count < 5 || PdfAResolve(array[1], source) is not PdfArray names ||
                PdfAResolve(array[4], source) is not PdfDictionary attributes || attributes.Get(PdfName.Get("Colorants")) is not PdfDictionary colorants)
                return array;
            var renames = new Dictionary<PdfName, PdfName>();
            var renamedSpaces = new Dictionary<PdfName, PdfArray>();
            foreach (var (key, _) in colorants.Entries)
                if (Look(colorants.GetRaw(key)!, source) is (PdfArray { Count: >= 4 } separation, var from) && separation.Get(0) is PdfName { Value: "Separation" })
                {
                    var given = SpotName(key, Fingerprint(separation[2], from, 0) + "|" + Fingerprint(separation[3], from, 0), separation, from);
                    if (given == key) continue;
                    renames[key] = given;
                    // the Separation space in /Colorants carries the name as well: renamed with it, or it still names the first description
                    // (docs/FEHLERGEDAECHTNIS.md, No. 504)
                    var renamedSpace = new PdfArray { Resolver = separation.Resolver };
                    for (var i = 0; i < separation.Count; i++) renamedSpace.Add(i == 1 ? given : separation[i]);
                    renamedSpaces[key] = renamedSpace;
                }
            if (renames.Count == 0) return array;
            var newNames = new PdfArray([.. Enumerable.Range(0, names.Count).Select(i => names[i] is PdfName n && renames.TryGetValue(n, out var to) ? to : names[i])]);
            var newColorants = new PdfDictionary { Resolver = colorants.Resolver };
            foreach (var (key, value) in colorants.Entries)
                newColorants.Set(renames.GetValueOrDefault(key, key), renamedSpaces.TryGetValue(key, out var renamedSeparation) ? renamedSeparation : value);
            var newAttributes = CloneWithResolver(attributes);
            newAttributes.Set(PdfName.Get("Colorants"), newColorants);
            var result = new PdfArray { Resolver = array.Resolver };
            for (var i = 0; i < array.Count; i++) result.Add(i switch { 1 => newNames, 4 => newAttributes, _ => array[i] });
            return result;
        }

        /// <summary>The name a spot colour with this description is written under (see <see cref="AlikeColorants"/>).</summary>
        private PdfName SpotName(PdfName name, string fingerprint, PdfArray space, Source? source)
        {
            if (!_separations.TryGetValue(name.Value, out var first))
            {
                _separations[name.Value] = (fingerprint, space, source);
                return name;
            }
            if (first.Fingerprint == fingerprint) return name;
            if (_renamedSeparations.TryGetValue((name.Value, fingerprint), out var known)) return known;
            var k = 2;
            PdfName given;
            do given = PdfName.Get($"{name.Value} {k++}");
            while (_separations.ContainsKey(given.Value));
            _separations[given.Value] = (fingerprint, space, source);
            return _renamedSeparations[(name.Value, fingerprint)] = given;
        }

        /// <summary>
        /// An object compared as strict readers compare spot colours (ISO 19005-2, 6.2.4.4): by its content — whether direct or indirect and how it
        /// is packed do not count; a stream by its dictionary (without length and filters) and its decoded data.
        /// </summary>
        private string Fingerprint(PdfObject? raw, Source? source, int depth)
        {
            if (raw is null || depth > 16) return "?";
            var (obj, from) = Look(raw, source);
            switch (obj)
            {
                case PdfName n:
                    return "/" + n.Value;
                case PdfInteger or PdfReal:
                    return PdfSerializer.FormatReal(obj.AsNumber() ?? 0);
                case PdfString s:
                    return "<" + Convert.ToHexString(s.Bytes) + ">";
                case PdfBoolean b:
                    return b.Value ? "true" : "false";
                case PdfArray a:
                    return "[" + string.Join(" ", Enumerable.Range(0, Math.Min(a.Count, 1000)).Select(i => Fingerprint(a[i], from, depth + 1))) + "]";
                case PdfStream st:
                    byte[] data;
                    try
                    {
                        data = (from ?? source)?.Doc.DecodeStream(st).Data ?? [];
                    }
                    catch (PdfException)
                    {
                        data = [];
                    }
                    var dict = string.Join(" ", st.Dictionary.Entries.Where(e => e.Key.Value is not ("Length" or "Filter" or "DecodeParms"))
                        .OrderBy(e => e.Key.Value, StringComparer.Ordinal).Select(e => "/" + e.Key.Value + " " + Fingerprint(e.Value, from, depth + 1)));
                    return "S<<" + dict + ">>" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
                case PdfDictionary d:
                    return "<<" + string.Join(" ", d.Entries.OrderBy(e => e.Key.Value, StringComparer.Ordinal)
                        .Select(e => "/" + e.Key.Value + " " + Fingerprint(e.Value, from, depth + 1))) + ">>";
                default:
                    return "null";
            }
        }

        /// <summary>
        /// A colour space array whose base is a device space the output intent does not cover (the alternate of DeviceN and Separation, the base
        /// of Indexed and of an uncoloured pattern): the base becomes the ICC-based space a default colour space would give it — the same colours,
        /// and valid where no default space applies (a shading pattern has none of the page, 6.2.4.3).
        /// </summary>
        private PdfArray DeviceBase(PdfArray array)
        {
            var state = _pdfA!;
            if (array.Count < 2 || array.Get(0) is not PdfName family) return array;
            var slot = family.Value switch
            {
                "DeviceN" or "Separation" => 2,
                "Indexed" or "I" or "Pattern" => 1,
                _ => -1,
            };
            if (slot < 0 || slot >= array.Count || array[slot] is not PdfName { Value: "DeviceCMYK" or "DeviceRGB" } device) return array;
            var icc = device.Value == "DeviceCMYK" ? state.Cmyk : state.Rgb;
            if (icc is null) return array;
            var copy = new PdfArray { Resolver = array.Resolver };
            for (var i = 0; i < array.Count; i++) copy.Add(i == slot ? icc : array[i]);
            return copy;
        }

        /// <summary>A colourant description the writer sampled (<see cref="CompleteColorants"/>): as written, with what it was made from.</summary>
        /// <param name="Space">The Separation space as written (its tint transform an object of the new file).</param>
        /// <param name="Alternate">The alternate space it stands for (<see cref="Fingerprint"/> of the space as read).</param>
        /// <param name="Values">The tint transform at the sample points.</param>
        private sealed record SampledColorant(PdfArray Space, string Alternate, double[,] Values);

        private readonly Dictionary<string, SampledColorant> _sampledColorants = new(StringComparer.Ordinal);

        private const int ColorantSamples = 33;

        /// <summary>
        /// A DeviceN space whose spot colours lack /Colorants (6.2.4.4): each gets a Separation space — its alternate space, and as tint
        /// transform the DeviceN transform with only this colourant set, sampled (33 points, 16 bit). Painting goes through the DeviceN space as
        /// before; the Separation spaces describe the colourants for output that separates them. Spot colours of a name must be alike in the whole
        /// file: a colourant described elsewhere already keeps that description where it is the same at every sample point (same alternate space,
        /// values within 1/1000); otherwise it is renamed in this space („X 2“, the same new name wherever it is described alike), as
        /// <see cref="AlikeColorants"/> does for Separation spaces — the colours drawn stay (docs/FEHLERGEDAECHTNIS.md, No. 337).
        /// </summary>
        private PdfArray CompleteColorants(PdfArray array, Source? source)
        {
            if (array.Count < 4 || array.Get(0) is not PdfName { Value: "DeviceN" } || PdfAResolve(array[1], source) is not PdfArray names)
                return array;
            var attributes = array.Count > 4 ? PdfAResolve(array[4], source) as PdfDictionary : null;
            var colorants = attributes?.Get(PdfName.Get("Colorants")) as PdfDictionary;
            var process = attributes?.GetDictionary(PdfName.Get("Process"))?.Get(PdfName.Get("Components")) as PdfArray;
            var missing = new List<(PdfName Name, int Index)>();
            for (var i = 0; i < names.Count && i < 32; i++)
                if (PdfAResolve(names[i], source) is PdfName name && name.Value is not ("None" or "Cyan" or "Magenta" or "Yellow" or "Black") &&
                    process?.Any(p => p is PdfName n && n == name) != true && colorants?.ContainsKey(name) != true && missing.All(m => m.Name != name))
                    missing.Add((name, i));
            // (the space may lie in a dictionary the writer assembled: its references are those of the new file)
            if (missing.Count == 0 || Look(array[3], source) is not ({ } tintObject, var tintFrom) ||
                PdfFunction.Create(tintObject, (tintFrom ?? source)?.Doc ?? _main) is not { } tint || tint.Inputs != names.Count || tint.Outputs is < 1 or > 8)
                return array;
            var alternate = array[2] is PdfName { Value: "DeviceCMYK" or "DeviceRGB" } device && (device.Value == "DeviceCMYK" ? _pdfA!.Cmyk : _pdfA!.Rgb) is { } icc
                ? (PdfObject)icc
                : Portable(array[2], source, source);
            var alternatePrint = Fingerprint(array[2], source, 0);
            var complete = colorants is null ? new PdfDictionary() : CloneWithResolver(colorants);
            var renames = new Dictionary<PdfName, PdfName>();
            foreach (var (name, index) in missing)
            {
                var values = new double[ColorantSamples, tint.Outputs];
                var input = new double[names.Count];
                var output = new double[tint.Outputs];
                for (var k = 0; k < ColorantSamples; k++)
                {
                    input[index] = k / (double)(ColorantSamples - 1);
                    tint.Evaluate(input, output);
                    for (var o = 0; o < output.Length; o++) values[k, o] = output[o];
                }
                // a spot colour described elsewhere already, alike: that description
                if (_separations.TryGetValue(name.Value, out var known) && Alike(name.Value, known, alternatePrint, values))
                {
                    complete.Set(name, _sampledColorants.TryGetValue(name.Value, out var sampled) ? sampled.Space : Portable(known.Space, known.Source, source));
                    continue;
                }
                // described differently elsewhere: a name of its own, the same for every place with this description
                var given = name;
                if (known.Space is not null)
                {
                    var key = (name.Value, "sampled|" + alternatePrint + "|" + SampleHash(values));
                    if (_renamedSeparations.TryGetValue(key, out var earlier) && _sampledColorants.TryGetValue(earlier.Value, out var again))
                    {
                        renames[name] = earlier;
                        complete.Set(earlier, again.Space);
                        continue;
                    }
                    var k = 2;
                    do given = PdfName.Get($"{name.Value} {k++}");
                    while (_separations.ContainsKey(given.Value));
                    _renamedSeparations[key] = given;
                    renames[name] = given;
                }
                var range = new PdfArray();
                var bytes = new byte[ColorantSamples * tint.Outputs * 2];
                for (var o = 0; o < tint.Outputs; o++)
                {
                    double low = 0, high = 1;
                    for (var k = 0; k < ColorantSamples; k++)
                    {
                        low = Math.Min(low, values[k, o]);
                        high = Math.Max(high, values[k, o]);
                    }
                    range.Add(new PdfReal(low));
                    range.Add(new PdfReal(high));
                    for (var k = 0; k < ColorantSamples; k++)
                    {
                        var v = (int)Math.Round((values[k, o] - low) / (high - low) * 65535);
                        bytes[(k * tint.Outputs + o) * 2] = (byte)(v >> 8);
                        bytes[(k * tint.Outputs + o) * 2 + 1] = (byte)v;
                    }
                }
                var function = new PdfDictionary();
                function.Set(PdfName.Get("FunctionType"), PdfInteger.Of(0));
                function.Set(PdfName.Get("Domain"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(1)]));
                function.Set(PdfName.Get("Range"), range);
                function.Set(PdfName.Get("Size"), new PdfArray([PdfInteger.Of(ColorantSamples)]));
                function.Set(PdfName.Get("BitsPerSample"), PdfInteger.Of(16));
                var separation = new PdfArray([PdfName.Get("Separation"), given, alternate, Fresh(new PdfStream(function, bytes))]);
                complete.Set(given, separation);
                _separations[given.Value] = ("sampled|" + alternatePrint + "|" + SampleHash(values), separation, null);
                _sampledColorants[given.Value] = new SampledColorant(separation, alternatePrint, values);
            }
            var newAttributes = attributes is null ? new PdfDictionary() : CloneWithResolver(attributes);
            newAttributes.Set(PdfName.Get("Colorants"), complete);
            var result = new PdfArray { Resolver = array.Resolver };
            result.Add(array[0]);
            result.Add(renames.Count == 0 ? array[1]
                : new PdfArray([.. Enumerable.Range(0, names.Count).Select(i => names[i] is PdfName n && renames.TryGetValue(n, out var to) ? to : names[i])]));
            result.Add(array[2]);
            result.Add(array[3]);
            result.Add(newAttributes);
            return result;
        }

        /// <summary>
        /// Whether a spot colour already described is the same as one sampled from a DeviceN space: the same alternate space, and its tint
        /// transform within 1/1000 at every sample point.
        /// </summary>
        private bool Alike(string name, (string Fingerprint, PdfArray Space, Source? Source) known, string alternate, double[,] values)
        {
            double[,]? theirs;
            if (_sampledColorants.TryGetValue(name, out var sampled))
            {
                if (sampled.Alternate != alternate) return false;
                theirs = sampled.Values;
            }
            else
            {
                if (known.Space.Count < 4 || Fingerprint(known.Space[2], known.Source, 0) != alternate ||
                    Look(known.Space[3], known.Source) is not ({ } obj, var from) ||
                    PdfFunction.Create(obj, (from ?? known.Source)?.Doc ?? _main) is not { Inputs: 1 } function || function.Outputs != values.GetLength(1))
                    return false;
                theirs = new double[ColorantSamples, function.Outputs];
                var input = new double[1];
                var output = new double[function.Outputs];
                for (var k = 0; k < ColorantSamples; k++)
                {
                    input[0] = k / (double)(ColorantSamples - 1);
                    function.Evaluate(input, output);
                    for (var o = 0; o < output.Length; o++) theirs[k, o] = output[o];
                }
            }
            if (theirs.GetLength(1) != values.GetLength(1)) return false;
            for (var k = 0; k < ColorantSamples; k++)
                for (var o = 0; o < values.GetLength(1); o++)
                    if (Math.Abs(theirs[k, o] - values[k, o]) > 0.001) return false;
            return true;
        }

        private static string SampleHash(double[,] values)
        {
            var text = new System.Text.StringBuilder();
            foreach (var v in values) text.Append(Math.Round(v, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(text.ToString())))[..16];
        }

        private static readonly HashSet<string> FontKinds = new(["Type0", "Type1", "MMType1", "TrueType", "Type3", "CIDFontType0", "CIDFontType2"], StringComparer.Ordinal);

        /// <summary>
        /// A name of a colourant or a structure type that is no UTF-8 (6.1.8), its bytes read as Latin-1 and written as UTF-8: the same
        /// characters, different names stay different (spot colours must not merge), and every place gets the same new name.
        /// </summary>
        private static PdfName Utf8Transcoded(PdfName name) =>
            PdfACheck.Utf8(name.Value) ? name : PdfName.Get(System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes(name.Value)));

        /// <summary>The colourant names of a Separation or DeviceN space in UTF-8 (<see cref="Utf8Transcoded"/>).</summary>
        private PdfArray Utf8Colorants(PdfArray array, Source? source)
        {
            if (array.Count < 2 || array.Get(0) is not PdfName { Value: "Separation" or "DeviceN" } family) return array;
            PdfObject? second = null;
            if (family.Value == "Separation" && array[1] is PdfName colorant && Utf8Transcoded(colorant) is var renamed && renamed != colorant)
                second = renamed;
            if (family.Value == "DeviceN" && PdfAResolve(array[1], source) is PdfArray names &&
                Enumerable.Range(0, names.Count).Any(i => names[i] is PdfName n && !PdfACheck.Utf8(n.Value)))
                second = new PdfArray([.. Enumerable.Range(0, names.Count).Select(i => names[i] is PdfName n ? Utf8Transcoded(n) : names[i])]);
            // DeviceN: the keys of /Colorants and the names of their Separation spaces as well (docs/FEHLERGEDAECHTNIS.md, No. 504)
            PdfObject? attributesCopy = null;
            if (family.Value == "DeviceN" && array.Count >= 5 && PdfAResolve(array[4], source) is PdfDictionary attributes &&
                attributes.Get(PdfName.Get("Colorants")) is PdfDictionary colorants && colorants.Entries.Any(e => !PdfACheck.Utf8(e.Key.Value)))
            {
                var newColorants = new PdfDictionary { Resolver = colorants.Resolver };
                foreach (var (key, value) in colorants.Entries)
                {
                    var renamedKey = Utf8Transcoded(key);
                    var renamedValue = value;
                    if (renamedKey != key && Look(colorants.GetRaw(key)!, source) is (PdfArray { Count: >= 2 } separation, _) && separation[1] is PdfName spot)
                    {
                        var space = new PdfArray { Resolver = separation.Resolver };
                        for (var i = 0; i < separation.Count; i++) space.Add(i == 1 ? Utf8Transcoded(spot) : separation[i]);
                        renamedValue = space;
                    }
                    newColorants.Set(renamedKey, renamedValue);
                }
                var newAttributes = CloneWithResolver(attributes);
                newAttributes.Set(PdfName.Get("Colorants"), newColorants);
                attributesCopy = newAttributes;
            }
            if (second is null && attributesCopy is null) return array;
            var copy = new PdfArray { Resolver = array.Resolver };
            for (var i = 0; i < array.Count; i++) copy.Add(i == 1 && second is not null ? second : i == 4 && attributesCopy is not null ? attributesCopy : array[i]);
            return copy;
        }

        /// <summary>Resources of something that draws: there must be some (an appearance has no others to use), with the default colour spaces.</summary>
        private void WithDefaultSpaces(PdfDictionary owner, Func<PdfDictionary> writable, Source? source)
        {
            var raw = owner.GetRaw(PdfName.Resources);
            switch (raw)
            {
                case null:
                    var resources = new PdfDictionary();
                    AddDefaultSpaces(resources, () => resources, source);
                    if (resources.Count > 0) writable().Set(PdfName.Resources, resources);
                    break;
                case PdfDictionary direct:
                    PdfDictionary? changed = null;
                    AddDefaultSpaces(direct, () => changed ??= CloneWithResolver(direct), source);
                    if (changed is not null) writable().Set(PdfName.Resources, changed);
                    break;
                default:
                    // a resource dictionary of its own gets them when it is written (marked here: it may hold keys of no resource category,
                    // docs/FEHLERGEDAECHTNIS.md, No. 337); an empty one is replaced here
                    if (NumberOf(raw, source) is > 0 and var resourcesNumber) _pdfA!.ResourceDictionaries.Add(resourcesNumber);
                    if (PdfAResolve(raw, source) is PdfDictionary { Count: 0 })
                    {
                        var empty = new PdfDictionary();
                        AddDefaultSpaces(empty, () => empty, source);
                        if (empty.Count > 0) writable().Set(PdfName.Resources, empty);
                    }
                    break;
            }
        }

        /// <summary>/DefaultCMYK and /DefaultRGB into the colour spaces of a resource dictionary (an indirect one is marked for when it is written).</summary>
        private void AddDefaultSpaces(PdfDictionary resources, Func<PdfDictionary> writable, Source? source)
        {
            var state = _pdfA!;
            if (state.Cmyk is null && state.Rgb is null) return;
            switch (resources.GetRaw(ColorSpaceKey))
            {
                case null:
                    var spaces = new PdfDictionary();
                    AddDefaults(spaces, () => spaces);
                    writable().Set(ColorSpaceKey, spaces);
                    break;
                case PdfDictionary direct:
                    PdfDictionary? changed = null;
                    AddDefaults(direct, () => changed ??= CloneWithResolver(direct));
                    if (changed is not null) writable().Set(ColorSpaceKey, changed);
                    break;
                case var reference:
                    if (NumberOf(reference, source) is > 0 and var number) state.ColorSpaces.Add(number);
                    break;
            }
        }

        private void AddDefaults(PdfDictionary spaces, Func<PdfDictionary> writable)
        {
            var state = _pdfA!;
            // a default that is itself a device space (/DefaultRGB /DeviceRGB) is replaced: it does not count (No. 509)
            if (state.Cmyk is { } cmyk && !PdfACheck.Independent(spaces.Get(DefaultCmykKey))) writable().Set(DefaultCmykKey, cmyk);
            if (state.Rgb is { } rgb && !PdfACheck.Independent(spaces.Get(DefaultRgbKey))) writable().Set(DefaultRgbKey, rgb);
        }
    }
}
