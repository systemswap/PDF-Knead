using System.Net.NetworkInformation;
using System.Reflection;

namespace PdfKnead.Core.Licensing;

/// <summary>The kind of use the user declared at the first start (LICENSE, section 3 (3)); a self-declaration, never transmitted.</summary>
public enum UsageKind
{
    /// <summary>Not asked yet; counts as commercial for the lock, the program asks before anything is locked.</summary>
    NotAsked,
    Private,
    /// <summary>A non-profit organization or a small association without full-time employees paid by it.</summary>
    NonProfit,
    Education,
    Authority,
    /// <summary>A company, also self-employed persons and freelancers.</summary>
    Commercial,
}

/// <summary>Whether a licensed feature may be used, and why.</summary>
public enum GateState
{
    /// <summary>Non-commercial use: always free.</summary>
    Free,
    /// <summary>Commercial use within the free introductory period.</summary>
    Introductory,
    /// <summary>Commercial use with a matching license key.</summary>
    Licensed,
    /// <summary>Commercial use after the introductory period without a matching key: the feature cannot be started.</summary>
    Locked,
}

/// <summary>Why a key does not unlock a feature.</summary>
public enum KeyProblem
{
    None,
    NoKey,
    Unreadable,
    Expired,
    VersionNotCovered,
    FeatureMissing,
    EmailMissing,
    BindingMismatch,
}

/// <summary>The answer for one feature.</summary>
/// <param name="State">Free, introductory, licensed or locked.</param>
/// <param name="Problem">For a commercial use without a working key: why the key does not help (also during the introductory period).</param>
/// <param name="DaysLeft">Days of the introductory period left, counting today; 0 after its end.</param>
/// <param name="Code">The key entered, when readable.</param>
public sealed record GateResult(GateState State, KeyProblem Problem, int DaysLeft, LicenseCode? Code)
{
    public bool Allowed => State != GateState.Locked;
}

/// <summary>Everything the decision depends on; the program fills it from the settings and the computer, the tests directly.</summary>
/// <param name="Usage">The declared kind of use.</param>
/// <param name="Key">The license key entered; null = none.</param>
/// <param name="Email">The e-mail address confirmed for a key bound to one; null = none.</param>
/// <param name="Today">The day that counts (<see cref="LicensePolicy.CountingDay"/>).</param>
/// <param name="ReleaseDate">The day this version was released (<see cref="ReleaseInfo.Date"/>).</param>
/// <param name="IntroductionEnd">The last day of the introductory period (<see cref="LicensePolicy.IntroductionEndFor"/>).</param>
/// <param name="HostNames">The names of this computer.</param>
/// <param name="Domains">The domains of this computer.</param>
public sealed record LicenseContext(UsageKind Usage, string? Key, string? Email, DateOnly Today, DateOnly ReleaseDate, DateOnly IntroductionEnd,
    IReadOnlyList<string> HostNames, IReadOnlyList<string> Domains)
{
    /// <summary>The first day a commercial kind of use applied on this computer (start of the trial); null = not yet.</summary>
    public DateOnly? CommercialSince { get; init; }

    /// <summary>The last free day of the licensed features: the end of the introductory period or of the trial, whichever is later.</summary>
    public DateOnly FreeUntil => LicensePolicy.FreeUntil(IntroductionEnd, CommercialSince);

    /// <summary>Whether the free time is the trial (it ends after the introductory period).</summary>
    public bool IsTrial => FreeUntil > IntroductionEnd;
}

/// <summary>
/// The rules of LICENSE, sections 2 and 3: non-commercial use is free with everything; commercial use is free except the licensed features
/// (<see cref="Licensed"/>), which are free too until the end of the introductory period — or of a trial of <see cref="TrialMonths"/> months
/// from the first commercial start, whichever is later — and then need a matching key. The lock is an aid to comply with
/// the license, not copy protection: only starting a licensed feature is checked; opening, viewing and saving always work.
/// </summary>
public static class LicensePolicy
{
    /// <summary>The last day of the introductory period as it stands in the license; it can be extended (section 3 (5)), never shortened.</summary>
    public static readonly DateOnly IntroductionEnd = new(2027, 12, 31);

    /// <summary>After the introductory period: how long a commercial use can try the licensed features, from its first start.</summary>
    public const int TrialMonths = 3;

    /// <summary>
    /// The features that need a license — this one line decides it, before a release: a feature taken out is free for everybody (no lock,
    /// not named in any notice). Compiled in; nothing outside the program changes it.
    /// </summary>
    public const LicensedFeature Licensed = LicensedFeature.Redaction | LicensedFeature.Measuring | LicensedFeature.CertificateSigning
        | LicensedFeature.PdfA | LicensedFeature.Scanning | LicensedFeature.Forms | LicensedFeature.Editing | LicensedFeature.Restrictions;

    /// <summary>Whether any feature needs a license (false when <see cref="Licensed"/> was emptied before a release).</summary>
    public static bool AnyLicensed => Licensed != LicensedFeature.None;

    /// <summary>In the last days before the end, the program also reminds when a licensed feature is started.</summary>
    public const int ReminderDays = 30;

    public static bool IsFreeUse(UsageKind usage) => usage is UsageKind.Private or UsageKind.NonProfit or UsageKind.Education;

    /// <summary>The end of the introductory period: the one of the license or a later one announced in the signed version file.</summary>
    /// <param name="extended">The announced end as yyyy-MM-dd; null or unreadable = none.</param>
    public static DateOnly IntroductionEndFor(string? extended) =>
        DateOnly.TryParseExact(extended, "yyyy-MM-dd", out var day) && day > IntroductionEnd ? day : IntroductionEnd;

    /// <summary>The day that counts: today, or the latest day the program ran when the clock was turned back.</summary>
    /// <param name="today">Today by the clock.</param>
    /// <param name="latestSeen">The latest day the program ran, as yyyy-MM-dd; null = none.</param>
    public static DateOnly CountingDay(DateOnly today, string? latestSeen) =>
        DateOnly.TryParseExact(latestSeen, "yyyy-MM-dd", out var seen) && seen > today ? seen : today;

    /// <summary>The last free day: the end of the introductory period, or of the trial when that ends later.</summary>
    /// <param name="introductionEnd">The end of the introductory period (<see cref="IntroductionEndFor"/>).</param>
    /// <param name="commercialSince">The first day of a commercial use on this computer; null = none yet.</param>
    public static DateOnly FreeUntil(DateOnly introductionEnd, DateOnly? commercialSince)
    {
        if (commercialSince is not { } since) return introductionEnd;
        var trialEnd = since.AddMonths(TrialMonths).AddDays(-1);
        return trialEnd > introductionEnd ? trialEnd : introductionEnd;
    }

    /// <summary>Days of the free time left, counting today; 0 after its end.</summary>
    public static int DaysLeft(DateOnly today, DateOnly end) => Math.Max(0, end.DayNumber - today.DayNumber + 1);

    /// <summary>Whether a feature may be started.</summary>
    public static GateResult Evaluate(LicensedFeature feature, LicenseContext context)
    {
        var daysLeft = DaysLeft(context.Today, context.FreeUntil);
        if (IsFreeUse(context.Usage) || (feature & Licensed) == 0) return new GateResult(GateState.Free, KeyProblem.None, daysLeft, null);
        var code = LicenseCodec.Decode(context.Key);
        var problem = Check(code, feature, context);
        if (problem == KeyProblem.None) return new GateResult(GateState.Licensed, KeyProblem.None, daysLeft, code);
        return new GateResult(daysLeft > 0 ? GateState.Introductory : GateState.Locked, problem, daysLeft, code);
    }

    /// <summary>Why a key does not unlock a feature on this computer and in this version; <see cref="KeyProblem.None"/> when it does.</summary>
    public static KeyProblem Check(LicenseCode? code, LicensedFeature feature, LicenseContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Key)) return KeyProblem.NoKey;
        if (code is null) return KeyProblem.Unreadable;
        if (context.Today > code.ExpiresOn) return KeyProblem.Expired;
        if (context.ReleaseDate > code.UpdatesUntil) return KeyProblem.VersionNotCovered;
        if ((code.Features & feature) != feature) return KeyProblem.FeatureMissing;
        if (code.BindsEmail && string.IsNullOrWhiteSpace(context.Email)) return KeyProblem.EmailMissing;
        if (code.BindsEmail && !LicenseCodec.Matches([context.Email], BindingKind.Email, code.EmailHash)) return KeyProblem.BindingMismatch;
        if (code.BindsHostname && !LicenseCodec.Matches(context.HostNames, BindingKind.Hostname, code.HostnameHash)) return KeyProblem.BindingMismatch;
        if (code.BindsDomain && !LicenseCodec.Matches(context.Domains, BindingKind.Domain, code.DomainHash)) return KeyProblem.BindingMismatch;
        return KeyProblem.None;
    }

    /// <summary>Whether a version released on <paramref name="releaseDate"/> is still covered by the key (for the warning before an update).</summary>
    public static bool Covers(LicenseCode code, DateOnly releaseDate) => releaseDate <= code.UpdatesUntil;

    /// <summary>The names of this computer: the machine name and the full DNS name.</summary>
    public static IReadOnlyList<string> ComputerNames()
    {
        var names = new List<string> { Environment.MachineName };
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            if (properties.HostName.Length > 0) names.Add(properties.HostName);
            if (properties.HostName.Length > 0 && properties.DomainName.Length > 0) names.Add(properties.HostName + "." + properties.DomainName);
        }
        catch (NetworkInformationException)
        {
            // the machine name alone
        }
        return names;
    }

    /// <summary>The domains of this computer: the DNS domain and, under Windows, the logon domain.</summary>
    public static IReadOnlyList<string> ComputerDomains()
    {
        var domains = new List<string>();
        try
        {
            var dns = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (dns.Length > 0) domains.Add(dns);
        }
        catch (NetworkInformationException)
        {
            // no DNS domain
        }
        // without a domain, Windows reports the machine name as logon domain: that is no domain
        if (OperatingSystem.IsWindows() && !string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            domains.Add(Environment.UserDomainName);
        return domains;
    }
}

/// <summary>The day this version was released: written into the program when it is built (Directory.Build.targets, PdfKneadReleaseDate).</summary>
public static class ReleaseInfo
{
    /// <summary>The release day; the day of the build when the program carries none.</summary>
    public static DateOnly Date { get; } = Read();

    private static DateOnly Read()
    {
        foreach (var meta in typeof(ReleaseInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (meta.Key == "PdfKneadReleaseDate" && DateOnly.TryParseExact(meta.Value, "yyyy-MM-dd", out var day)) return day;
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }
}
