using PdfKnead.Core.Licensing;
using PdfKnead.Core.Policy;
using PdfKnead.Core.Updates;

namespace PdfKnead.Services;

/// <summary>
/// The rules of the license (<see cref="LicensePolicy"/>) applied to the settings of this computer: the declared kind of use, the license
/// key, the end of the introductory period (the one of the license or a later one from the signed version file) and the latest day the
/// program ran. Nothing of it leaves the computer.
/// </summary>
public static class Licensing
{
    private static readonly Lazy<IReadOnlyList<string>> Names = new(LicensePolicy.ComputerNames);
    private static readonly Lazy<IReadOnlyList<string>> Domains = new(LicensePolicy.ComputerDomains);

    /// <summary>Raised when the kind of use, the key or the end of the introductory period changed.</summary>
    public static event Action? Changed;

    /// <summary>The eight features in the order they are named.</summary>
    public static readonly LicensedFeature[] Features =
        [LicensedFeature.Redaction, LicensedFeature.Measuring, LicensedFeature.CertificateSigning, LicensedFeature.PdfA, LicensedFeature.Scanning, LicensedFeature.Forms, LicensedFeature.Editing,
         LicensedFeature.Restrictions];

    /// <summary>
    /// The kind of use that applies: the one a function file of the organization sets (<see cref="ActivePolicy"/>), otherwise the one
    /// chosen. The file does not overwrite the choice — without it, the choice applies again.
    /// </summary>
    public static UsageKind Usage => ActivePolicy.FixedUsage switch
    {
        PolicyUsage.Authority => UsageKind.Authority,
        PolicyUsage.Commercial => UsageKind.Commercial,
        _ => UserSettings.Current.Usage,
    };

    /// <summary>Whether a function file sets the kind of use (then it cannot be changed in the program).</summary>
    public static bool UsageFixed => ActivePolicy.IsActive;

    /// <summary>The context of this computer now.</summary>
    public static LicenseContext Context()
    {
        var s = UserSettings.Current;
        var today = LicensePolicy.CountingDay(DateOnly.FromDateTime(DateTime.Now), s.LatestSeenDay);
        return new LicenseContext(Usage, s.LicenseKey, s.LicenseEmail, today, ReleaseInfo.Date, LicensePolicy.IntroductionEndFor(s.IntroductionUntil),
            Names.Value, Domains.Value)
        {
            CommercialSince = DateOnly.TryParseExact(s.CommercialSince, "yyyy-MM-dd", out var since) ? since : null,
        };
    }

    /// <summary>Whether a feature may be started now.</summary>
    public static GateResult Evaluate(LicensedFeature feature) => LicensePolicy.Evaluate(feature, Context());

    /// <summary>The features that cannot be started now.</summary>
    public static LicensedFeature Locked()
    {
        var context = Context();
        var locked = LicensedFeature.None;
        foreach (var feature in Features)
            if (!LicensePolicy.Evaluate(feature, context).Allowed) locked |= feature;
        return locked;
    }

    /// <summary>The licensed features (<see cref="LicensePolicy.Licensed"/>) for a sentence: „A“, „B“ und „C“.</summary>
    public static string LicensedList()
    {
        // the names as on the buttons, in the quotation marks of the language
        var german = Tr.Culture.TwoLetterISOLanguageName == "de";
        var names = Features.Where(f => (LicensePolicy.Licensed & f) != 0).Select(f => german ? $"„{FeatureName(f)}“" : $"“{FeatureName(f)}”").ToList();
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => Tr.T("{0} und {1}", string.Join(", ", names[..^1]), names[^1]),
        };
    }

    /// <summary>At the start: remembers today as the latest day the program ran, unless a later one is remembered (clock turned back).</summary>
    public static void NoteToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        if (string.CompareOrdinal(today, UserSettings.Current.LatestSeenDay ?? "") > 0) UserSettings.Update(s => s with { LatestSeenDay = today });
        NoteCommercialStart(Usage);
    }

    // the trial counts from the first day a commercial kind of use applies; switching back and forth does not start it again
    private static void NoteCommercialStart(UsageKind usage)
    {
        if (LicensePolicy.IsFreeUse(usage) || usage == UsageKind.NotAsked || UserSettings.Current.CommercialSince is not null) return;
        var day = Context().Today.ToString("yyyy-MM-dd");
        UserSettings.Update(s => s with { CommercialSince = day });
    }

    /// <summary>Takes over a later end of the introductory period from a signed version file; an earlier one is ignored.</summary>
    public static void TakeIntroduction(string? until)
    {
        if (LicensePolicy.IntroductionEndFor(until) <= LicensePolicy.IntroductionEndFor(UserSettings.Current.IntroductionUntil)) return;
        UserSettings.Update(s => s with { IntroductionUntil = until });
        Changed?.Invoke();
    }

    /// <summary>Sets the kind of use.</summary>
    public static void SetUsage(UsageKind usage)
    {
        UserSettings.Update(s => s with { Usage = usage });
        NoteCommercialStart(usage);
        Changed?.Invoke();
    }

    /// <summary>Enters a key (and the e-mail address of a key bound to one); empty removes the key.</summary>
    public static void SetKey(string? key, string? email)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToUpperInvariant();
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        UserSettings.Update(s => s with { LicenseKey = key, LicenseEmail = email });
        Changed?.Invoke();
    }

    /// <summary>Whether the kind of use asks for a license after the introductory period (also when not asked yet).</summary>
    public static bool NeedsLicense(UsageKind usage) => !LicensePolicy.IsFreeUse(usage) && LicensePolicy.AnyLicensed;

    /// <summary>
    /// Before an update: whether the new version is no longer covered by the key entered — then its licensed features would be locked after
    /// the introductory period, while the version installed now keeps working with the key.
    /// </summary>
    public static bool UpdateNotCovered(UpdateManifest manifest)
    {
        var s = UserSettings.Current;
        return NeedsLicense(Usage) && LicenseCodec.Decode(s.LicenseKey) is { } code
            && DateOnly.TryParseExact(manifest.Date, "yyyy-MM-dd", out var released) && !LicensePolicy.Covers(code, released);
    }

    /// <summary>The name of a kind of use, as in the question at the first start.</summary>
    public static string UsageName(UsageKind usage) => usage switch
    {
        UsageKind.Private => Tr.T("Privat"),
        UsageKind.NonProfit => Tr.T("Gemeinnützig oder kleiner Verein"),
        UsageKind.Education => Tr.T("Bildungseinrichtung"),
        UsageKind.Authority => Tr.T("Behörde oder öffentliche Stelle"),
        UsageKind.Commercial => Tr.T("Gewerblich (auch selbstständig oder freiberuflich)"),
        _ => Tr.T("Noch nicht angegeben"),
    };

    /// <summary>The name of a feature.</summary>
    public static string FeatureName(LicensedFeature feature) => feature switch
    {
        LicensedFeature.Redaction => Tr.T("Schwärzen"),
        LicensedFeature.Measuring => Tr.T("Messen"),
        LicensedFeature.CertificateSigning => Tr.T("Signieren mit Zertifikat"),
        LicensedFeature.PdfA => Tr.T("Umwandeln nach PDF/A"),
        LicensedFeature.Scanning => Tr.T("Scannen"),
        LicensedFeature.Forms => Tr.T("Formulare erstellen"),
        LicensedFeature.Editing => Tr.T("Bearbeiten"),
        LicensedFeature.Restrictions => Tr.T("Einschränkungen entfernen"),
        _ => "",
    };

    /// <summary>The names of the features in a set, in their order.</summary>
    public static string FeatureNames(LicensedFeature features) =>
        string.Join(", ", Features.Where(f => (features & f) == f).Select(FeatureName));

    /// <summary>Why a key does not help, for the license window.</summary>
    public static string ProblemText(KeyProblem problem, LicenseCode? code) => problem switch
    {
        KeyProblem.NoKey => Tr.T("Kein Lizenzschlüssel eingetragen."),
        KeyProblem.Unreadable => Tr.T("Der Lizenzschlüssel ist nicht lesbar — bitte auf Tippfehler prüfen."),
        KeyProblem.Expired => Tr.T("Der Lizenzschlüssel galt bis {0:d}.", code?.ExpiresOn),
        KeyProblem.VersionNotCovered => Tr.T("Der Lizenzschlüssel deckt Versionen bis {0:d}; diese Version erschien am {1:d}. Ältere Versionen arbeiten mit ihm weiter.",
            code?.UpdatesUntil, ReleaseInfo.Date),
        KeyProblem.FeatureMissing => Tr.T("Der Lizenzschlüssel schaltet nur frei: {0}.", FeatureNames(code?.Features ?? LicensedFeature.None)),
        KeyProblem.EmailMissing => Tr.T("Dieser Lizenzschlüssel gilt für eine E-Mail-Adresse — bitte die Adresse eintragen, für die er ausgestellt wurde."),
        KeyProblem.BindingMismatch => Tr.T("Der Lizenzschlüssel ist für eine andere E-Mail-Adresse, einen anderen Rechner oder eine andere Domäne ausgestellt."),
        _ => "",
    };

    /// <summary>One line about the state, for the settings and the window „Über PDF-Knead“.</summary>
    public static string StatusText()
    {
        var context = Context();
        if (LicensePolicy.IsFreeUse(context.Usage))
            return Tr.T("{0}: alle Funktionen frei, ohne zeitliche Grenze.", UsageName(context.Usage));
        var code = LicenseCodec.Decode(context.Key);
        if (code is not null && LicensePolicy.Check(code, code.Features, context) == KeyProblem.None)
            return code.HasUpdatesWithoutEnd
                ? Tr.T("Lizenziert für {0} (Nr. {1}): {2}; Updates ohne Ende.", Licensee(code), code.Serial, FeatureNames(code.Features))
                : Tr.T("Lizenziert für {0} (Nr. {1}): {2}; Versionen bis {3:d}.", Licensee(code), code.Serial, FeatureNames(code.Features), code.UpdatesUntil);
        if (!LicensePolicy.AnyLicensed) return Tr.T("{0}: alle Funktionen frei, ohne zeitliche Grenze.", UsageName(context.Usage));
        var days = LicensePolicy.DaysLeft(context.Today, context.FreeUntil);
        if (days <= 0) return Tr.T("Die kostenlose Zeit ist beendet: {0} brauchen eine Lizenz.", LicensedList());
        return context.IsTrial
            ? Tr.T("Kostenlose Testzeit für {2}: noch {0} Tage (bis {1:d}).", days, context.FreeUntil, LicensedList())
            : Tr.T("Kostenlose Einführungszeit für {2}: noch {0} Tage (bis {1:d}).", days, context.FreeUntil, LicensedList());
    }

    private static string Licensee(LicenseCode code) => code.Company.Length > 0 ? code.Company : Tr.T("diesen Lizenznehmer");
}
