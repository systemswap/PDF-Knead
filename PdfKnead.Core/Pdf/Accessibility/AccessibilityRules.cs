namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// A machine-checkable requirement of PDF/UA (docs/ABLAUFPLAN.md, 6.7 step 1): its clause and test number in ISO 14289-1 and -2, the failure
/// condition of the Matterhorn Protocol and what PDF-Knead can repair. The requirement itself is put in the program's own words where the
/// finding is made — no text of the standards.
/// </summary>
/// <param name="Ua1">In ISO 14289-1, such as „7.1-8“; null if part 1 does not have it.</param>
/// <param name="Ua2">In ISO 14289-2, such as „8.11.1-2“; null if part 2 does not have it.</param>
/// <param name="Matterhorn">The Matterhorn failure condition (PDF/UA-1); null if none.</param>
/// <param name="Repair">What PDF-Knead can do about it.</param>
/// <param name="Heuristic">A sign, not a rule (docs/ABLAUFPLAN.md, 6.7 step 4): „Bitte prüfen“, never an error.</param>
internal sealed record AccessibilityRule(string? Ua1, string? Ua2, string? Matterhorn = null, AccessibilityRepair Repair = AccessibilityRepair.None,
    bool Heuristic = false)
{
    /// <summary>The number in the part checked; null if the rule does not apply there.</summary>
    public string? Id(int part) => part == 1 ? Ua1 : Ua2;
}

/// <summary>The rules the check knows, grouped by its stages (docs/ABLAUFPLAN.md, 6.7 step 3).</summary>
internal static class AccessibilityRules
{
    // ------------------------------------------------------------ stage A: document

    /// <summary>The catalogue's XMP names the PDF/UA identification.</summary>
    /// <remarks>No repair: PDF-Knead writes the identification only when everything is met and confirmed (docs/ABLAUFPLAN.md, 6.7 step 8).</remarks>
    public static readonly AccessibilityRule Identification = new("5-1", "5-1", "06-002");

    /// <summary>The identification names the part checked.</summary>
    public static readonly AccessibilityRule IdentificationPart = new("5-2", "5-2", "06-002", AccessibilityRepair.RemoveClaim);

    /// <summary>pdfuaid:part under the prefix pdfuaid.</summary>
    public static readonly AccessibilityRule PartPrefix = new("5-3", "5-3", "06-002", AccessibilityRepair.RemoveClaim);

    /// <summary>PDF/UA-1: pdfuaid:amd under the prefix pdfuaid; PDF/UA-2: pdfuaid:rev under the prefix pdfuaid.</summary>
    public static readonly AccessibilityRule SecondPrefix = new("5-4", "5-4", "06-002", AccessibilityRepair.RemoveClaim);

    /// <summary>PDF/UA-1: pdfuaid:corr under the prefix pdfuaid; PDF/UA-2: the revision names the year 2024.</summary>
    public static readonly AccessibilityRule ThirdProperty = new("5-5", "5-5", "06-002", AccessibilityRepair.RemoveClaim);

    /// <summary>The version: PDF/UA-1 1.0 to 1.7 in the header; PDF/UA-2 2.0 (header or catalogue).</summary>
    public static readonly AccessibilityRule Version = new("6.1-1", "6.1-1");

    /// <summary>The catalogue marks the file as tagged (/MarkInfo /Marked true).</summary>
    public static readonly AccessibilityRule Marked = new("6.2-1", "6.2-1");

    /// <summary>/MarkInfo /Suspects is not true.</summary>
    public static readonly AccessibilityRule Suspects = new("7.1-4", null, "01-007");

    /// <summary>The catalogue has a metadata stream.</summary>
    public static readonly AccessibilityRule Metadata = new("7.1-8", "8.11.1-2", "06-001", AccessibilityRepair.Title);

    /// <summary>The metadata hold a title (dc:title).</summary>
    public static readonly AccessibilityRule Title = new("7.1-9", "8.11.1-1", "06-003", AccessibilityRepair.Title);

    /// <summary>The viewer shows the title (/DisplayDocTitle true).</summary>
    public static readonly AccessibilityRule DisplayDocTitle = new("7.1-10", "8.11.2-1", "07-001", AccessibilityRepair.DisplayDocTitle);

    /// <summary>The catalogue has a structure tree.</summary>
    public static readonly AccessibilityRule StructTreeRoot = new("7.1-11", "8.2.1-1");

    /// <summary>The language of the outline entries can be told.</summary>
    public static readonly AccessibilityRule OutlineLanguage = new("7.2-2", null, "11-003", AccessibilityRepair.Language);

    /// <summary>The language of the /Contents of annotations can be told.</summary>
    public static readonly AccessibilityRule AnnotationLanguage = new("7.2-24", null, "11-004", AccessibilityRepair.Language);

    /// <summary>The language of the /TU of form fields can be told.</summary>
    public static readonly AccessibilityRule FieldLanguage = new("7.2-25", null, "11-005", AccessibilityRepair.Language);

    /// <summary>
    /// Every /Lang is a language tag in the form the standards name (ISO 32000-1, 14.9.2: RFC 3066; the reference checker asks the same form
    /// of PDF/UA-2) and not empty — catalogue, structure elements, marked content.
    /// </summary>
    public static readonly AccessibilityRule LanguageTag = new("7.2-29", "8.4.4-2", null, AccessibilityRepair.Language);

    /// <summary>The language of the metadata can be told.</summary>
    public static readonly AccessibilityRule MetadataLanguage = new("7.2-33", null, "11-006", AccessibilityRepair.Language);

    /// <summary>The language of the text on the pages can be told.</summary>
    public static readonly AccessibilityRule PageLanguage = new("7.2-34", null, "11-001", AccessibilityRepair.Language);

    /// <summary>
    /// Heuristic H11: a language tag of the right form whose language subtag no language can have (four to eight letters, an unknown two-letter
    /// code) — usually the name of a language („English“); with the tag meant (Matterhorn 11-007 is the human check „language not appropriate“).
    /// </summary>
    public static readonly AccessibilityRule LanguageKnown = new("H11", "H11", "11-007", AccessibilityRepair.Language, Heuristic: true);

    /// <summary>PDF/UA-2: the catalogue names a language (/Lang, not empty).</summary>
    public static readonly AccessibilityRule CatalogLanguage = new(null, "8.4.4-1", null, AccessibilityRepair.Language);

    /// <summary>Every layer configuration has a name that is not empty.</summary>
    public static readonly AccessibilityRule LayerName = new("7.10-1", "8.7-1", "20-001", AccessibilityRepair.LayerNames);

    /// <summary>No layer configuration has /AS.</summary>
    public static readonly AccessibilityRule LayerAutoState = new("7.10-2", "8.7-2", "20-003");

    /// <summary>The file specification of an embedded file has /F and /UF, not empty.</summary>
    public static readonly AccessibilityRule EmbeddedFileNames = new("7.11-1", null, "21-001");

    /// <summary>PDF/UA-2: the file specification of an embedded file has a description (/Desc).</summary>
    public static readonly AccessibilityRule EmbeddedFileDescription = new(null, "8.14.1-1");

    /// <summary>
    /// The file specification of a file attachment annotation meets the requirements of embedded files (ISO 14289-1, 7.18.7; the rule
    /// collections have no machine rule for it — Matterhorn 28-016).
    /// </summary>
    public static readonly AccessibilityRule AttachmentAnnotationNames = new("7.18.7-1", null, "28-016");

    /// <summary>PDF/UA-1: no dynamic XFA (dynamicRender required); PDF/UA-2: no XFA at all.</summary>
    public static readonly AccessibilityRule Xfa = new("7.15-1", "8.10.1-3", "25-001", AccessibilityRepair.RemoveXfa);

    /// <summary>An encrypted file lets assistive technology read it (/P with bit 10).</summary>
    public static readonly AccessibilityRule Permissions = new("7.16-1", null, "26-002", AccessibilityRepair.Permissions);

    // ------------------------------------------------------------ stage B: fonts and characters of the private use areas

    /// <summary>The font rules of ISO 14289-1, 7.21 and -2, 8.4.5, by the test of PDF/A-2 level u that checks the same (ISO 19005-2, 6.2.11).</summary>
    public static readonly IReadOnlyDictionary<string, AccessibilityRule> Fonts = new Dictionary<string, AccessibilityRule>(StringComparer.Ordinal)
    {
        ["6.2.11.3.1-1"] = new("7.21.3.1-1", "8.4.5.3.1-1", "31-002"),
        ["6.2.11.3.2-1"] = new("7.21.3.2-1", "8.4.5.3.2-1", "31-005"),
        ["6.2.11.3.3-1"] = new("7.21.3.3-1", "8.4.5.4-1", "31-006"),
        ["6.2.11.3.3-2"] = new("7.21.3.3-2", "8.4.5.4-2", "31-007"),
        ["6.2.11.3.3-3"] = new("7.21.3.3-3", "8.4.5.4-3", "31-008"),
        ["6.2.11.4.1-1"] = new("7.21.4.1-1", "8.4.5.5.1-1", "31-009"),
        ["6.2.11.4.1-2"] = new("7.21.4.1-2", "8.4.5.5.1-2", "31-011"),
        ["6.2.11.4.2-1"] = new("7.21.4.2-1", null, "31-012"),
        ["6.2.11.4.2-2"] = new("7.21.4.2-2", null, "31-014"),
        ["6.2.11.5-1"] = new("7.21.5-1", "8.4.5.6-1", "31-016"),
        ["6.2.11.6-1"] = new("7.21.6-1", "8.4.5.7-1", "31-017"),
        ["6.2.11.6-2"] = new("7.21.6-2", "8.4.5.7-2", "31-021"),
        ["6.2.11.6-3"] = new("7.21.6-3", "8.4.5.7-3", "31-024"),
        ["6.2.11.6-4"] = new("7.21.6-4", "8.4.5.7-4", "31-026"),
        ["6.2.11.7.2-1"] = new("7.21.7-1", "8.4.5.8-1", "31-027"),
        ["6.2.11.7.2-2"] = new("7.21.7-2", "8.4.5.8-2", "31-028"),
        ["6.2.11.8-1"] = new("7.21.8-1", "8.4.5.9-1", "31-030"),
    };

    /// <summary>PDF/UA-2: /ActualText without characters of the private use areas.</summary>
    public static readonly AccessibilityRule ActualTextNoPua = new(null, "8.4.3-2");

    /// <summary>PDF/UA-2: /Alt without characters of the private use areas.</summary>
    public static readonly AccessibilityRule AltNoPua = new(null, "8.4.3-3");

    /// <summary>PDF/UA-2: text strings meant to be read hold no characters of the private use areas.</summary>
    public static readonly AccessibilityRule TextStringNoPua = new(null, "8.6-1");

    // ------------------------------------------------------------ stage C: content (TaggedContent)

    /// <summary>No artifact inside tagged content.</summary>
    public static readonly AccessibilityRule ArtifactInTagged = new("7.1-1", null, "01-003");

    /// <summary>No tagged content inside an artifact.</summary>
    public static readonly AccessibilityRule TaggedInArtifact = new("7.1-2", null, "01-004");

    /// <summary>All content is tagged (marked content whose MCID the parent tree gives to an element) or an artifact.</summary>
    public static readonly AccessibilityRule UntaggedContent = new("7.1-3", "8.2.2-1", "01-005");

    /// <summary>Every structure element has a parent (/P) — of stage E, built with the check of the tree in stage C.</summary>
    public static readonly AccessibilityRule ElementParent = new("7.1-12", "8.2.1-2");

    /// <summary>The language of /ActualText in marked content can be told.</summary>
    public static readonly AccessibilityRule ActualTextLanguage = new("7.2-30", null, "11-002", AccessibilityRepair.Language);

    /// <summary>The language of /Alt in marked content can be told.</summary>
    public static readonly AccessibilityRule AltLanguage = new("7.2-31", null, "11-002", AccessibilityRepair.Language);

    /// <summary>The language of /E in marked content can be told.</summary>
    public static readonly AccessibilityRule ExpansionLanguage = new("7.2-32", null, "11-002", AccessibilityRepair.Language);

    /// <summary>No reference XObject.</summary>
    public static readonly AccessibilityRule ReferenceXObject = new("7.20-1", null, "30-001");

    /// <summary>A form holding marked content with MCIDs is drawn only once.</summary>
    public static readonly AccessibilityRule FormWithMcidsOnce = new("7.20-2", null, "30-002");

    /// <summary>PDF/UA-2: real content showing characters of the private use areas has /ActualText or /Alt (marked content or element).</summary>
    public static readonly AccessibilityRule PrivateUseReplaced = new(null, "8.4.3-1");

    // ------------------------------------------------------------ stage D: annotations without their structure

    /// <summary>A page with annotations names their order: PDF/UA-1 that of the structure (/Tabs /S), PDF/UA-2 one of /A, /W, /S.</summary>
    public static readonly AccessibilityRule TabOrder = new("7.18.3-1", "8.9.3.3-1");

    /// <summary>No trap network annotation.</summary>
    public static readonly AccessibilityRule TrapNet = new("7.18.2-1", "8.9.2.4.15-1");

    /// <summary>PDF/UA-1: an annotation other than a widget has /Contents or an /Alt on its structure element.</summary>
    public static readonly AccessibilityRule AnnotationDescription = new("7.18.1-2", null);

    /// <summary>PDF/UA-1: a link describes itself in /Contents.</summary>
    public static readonly AccessibilityRule LinkDescription = new("7.18.5-2", null);

    /// <summary>PDF/UA-1: a media clip names the type of its content (/CT).</summary>
    public static readonly AccessibilityRule MediaClipType = new("7.18.6.2-1", null);

    /// <summary>PDF/UA-1: a media clip has a description (/Alt).</summary>
    public static readonly AccessibilityRule MediaClipDescription = new("7.18.6.2-2", null);

    /// <summary>PDF/UA-2: the rich text of a markup annotation (/RC) says the same as its /Contents.</summary>
    public static readonly AccessibilityRule RichTextContents = new(null, "8.9.2.3-2");

    /// <summary>PDF/UA-2: a stamp says what it means (/Name or /Contents).</summary>
    public static readonly AccessibilityRule StampDescription = new(null, "8.9.2.4.7-1");

    /// <summary>PDF/UA-2: an ink annotation has /Contents.</summary>
    public static readonly AccessibilityRule InkDescription = new(null, "8.9.2.4.8-1");

    /// <summary>PDF/UA-2: a pop-up is not in the structure tree.</summary>
    public static readonly AccessibilityRule PopupInTree = new(null, "8.9.2.4.9-1");

    /// <summary>PDF/UA-2: the file of a file attachment annotation names its relationship (/AFRelationship).</summary>
    public static readonly AccessibilityRule AttachmentRelationship = new(null, "8.9.2.4.10-1");

    /// <summary>PDF/UA-2: no sound annotation.</summary>
    public static readonly AccessibilityRule SoundAnnotation = new(null, "8.9.2.4.11-1");

    /// <summary>PDF/UA-2: no movie annotation.</summary>
    public static readonly AccessibilityRule MovieAnnotation = new(null, "8.9.2.4.11-2");

    /// <summary>PDF/UA-2: a screen annotation has /Contents.</summary>
    public static readonly AccessibilityRule ScreenDescription = new(null, "8.9.2.4.12-1");

    /// <summary>PDF/UA-2: a 3D annotation has /Contents.</summary>
    public static readonly AccessibilityRule ThreeDDescription = new(null, "8.9.2.4.19-1");

    /// <summary>PDF/UA-2: a rich media annotation has /Contents.</summary>
    public static readonly AccessibilityRule RichMediaDescription = new(null, "8.9.2.4.19-2");

    /// <summary>PDF/UA-2: a widget of a field in the structure with additional actions (/AA) has /Contents.</summary>
    public static readonly AccessibilityRule WidgetActionDescription = new(null, "8.10.2.3-2");

    /// <summary>PDF/UA-2: a text field with a rich value (/RV) has a value (/V) with the same text.</summary>
    public static readonly AccessibilityRule RichValue = new(null, "8.10.3.3-1");

    // ------------------------------------------------------------ stage E: the structure tree

    /// <summary>Every structure type is standard or mapped to one (PDF/UA-2: in one of the standard namespaces or MathML).</summary>
    public static readonly AccessibilityRule TypeMapped = new("7.1-5", "8.2.4-1", "02-001");

    /// <summary>The role map runs in no circle.</summary>
    public static readonly AccessibilityRule RoleMapCircle = new("7.1-6", "8.2.4-2", "02-003");

    /// <summary>A standard type is not mapped to another.</summary>
    public static readonly AccessibilityRule StandardNotRemapped = new("7.1-7", "8.2.4-4", "02-004");

    /// <summary>PDF/UA-2: no type is mapped to a type of the same explicit namespace.</summary>
    public static readonly AccessibilityRule RoleMapSameNamespace = new(null, "8.2.4-3");

    /// <summary>PDF/UA-1: the language of an element's /ActualText can be told (on it, above it or in the catalogue).</summary>
    public static readonly AccessibilityRule ElementActualTextLanguage = new("7.2-21", null, null, AccessibilityRepair.Language);

    /// <summary>PDF/UA-1: the language of an element's /Alt can be told.</summary>
    public static readonly AccessibilityRule ElementAltLanguage = new("7.2-22", null, null, AccessibilityRepair.Language);

    /// <summary>PDF/UA-1: the language of an element's /E can be told.</summary>
    public static readonly AccessibilityRule ElementExpansionLanguage = new("7.2-23", null, null, AccessibilityRepair.Language);

    /// <summary>A figure has a description (PDF/UA-1: /Alt not empty or /ActualText; PDF/UA-2: /Alt or /ActualText).</summary>
    public static readonly AccessibilityRule FigureDescription = new("7.3-1", "8.2.5.28.2-1", "13-004");

    /// <summary>PDF/UA-1: numbered headings begin with H1 and skip no level.</summary>
    public static readonly AccessibilityRule HeadingNesting = new("7.4.2-1", null, "14-003");

    /// <summary>PDF/UA-1: an element holds at most one H.</summary>
    public static readonly AccessibilityRule OneHeadingPerNode = new("7.4.4-1", null, "14-006");

    /// <summary>PDF/UA-1: H elements in a document that uses numbered headings as well.</summary>
    public static readonly AccessibilityRule PlainHeadingsOnly = new("7.4.4-2", null, "14-007");

    /// <summary>PDF/UA-1: numbered headings in a document that uses H as well.</summary>
    public static readonly AccessibilityRule NumberedHeadingsOnly = new("7.4.4-3", null, "14-007");

    /// <summary>PDF/UA-1: a formula has a description (/Alt not empty or /ActualText).</summary>
    public static readonly AccessibilityRule FormulaDescription = new("7.7-1", null, "17-002");

    /// <summary>PDF/UA-1: a note has an /ID.</summary>
    public static readonly AccessibilityRule NoteId = new("7.9-1", null, "19-003");

    /// <summary>PDF/UA-1: the /ID of each note is its own.</summary>
    public static readonly AccessibilityRule NoteIdUnique = new("7.9-2", null, "19-004");

    /// <summary>PDF/UA-2: the root holds one Document as its only kid.</summary>
    public static readonly AccessibilityRule RootDocument = new(null, "8.2.5.2-1");

    /// <summary>PDF/UA-2: that Document is of the namespace for PDF 2.0.</summary>
    public static readonly AccessibilityRule RootDocumentNamespace = new(null, "8.2.5.2-2");

    /// <summary>PDF/UA-2: each TOCI names its target with /Ref (on it or below it).</summary>
    public static readonly AccessibilityRule TociRef = new(null, "8.2.5.8-1");

    /// <summary>PDF/UA-2: no H.</summary>
    public static readonly AccessibilityRule NoPlainHeading = new(null, "8.2.5.12-1");

    /// <summary>PDF/UA-2: no Note (of PDF 1.7) unless mapped into the namespace for PDF 2.0.</summary>
    public static readonly AccessibilityRule NoNote = new(null, "8.2.5.14-1");

    /// <summary>PDF/UA-2: the NoteType of an FENote is Footnote, Endnote or None.</summary>
    public static readonly AccessibilityRule NoteType = new(null, "8.2.5.14-4");

    /// <summary>PDF/UA-2: a list with labels names its numbering (ListNumbering other than None).</summary>
    public static readonly AccessibilityRule ListNumbering = new(null, "8.2.5.25-1");

    /// <summary>PDF/UA-2: MathML only below a Formula (or other MathML).</summary>
    public static readonly AccessibilityRule MathInFormula = new(null, "8.2.5.29-1");

    // ------------------------------------------------------------ stage F: annotations in the structure tree

    /// <summary>PDF/UA-1: an annotation other than a widget, printer mark or link is in an Annot element.</summary>
    public static readonly AccessibilityRule AnnotationInAnnot = new("7.18.1-1", null, "28-002");

    /// <summary>PDF/UA-1: a field has /TU, or its widget's element an /Alt.</summary>
    public static readonly AccessibilityRule FieldDescription = new("7.18.1-3", null, "28-005");

    /// <summary>A widget (of a field, in PDF/UA-2 as real content) is in a Form element.</summary>
    public static readonly AccessibilityRule WidgetInForm = new("7.18.4-1", "8.10.1-1", "28-010");

    /// <summary>PDF/UA-1: a Form element without a Role holds exactly one widget; PDF/UA-2: a Form element at most one widget.</summary>
    public static readonly AccessibilityRule FormOneWidget = new("7.18.4-2", "8.10.1-2", "28-011");

    /// <summary>A link is in a Link element (PDF/UA-2: Link or Reference, as real content).</summary>
    public static readonly AccessibilityRule LinkInLink = new("7.18.5-1", "8.2.5.20-1", "28-011");

    /// <summary>PDF/UA-1: a printer mark is not in the structure tree.</summary>
    public static readonly AccessibilityRule PrinterMarkOutside = new("7.18.8-1", null, "28-018");

    /// <summary>PDF/UA-2: an annotation with the Invisible flag is an artifact.</summary>
    public static readonly AccessibilityRule InvisibleArtifact = new(null, "8.9.2.2-1");

    /// <summary>PDF/UA-2: an annotation with NoView (without ToggleNoView) is an artifact.</summary>
    public static readonly AccessibilityRule NoViewArtifact = new(null, "8.9.2.2-2");

    /// <summary>PDF/UA-2: a markup annotation as real content is in an Annot element.</summary>
    public static readonly AccessibilityRule MarkupInAnnot = new(null, "8.9.2.3-1");

    /// <summary>PDF/UA-2: a widget of no size is an artifact.</summary>
    public static readonly AccessibilityRule ZeroWidgetArtifact = new(null, "8.9.2.4.13-1");

    /// <summary>PDF/UA-2: a printer mark is an artifact.</summary>
    public static readonly AccessibilityRule PrinterMarkArtifact = new(null, "8.9.2.4.14-1");

    /// <summary>PDF/UA-2: a watermark annotation as real content is in an Annot element.</summary>
    public static readonly AccessibilityRule WatermarkInAnnot = new(null, "8.9.2.4.16-1");

    /// <summary>PDF/UA-2: /Contents of an annotation and /Alt of its element say the same.</summary>
    public static readonly AccessibilityRule ContentsEqualsAlt = new(null, "8.9.4.2-1");

    /// <summary>PDF/UA-2: a widget of a field has a label (Lbl) or /Contents.</summary>
    public static readonly AccessibilityRule WidgetLabel = new(null, "8.10.2.3-1");

    /// <summary>PDF/UA-2: the links of one Link element lead to one target.</summary>
    public static readonly AccessibilityRule LinkOneTarget = new(null, "8.2.5.20-2");

    /// <summary>
    /// PDF/UA-2, 8.9.2.1: annotations are in the structure tree unless they are artifacts — a sign only (any annotation may be an artifact; the
    /// rule collections have no machine rule for it).
    /// </summary>
    public static readonly AccessibilityRule AnnotationInTree = new(null, "8.9.2.1-1", null, AccessibilityRepair.None, Heuristic: true);

    // ------------------------------------------------------------ stage G: containment and tables

    /// <summary>PDF/UA-1: a Table holds only TR, THead, TBody, TFoot and Caption.</summary>
    public static readonly AccessibilityRule TableKids = new("7.2-3", null, "09-004");

    /// <summary>PDF/UA-1: a TR stands in Table, THead, TBody or TFoot.</summary>
    public static readonly AccessibilityRule RowInTable = new("7.2-4", null, "09-004");

    /// <summary>PDF/UA-1: a THead stands in a Table.</summary>
    public static readonly AccessibilityRule HeadInTable = new("7.2-5", null, "09-004");

    /// <summary>PDF/UA-1: a TBody stands in a Table.</summary>
    public static readonly AccessibilityRule BodyInTable = new("7.2-6", null, "09-004");

    /// <summary>PDF/UA-1: a TFoot stands in a Table.</summary>
    public static readonly AccessibilityRule FootInTable = new("7.2-7", null, "09-004");

    /// <summary>PDF/UA-1: a TH stands in a TR.</summary>
    public static readonly AccessibilityRule HeaderCellInRow = new("7.2-8", null, "09-004");

    /// <summary>PDF/UA-1: a TD stands in a TR.</summary>
    public static readonly AccessibilityRule DataCellInRow = new("7.2-9", null, "09-004");

    /// <summary>PDF/UA-1: a TR holds only TH and TD.</summary>
    public static readonly AccessibilityRule RowKids = new("7.2-10", null, "09-004");

    /// <summary>PDF/UA-1: a Table holds at most one THead.</summary>
    public static readonly AccessibilityRule OneTableHead = new("7.2-11", null, "09-004");

    /// <summary>PDF/UA-1: a Table holds at most one TFoot.</summary>
    public static readonly AccessibilityRule OneTableFoot = new("7.2-12", null, "09-004");

    /// <summary>PDF/UA-1: a Table with a TFoot holds a TBody.</summary>
    public static readonly AccessibilityRule FootNeedsBody = new("7.2-13", null, "09-004");

    /// <summary>PDF/UA-1: a Table with a THead holds a TBody.</summary>
    public static readonly AccessibilityRule HeadNeedsBody = new("7.2-14", null, "09-004");

    /// <summary>The cells of a table do not overlap.</summary>
    public static readonly AccessibilityRule CellsOverlap = new("7.2-15", "8.2.5.26-1", "09-004");

    /// <summary>PDF/UA-1: the Caption of a Table is its first or last kid.</summary>
    public static readonly AccessibilityRule TableCaptionPlace = new("7.2-16", null, "09-004");

    /// <summary>PDF/UA-1: an LI stands in an L.</summary>
    public static readonly AccessibilityRule ItemInList = new("7.2-17", null, "09-005");

    /// <summary>PDF/UA-1: an LBody stands in an LI.</summary>
    public static readonly AccessibilityRule BodyInItem = new("7.2-18", null, "09-005");

    /// <summary>PDF/UA-1: an L holds only L, LI and Caption.</summary>
    public static readonly AccessibilityRule ListKids = new("7.2-19", null, "09-005");

    /// <summary>PDF/UA-1: an LI holds only Lbl and LBody.</summary>
    public static readonly AccessibilityRule ItemKids = new("7.2-20", null, "09-005");

    /// <summary>PDF/UA-1: a TOCI stands in a TOC.</summary>
    public static readonly AccessibilityRule TociInToc = new("7.2-26", null, "09-006");

    /// <summary>PDF/UA-1: a TOC holds only TOC, TOCI and Caption.</summary>
    public static readonly AccessibilityRule TocKids = new("7.2-27", null, "09-006");

    /// <summary>PDF/UA-1: the Caption of a TOC is its first kid.</summary>
    public static readonly AccessibilityRule TocCaptionFirst = new("7.2-28", null, "09-006");

    /// <summary>PDF/UA-1: a THead holds only TR.</summary>
    public static readonly AccessibilityRule HeadKids = new("7.2-36", null, "09-004");

    /// <summary>PDF/UA-1: a TBody holds only TR.</summary>
    public static readonly AccessibilityRule BodyKids = new("7.2-37", null, "09-004");

    /// <summary>PDF/UA-1: a TFoot holds only TR.</summary>
    public static readonly AccessibilityRule FootKids = new("7.2-38", null, "09-004");

    /// <summary>PDF/UA-1: a Table holds at most one Caption.</summary>
    public static readonly AccessibilityRule OneTableCaption = new("7.2-39", null, "09-004");

    /// <summary>PDF/UA-1: the Caption of an L is its first kid.</summary>
    public static readonly AccessibilityRule ListCaptionFirst = new("7.2-40", null, "09-005");

    /// <summary>Every column of a table reaches over as many rows as the first (PDF/UA-2: also within THead, TBody, TFoot).</summary>
    public static readonly AccessibilityRule ColumnsRegular = new("7.2-41", "8.2.5.26-2", "09-004");

    /// <summary>No row of a table reaches over more columns than the first.</summary>
    public static readonly AccessibilityRule RowsReachBeyond = new("7.2-42", "8.2.5.26-3", "09-004");

    /// <summary>Every row of a table reaches over as many columns as the first.</summary>
    public static readonly AccessibilityRule RowsRegular = new("7.2-43", "8.2.5.26-4", "09-004");

    /// <summary>In a table with header cells every data cell has one: through /Headers or a TH with /Scope.</summary>
    public static readonly AccessibilityRule HeaderDeterminable = new("7.5-1", "8.2.5.26-5", "15-003");

    /// <summary>A data cell whose /Headers names unknown IDs still has a header through /Scope.</summary>
    public static readonly AccessibilityRule HeaderReferences = new("7.5-2", "8.2.5.26-6", "15-005");

    /// <summary>PDF/UA-2: a Ruby holds RB and RT, or RB, RP, RT and RP.</summary>
    public static readonly AccessibilityRule RubyKids = new(null, "8.2.5.23-1");

    /// <summary>PDF/UA-2: a Warichu holds WP, WT and WP.</summary>
    public static readonly AccessibilityRule WarichuKids = new(null, "8.2.5.24-1");

    /// <summary>PDF/UA-2: an LI holds no content directly (only in Lbl or LBody).</summary>
    public static readonly AccessibilityRule ItemContentInBody = new(null, "8.2.5.25-2");

    /// <summary>PDF/UA-2: a FENote names in /Ref every element citing it.</summary>
    public static readonly AccessibilityRule NoteListsCitations = new(null, "8.2.5.14-2");

    /// <summary>PDF/UA-2: the elements a FENote names in /Ref cite it.</summary>
    public static readonly AccessibilityRule CitationsReferToNote = new(null, "8.2.5.14-3");

    /// <summary>PDF/UA-2: a Caption is the first or last kid of its element.</summary>
    public static readonly AccessibilityRule CaptionFirstOrLast = new(null, "8.2.5.27-1");

    /// <summary>PDF/UA-2 (ISO 14289-2, 5 and 8.2.4): the containment rules of ISO/TS 32005, Table 5 (<see cref="Containment"/>); the number is an own one — the rule collection of the reference checker numbers each pair.</summary>
    public static readonly AccessibilityRule ContainmentRules = new(null, "32005-T5");

    // ------------------------------------------------------------ destinations

    /// <summary>PDF/UA-2: a destination within the document is a structure destination (begins with a structure element).</summary>
    public static readonly AccessibilityRule StructureDestination = new(null, "8.8-1");

    /// <summary>PDF/UA-2: a GoTo action has a structure destination (/SD).</summary>
    public static readonly AccessibilityRule GoToStructureDestination = new(null, "8.8-2");

    /// <summary>
    /// PDF/UA-2, 8.10.3.5: the graphic of a visible signature has an alternative text — a sign only (the rule collections leave open which
    /// content counts as the signature's graphic; /Alt of the element or /Contents of the widget counts, they must be equal by 8.9.4.2).
    /// </summary>
    public static readonly AccessibilityRule SignatureGraphicAlt = new(null, "8.10.3.5-1", null, AccessibilityRepair.None, Heuristic: true);
}
