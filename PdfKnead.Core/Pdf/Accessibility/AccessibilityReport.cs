namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>The profile a file is checked against (docs/ABLAUFPLAN.md, 6.7 step 2).</summary>
public enum AccessibilityProfile
{
    /// <summary>PDF/UA-1 (ISO 14289-1): files up to PDF 1.7.</summary>
    PdfUa1,

    /// <summary>PDF/UA-2 (ISO 14289-2) and WTPDF 1.0: files in PDF 2.0.</summary>
    PdfUa2,

    /// <summary>Public authorities (BITV 2.0, EN 301 549 chapter 10): PDF/UA-1 or -2 by the version.</summary>
    Authority,

    /// <summary>Hints only (the default for firms): as PDF/UA-1 or -2, every finding only a hint.</summary>
    HintsOnly,
}

/// <summary>How much a finding weighs.</summary>
public enum AccessibilitySeverity
{
    /// <summary>A rule the file breaks: „Muss behoben werden“.</summary>
    Error,

    /// <summary>A sign that something may be wrong: „Bitte prüfen“.</summary>
    Check,

    /// <summary>Said, not counted (profile „Nur Hinweise“).</summary>
    Hint,
}

/// <summary>What PDF-Knead can do about a finding (docs/ABLAUFPLAN.md, 6.7 step 7; the repairs are built step by step).</summary>
public enum AccessibilityRepair
{
    /// <summary>Nothing PDF-Knead can do by itself.</summary>
    None,

    /// <summary>Give the document a title.</summary>
    Title,

    /// <summary>Set or correct the language of the document.</summary>
    Language,

    /// <summary>Show the title instead of the file name.</summary>
    DisplayDocTitle,

    /// <summary>Tab order by structure (/Tabs).</summary>
    Tabs,

    /// <summary>Name the layer configurations.</summary>
    LayerNames,

    /// <summary>Allow assistive technology (permission bit 10) by protecting anew.</summary>
    Permissions,

    /// <summary>Remove the PDF/UA identification.</summary>
    RemoveClaim,

    /// <summary>Remove the XFA form.</summary>
    RemoveXfa,
}

/// <summary>One finding of the check.</summary>
/// <param name="Id">Clause and test number in the part of ISO 14289 of the profile, such as „7.1-8“ or „8.11.1-1“.</param>
/// <param name="Matterhorn">The failure condition of the Matterhorn Protocol 1.1 (PDF/UA-1), such as „06-003“; null if none.</param>
/// <param name="Severity">How much it weighs.</param>
/// <param name="Text">What is wrong, in the language of the user interface.</param>
/// <param name="Page">The page (from 1); null for the document as a whole.</param>
/// <param name="Repair">What PDF-Knead can do about it.</param>
/// <param name="Suggestion">A value proposed for the repair (such as „en“ for the language „English“); null if none.</param>
public sealed record AccessibilityFinding(string Id, string? Matterhorn, AccessibilitySeverity Severity, string Text, int? Page = null,
    AccessibilityRepair Repair = AccessibilityRepair.None, string? Suggestion = null);

/// <summary>The result of the check „barrierefrei?“.</summary>
/// <param name="Profile">The profile checked.</param>
/// <param name="Part">The part of ISO 14289 the rule numbers belong to (1 or 2).</param>
/// <param name="Findings">What was found, each rule once (first place).</param>
/// <param name="Stages">The stages of the check built so far (docs/ABLAUFPLAN.md, 6.7 step 3), such as „A, B“: what is not built is not checked.</param>
public sealed record AccessibilityReport(AccessibilityProfile Profile, int Part, IReadOnlyList<AccessibilityFinding> Findings, string Stages)
{
    /// <summary>The number of findings that must be fixed.</summary>
    public int Errors => Findings.Count(f => f.Severity == AccessibilitySeverity.Error);

    /// <summary>The headline in words (not only in colour), announced as a status message.</summary>
    public string Headline => Errors > 0
        ? Tr.T("Nicht barrierefrei nach PDF/UA-{0}: {1} Fehler", Part, Errors)
        : Findings.Count > 0
            ? Tr.T("Maschinell geprüft (Stufen {0}): keine Fehler — {1} Hinweise", Stages, Findings.Count)
            : Tr.T("Maschinell geprüft (Stufen {0}): keine Fehler", Stages);
}
