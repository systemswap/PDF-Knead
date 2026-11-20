namespace PdfKnead.Core.Pdf.Writing;

/// <summary>A value type of an XMP property (XMP Specification, September 2005, Part 2, 4.2 and the schemas of 4.4–4.11).</summary>
internal abstract record XmpType;

/// <summary>A simple value of a basic type, checked by its form (<see cref="XmpSchemas.Lexical"/>).</summary>
/// <param name="Name">Text, Integer, Real, Rational, Boolean, Date, GPSCoordinate, or a kind of text (URI, URL, AgentName …).</param>
internal sealed record XmpSimple(string Name) : XmpType;

/// <summary>An array: rdf:Bag, rdf:Seq or rdf:Alt of items of one type.</summary>
internal sealed record XmpArray(string Container, XmpType Item) : XmpType;

/// <summary>An alternative of texts in languages (rdf:Alt, every item with xml:lang).</summary>
internal sealed record XmpLangAlt : XmpType;

/// <summary>A structure: its fields in one namespace, each with its type.</summary>
/// <param name="Name">The name of the type (for an extension schema's reference to it).</param>
/// <param name="Namespace">The namespace of its fields.</param>
/// <param name="Fields">The fields by name; a function, so that structures can contain each other.</param>
internal sealed record XmpStruct(string Name, string Namespace, Func<IReadOnlyDictionary<string, XmpType>> Fields) : XmpType;

/// <summary>
/// The schemas of the XMP Specification of September 2005 (Part 2, 4.4–4.11: Dublin Core, XMP Basic, Rights Management, Media
/// Management, Basic Job Ticket, Paged-Text, Dynamic Media, Adobe PDF, Photoshop, Camera Raw, EXIF and TIFF) — those ISO 19005-2, 6.6.2.3.1
/// names as predefined — with the types of their properties, and the PDF/A schemas of identification and extension (6.6.2.3.3, 6.6.4).
/// </summary>
internal static class XmpSchemas
{
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
    public const string PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    public const string Extension = "http://www.aiim.org/pdfa/ns/extension/";
    public const string Schema = "http://www.aiim.org/pdfa/ns/schema#";
    public const string Property = "http://www.aiim.org/pdfa/ns/property#";
    public const string Type = "http://www.aiim.org/pdfa/ns/type#";
    public const string Field = "http://www.aiim.org/pdfa/ns/field#";

    /// <summary>The qualifier namespace of xmp:Identifier (Part 2, 4.4.2: xmpidq:Scheme).</summary>
    public const string IdentifierQualifier = "http://ns.adobe.com/xmp/Identifier/qual/1.0/";

    private const string Dc = "http://purl.org/dc/elements/1.1/";
    private const string Xmp = "http://ns.adobe.com/xap/1.0/";
    private const string XmpRights = "http://ns.adobe.com/xap/1.0/rights/";
    private const string XmpMM = "http://ns.adobe.com/xap/1.0/mm/";
    private const string XmpBJ = "http://ns.adobe.com/xap/1.0/bj/";
    private const string XmpTPg = "http://ns.adobe.com/xap/1.0/t/pg/";
    private const string XmpDM = "http://ns.adobe.com/xmp/1.0/DynamicMedia/";
    private const string Pdf = "http://ns.adobe.com/pdf/1.3/";
    private const string Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    private const string Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private const string Tiff = "http://ns.adobe.com/tiff/1.0/";
    private const string Exif = "http://ns.adobe.com/exif/1.0/";
    private const string Aux = "http://ns.adobe.com/exif/1.0/aux/";

    // ------------------------------------------------------------ structures (Part 2, 4.3; the media and EXIF types of 4.9, 4.11)

    private static XmpStruct Struct(string name, string ns, string fields)
    {
        // read on first use: structures refer to each other
        var parsed = new Lazy<IReadOnlyDictionary<string, XmpType>>(() => fields.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Split(' ', 2)).ToDictionary(f => f[0], f => Parse(f[1])!, StringComparer.Ordinal));
        return new XmpStruct(name, ns, () => parsed.Value);
    }

    /// <summary>The structured types of the predefined schemas, by name.</summary>
    public static readonly IReadOnlyDictionary<string, XmpStruct> Structs = new[]
    {
        Struct("Dimensions", "http://ns.adobe.com/xap/1.0/sType/Dimensions#", "w Real; h Real; unit Text"),
        Struct("Font", "http://ns.adobe.com/xap/1.0/sType/Font#",
            "fontName Text; fontFamily Text; fontFace Text; fontType Text; versionString Text; composite Boolean; fontFileName Text; childFontFiles Seq Text"),
        Struct("Colorant", "http://ns.adobe.com/xap/1.0/g/",
            "swatchName Text; mode Text; type Text; cyan Real; magenta Real; yellow Real; black Real; red Integer; green Integer; blue Integer; L Real; A Integer; B Integer"),
        Struct("Thumbnail", "http://ns.adobe.com/xap/1.0/g/img/", "height Integer; width Integer; format Text; image Text"),
        Struct("ResourceEvent", "http://ns.adobe.com/xap/1.0/sType/ResourceEvent#", "action Text; instanceID URI; parameters Text; softwareAgent AgentName; when Date"),
        Struct("ResourceRef", "http://ns.adobe.com/xap/1.0/sType/ResourceRef#",
            "instanceID URI; documentID URI; versionID Text; renditionClass RenditionClass; renditionParams Text; manager AgentName; managerVariant Text; manageTo URI; manageUI URI"),
        Struct("Version", "http://ns.adobe.com/xap/1.0/sType/Version#", "comments Text; event ResourceEvent; modifyDate Date; modifier ProperName; version Text"),
        Struct("Job", "http://ns.adobe.com/xap/1.0/sType/Job#", "name Text; id Text; url URL"),
        Struct("ProjectLink", XmpDM, "type Text; path URI"),
        Struct("Time", XmpDM, "value Integer; scale Rational"),
        Struct("Timecode", XmpDM, "timeValue Text; timeFormat Text"),
        Struct("Marker", XmpDM, "startTime Time; duration Time; comment Text; name Text; location URI; target Text; type Text"),
        Struct("Media", XmpDM, "path URI; track Text; startTime Time; duration Time; managed Boolean; webStatement URI"),
        Struct("BeatSpliceStretch", XmpDM, "useFileBeatsMarker Boolean; riseInDecibel Real; riseInTimeDuration Time"),
        Struct("ResampleStretch", XmpDM, "quality Text"),
        Struct("TimeScaleStretch", XmpDM, "quality Text; frameSize Real; frameOverlappingPercentage Real"),
        Struct("Flash", Exif, "Fired Boolean; Return Integer; Mode Integer; Function Boolean; RedEyeMode Boolean"),
        Struct("OECF", Exif, "Columns Integer; Rows Integer; Names Seq Text; Values Seq Rational"),
        Struct("CFAPattern", Exif, "Columns Integer; Rows Integer; Values Seq Integer"),
        Struct("DeviceSettings", Exif, "Columns Integer; Rows Integer; Settings Seq Text"),
    }.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>The basic types of XMP (Part 2, 4.2) an extension schema may name, besides arrays of them and the structures.</summary>
    private static readonly HashSet<string> Basic = new(["Text", "Integer", "Real", "Boolean", "Date", "URI", "URL", "Rational", "AgentName",
        "ProperName", "Locale", "MIMEType", "XPath", "RenditionClass", "GUID", "GPSCoordinate"], StringComparer.Ordinal);

    /// <summary>
    /// A type by the name an XMP schema gives it: a basic type, "Lang Alt", "Bag", "Seq" or "Alt" of a type, a choice of a type, a
    /// structure — of the predefined ones or <paramref name="declared"/> by an extension schema; null if no type has that name.
    /// </summary>
    public static XmpType? Parse(string name, IReadOnlyDictionary<string, XmpStruct>? declared = null)
    {
        name = name.Trim();
        if (name is "Lang Alt") return new XmpLangAlt();
        foreach (var container in (string[])["Bag ", "Seq ", "Alt "])
            if (name.StartsWith(container, StringComparison.Ordinal))
                return Parse(name[container.Length..], declared) is { } item ? new XmpArray(container.Trim(), item) : null;
        foreach (var choice in (string[])["Closed Choice of ", "Open Choice of ", "Choice of "])
            if (name.StartsWith(choice, StringComparison.Ordinal)) return Parse(name[choice.Length..], declared);
        if (name is "Choice") return new XmpSimple("Text");
        if (Basic.Contains(name)) return new XmpSimple(name);
        if (Structs.TryGetValue(name, out var known)) return known;
        return declared is not null && declared.TryGetValue(name, out var own) ? own : null;
    }

    // ------------------------------------------------------------ the schemas

    private static Dictionary<string, XmpType> Properties(string list) =>
        list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(' ', 2)).ToDictionary(p => p[0], p => Parse(p[1])!, StringComparer.Ordinal);

    /// <summary>The predefined schemas: per namespace the properties with their types.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, XmpType>> Predefined = new Dictionary<string, IReadOnlyDictionary<string, XmpType>>
    {
        [Dc] = Properties("contributor Bag ProperName; coverage Text; creator Seq ProperName; date Seq Date; description Lang Alt; format MIMEType; " +
                          "identifier Text; language Bag Locale; publisher Bag ProperName; relation Bag Text; rights Lang Alt; source Text; " +
                          "subject Bag Text; title Lang Alt; type Bag Text"),
        // xmp:Rating: a closed choice of integers, which strict validators take as a real
        [Xmp] = Properties("Advisory Bag XPath; BaseURL URL; CreateDate Date; CreatorTool AgentName; Identifier Bag Text; Label Text; " +
                           "MetadataDate Date; ModifyDate Date; Nickname Text; Rating Real; Thumbnails Alt Thumbnail"),
        [XmpRights] = Properties("Certificate URL; Marked Boolean; Owner Bag ProperName; UsageTerms Lang Alt; WebStatement URL"),
        [XmpMM] = Properties("DerivedFrom ResourceRef; DocumentID URI; History Seq ResourceEvent; InstanceID URI; LastURL URL; " +
                             "ManagedFrom ResourceRef; Manager AgentName; ManageTo URI; ManageUI URI; ManagerVariant Text; RenditionClass RenditionClass; " +
                             "RenditionOf ResourceRef; RenditionParams Text; SaveID Integer; VersionID Text; Versions Seq Version"),
        [XmpBJ] = Properties("JobRef Bag Job"),
        [XmpTPg] = Properties("MaxPageSize Dimensions; NPages Integer; Fonts Bag Font; Colorants Seq Colorant; PlateNames Seq Text"),
        [XmpDM] = Properties("absPeakAudioFilePath URI; album Text; altTapeName Text; altTimecode Timecode; artist Text; audioModDate Date; " +
                             "audioSampleRate Integer; audioSampleType Text; audioChannelType Text; audioCompressor Text; beatSpliceParams BeatSpliceStretch; " +
                             "composer Text; contributedMedia Bag Media; copyright Text; duration Time; engineer Text; fileDataRate Rational; genre Text; " +
                             "instrument Text; introTime Time; key Text; logComment Text; loop Boolean; numberOfBeats Real; markers Seq Marker; " +
                             "metadataModDate Date; outCue Time; projectRef ProjectLink; pullDown Text; relativePeakAudioFilePath URI; " +
                             "relativeTimestamp Time; releaseDate Date; resampleParams ResampleStretch; scaleType Text; scene Text; shotDate Date; " +
                             "shotLocation Text; shotName Text; speakerPlacement Text; startTimecode Timecode; stretchMode Text; tapeName Text; tempo Real; " +
                             "timeScaleParams TimeScaleStretch; timeSignature Text; trackNumber Integer; videoAlphaMode Text; " +
                             "videoAlphaPremultipleColor Colorant; videoAlphaUnityIsTransparent Boolean; videoColorSpace Text; videoCompressor Text; " +
                             "videoFieldOrder Text; videoFrameRate Text; videoFrameSize Dimensions; videoModDate Date; videoPixelAspectRatio Rational; " +
                             "videoPixelDepth Text"),
        [Pdf] = Properties("Keywords Text; PDFVersion Text; Producer AgentName"),
        [Photoshop] = Properties("AuthorsPosition Text; CaptionWriter ProperName; Category Text; City Text; Country Text; Credit Text; " +
                                 "DateCreated Date; Headline Text; Instructions Text; Source Text; State Text; SupplementalCategories Bag Text; " +
                                 "TransmissionReference Text; Urgency Integer"),
        [Crs] = Properties("AutoBrightness Boolean; AutoContrast Boolean; AutoExposure Boolean; AutoShadows Boolean; BlueHue Integer; " +
                           "BlueSaturation Integer; Brightness Integer; CameraProfile Text; ChromaticAberrationB Integer; ChromaticAberrationR Integer; " +
                           "ColorNoiseReduction Integer; Contrast Integer; CropTop Real; CropLeft Real; CropBottom Real; CropRight Real; CropAngle Real; " +
                           "CropWidth Real; CropHeight Real; CropUnits Integer; Exposure Real; GreenHue Integer; GreenSaturation Integer; HasCrop Boolean; " +
                           "HasSettings Boolean; LuminanceSmoothing Integer; RawFileName Text; RedHue Integer; RedSaturation Integer; Saturation Integer; " +
                           "Shadows Integer; ShadowTint Integer; Sharpness Integer; Temperature Integer; Tint Integer; ToneCurve Seq Text; " +
                           "ToneCurveName Text; Version Text; VignetteAmount Integer; VignetteMidpoint Integer; WhiteBalance Text"),
        [Tiff] = Properties("ImageWidth Integer; ImageLength Integer; BitsPerSample Seq Integer; Compression Integer; PhotometricInterpretation Integer; " +
                            "Orientation Integer; SamplesPerPixel Integer; PlanarConfiguration Integer; YCbCrSubSampling Seq Integer; " +
                            "YCbCrPositioning Integer; XResolution Rational; YResolution Rational; ResolutionUnit Integer; TransferFunction Seq Integer; " +
                            "WhitePoint Seq Rational; PrimaryChromaticities Seq Rational; YCbCrCoefficients Seq Rational; ReferenceBlackWhite Seq Rational; " +
                            "DateTime Date; ImageDescription Lang Alt; Make ProperName; Model ProperName; Software AgentName; Artist ProperName; " +
                            "Copyright Lang Alt"),
        [Exif] = Properties("ExifVersion Text; FlashpixVersion Text; ColorSpace Integer; ComponentsConfiguration Seq Integer; " +
                            "CompressedBitsPerPixel Rational; PixelXDimension Integer; PixelYDimension Integer; UserComment Lang Alt; RelatedSoundFile Text; " +
                            "DateTimeOriginal Date; DateTimeDigitized Date; ExposureTime Rational; FNumber Rational; ExposureProgram Integer; " +
                            "SpectralSensitivity Text; ISOSpeedRatings Seq Integer; OECF OECF; ShutterSpeedValue Rational; ApertureValue Rational; " +
                            "BrightnessValue Rational; ExposureBiasValue Rational; MaxApertureValue Rational; SubjectDistance Rational; " +
                            "MeteringMode Integer; LightSource Integer; Flash Flash; FocalLength Rational; SubjectArea Seq Integer; FlashEnergy Rational; " +
                            "SpatialFrequencyResponse OECF; FocalPlaneXResolution Rational; FocalPlaneYResolution Rational; FocalPlaneResolutionUnit Integer; " +
                            "SubjectLocation Seq Integer; ExposureIndex Rational; SensingMethod Integer; FileSource Integer; SceneType Integer; " +
                            "CFAPattern CFAPattern; CustomRendered Integer; ExposureMode Integer; WhiteBalance Integer; DigitalZoomRatio Rational; " +
                            "FocalLengthIn35mmFilm Integer; SceneCaptureType Integer; GainControl Integer; Contrast Integer; Saturation Integer; " +
                            "Sharpness Integer; DeviceSettingDescription DeviceSettings; SubjectDistanceRange Integer; ImageUniqueID Text; " +
                            "GPSVersionID Text; GPSLatitude GPSCoordinate; GPSLongitude GPSCoordinate; GPSAltitudeRef Integer; GPSAltitude Rational; " +
                            "GPSTimeStamp Date; GPSSatellites Text; GPSStatus Text; GPSMeasureMode Integer; GPSDOP Rational; GPSSpeedRef Text; " +
                            "GPSSpeed Rational; GPSTrackRef Text; GPSTrack Rational; GPSImgDirectionRef Text; GPSImgDirection Rational; GPSMapDatum Text; " +
                            "GPSDestLatitude GPSCoordinate; GPSDestLongitude GPSCoordinate; GPSDestBearingRef Text; GPSDestBearing Rational; " +
                            "GPSDestDistanceRef Text; GPSDestDistance Rational; GPSProcessingMethod Text; GPSAreaInformation Text; GPSDifferential Integer"),
        [Aux] = Properties("Lens Text; SerialNumber Text"),
        // the identification of PDF/A (ISO 19005-2, 6.6.4; ISO 19005-4, 6.7.3)
        [PdfAId] = Properties("part Integer; conformance Text; amd Text; corr Text; rev Text"),
    };

    /// <summary>
    /// The predefined schemas of the XMP Specification of January 2004, those ISO 19005-1, 6.7.2 names: without the Camera Raw, Dynamic Media
    /// and additional EXIF schemas, without xmp:Label, xmp:Rating and the fonts, colorants and plate names of Paged-Text; photoshop:
    /// SupplementalCategories is a text there, and exif:MakerNote a property of its own. The identification has no corrigendum and no revision.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, XmpType>> Predefined2004 = Build2004();

    private static Dictionary<string, IReadOnlyDictionary<string, XmpType>> Build2004()
    {
        var schemas = new Dictionary<string, IReadOnlyDictionary<string, XmpType>>(StringComparer.Ordinal);
        foreach (var (ns, properties) in Predefined)
            if (ns is not (Crs or XmpDM or Aux)) schemas[ns] = new Dictionary<string, XmpType>((IDictionary<string, XmpType>)properties, StringComparer.Ordinal);
        void Change(string ns, Action<Dictionary<string, XmpType>> change) => change((Dictionary<string, XmpType>)schemas[ns]);
        Change(Xmp, p => { p.Remove("Label"); p.Remove("Rating"); });
        Change(XmpTPg, p => { p.Remove("Fonts"); p.Remove("Colorants"); p.Remove("PlateNames"); });
        Change(Photoshop, p => p["SupplementalCategories"] = new XmpSimple("Text"));
        Change(Exif, p => p["MakerNote"] = new XmpSimple("Text"));
        Change(PdfAId, p => { p.Remove("corr"); p.Remove("rev"); });
        return schemas;
    }

    /// <summary>The predefined schemas of a part: of XMP 2004 for PDF/A-1, of XMP 2005 for the others.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, XmpType>> For(PdfAConformance part) =>
        part.Part() == 1 ? Predefined2004 : Predefined;

    // ------------------------------------------------------------ forms of simple values

    private static readonly System.Text.RegularExpressions.Regex IntegerForm = new(@"^[+-]?\d+$");
    private static readonly System.Text.RegularExpressions.Regex RealForm = new(@"^[+-]?(\d+(\.\d*)?|\.\d+)$");
    private static readonly System.Text.RegularExpressions.Regex RationalForm = new(@"^[+-]?\d+/[+-]?\d+$");
    private static readonly System.Text.RegularExpressions.Regex DateForm =
        new(@"^\d{4}(-\d{2}(-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})?)?)?)?$");
    private static readonly System.Text.RegularExpressions.Regex GpsForm = new(@"^\d{1,3},\d{1,2}(,\d{1,2}|\.\d+)[NSEW]$");

    /// <summary>
    /// Whether a simple value has the form of its type (Part 2, 4.2): integers and reals as decimal numbers, rationals as two integers with
    /// „/“, booleans True or False, dates as ISO 8601 in the forms XMP allows, GPS coordinates as „DDD,MM,SSk“ or „DDD,MM.mmk“ — every kind of
    /// text in any form.
    /// </summary>
    public static bool Lexical(string type, string value) => type switch
    {
        "Integer" => IntegerForm.IsMatch(value),
        "Real" => RealForm.IsMatch(value),
        "Rational" => RationalForm.IsMatch(value),
        "Boolean" => value is "True" or "False",
        "Date" => DateForm.IsMatch(value),
        "GPSCoordinate" => GpsForm.IsMatch(value),
        _ => true,
    };
}
