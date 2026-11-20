using PdfKnead.Core.Licensing;
using PdfKnead.Core.Updates;

namespace PdfKnead.Tests.Licensing;

/// <summary>
/// The license key and the rules of LICENSE, sections 2 and 3: non-commercial use always free; commercial use free during the introductory
/// period, then only with a matching key; a key works without a time limit in every version released up to the end of its update period.
/// </summary>
public sealed class LicensingTests
{
    private static readonly DateOnly Released = new(2028, 6, 1);

    private static LicenseContext Context(UsageKind usage, string? key = null, DateOnly? today = null, string? email = null,
        DateOnly? released = null, string[]? hosts = null, string[]? domains = null) =>
        new(usage, key, email, today ?? new DateOnly(2027, 1, 1), released ?? Released, LicensePolicy.IntroductionEnd,
            hosts ?? ["arbeitsplatz7"], domains ?? ["musterfirma.local"]);

    private static string Key(LicenseCode code) => LicenseCodec.Encode(code);

    [Fact]
    public void Codec_RoundTrip_AllFields()
    {
        var code = new LicenseCode
        {
            ExpiresOn = new DateOnly(2031, 3, 31),
            UpdatesUntil = new DateOnly(2029, 12, 31),
            BindsEmail = true,
            EmailHash = LicenseCodec.HashBinding("Einkauf@Musterfirma.example", BindingKind.Email),
            BindsDomain = true,
            DomainHash = LicenseCodec.HashBinding("musterfirma.local", BindingKind.Domain),
            Serial = 70001,
            Kind = LicenseKind.Trial,
            Features = LicensedFeature.Redaction | LicensedFeature.PdfA,
            Company = "Musterfirma GmbH & Co. KG — Büro Süd",
        };
        var text = Key(code);
        Assert.Matches("^[A-HJKMNP-TV-Z0-9]{5}(-[A-HJKMNP-TV-Z0-9]{1,5})+$", text);
        var read = LicenseCodec.Decode(text);
        Assert.NotNull(read);
        Assert.Equal(code with { BindsHostname = false, HostnameHash = read!.HostnameHash }, read);
    }

    [Fact]
    public void Codec_AllFeatures_SurviveTheKey()
    {
        var read = LicenseCodec.Decode(Key(new LicenseCode { Features = LicensedFeature.All }));
        Assert.Equal(LicensedFeature.All, read?.Features);
        var high = LicenseCodec.Decode(Key(new LicenseCode { Features = LicensedFeature.Restrictions | LicensedFeature.Editing }));
        Assert.Equal(LicensedFeature.Restrictions | LicensedFeature.Editing, high?.Features);
    }

    [Fact]
    public void EveryFeature_StandsInExactlyOneTab()
    {
        var listed = LicensedFeatureTabs.Groups.SelectMany(g => g.Features).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(LicensedFeature.All, listed.Aggregate(LicensedFeature.None, (all, f) => all | f));
        Assert.All(listed, f => Assert.True(System.Numerics.BitOperations.PopCount((uint)f) == 1, $"{f} is not a single feature"));
    }

    [Fact]
    public void Codec_TypingHelps_ButErrorsAreFound()
    {
        var text = Key(new LicenseCode { Company = "Muster AG", Serial = 5 });
        // lower case, spaces instead of dashes, the confusable letters O/I/L/U for 0/1/1/V
        var sloppy = text.ToLowerInvariant().Replace('-', ' ').Replace('0', 'o').Replace('1', 'l').Replace('v', 'u');
        Assert.Equal(5, LicenseCodec.Decode(sloppy)?.Serial);
        // one character changed: refused
        var i = text.IndexOf(text.First(char.IsLetter));
        var changed = text[..i] + (text[i] == 'A' ? 'B' : 'A') + text[(i + 1)..];
        Assert.Null(LicenseCodec.Decode(changed));
        Assert.Null(LicenseCodec.Decode(text[..^3]));
        Assert.Null(LicenseCodec.Decode(""));
        Assert.Null(LicenseCodec.Decode("111-1111-1111-111"));
        Assert.Null(LicenseCodec.Decode(new string('A', 5000)));
    }

    [Fact]
    public void Codec_UnboundFieldsAreRandom_SoKeysDiffer()
    {
        var code = new LicenseCode { Company = "Muster AG" };
        Assert.NotEqual(Key(code), Key(code));
    }

    [Fact]
    public void Codec_RefusesTooLongLicenseeAndDatesOutside()
    {
        Assert.Throws<ArgumentException>(() => Key(new LicenseCode { Company = new string('x', LicenseCodec.MaxCompanyBytes + 1) }));
        Assert.Throws<ArgumentException>(() => Key(new LicenseCode { UpdatesUntil = new DateOnly(2019, 12, 31) }));
    }

    [Theory]
    [InlineData(UsageKind.Private)]
    [InlineData(UsageKind.NonProfit)]
    [InlineData(UsageKind.Education)]
    public void FreeUse_AlwaysFree_AlsoAfterTheIntroductoryPeriod(UsageKind usage)
    {
        var result = LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(usage, today: new DateOnly(2035, 1, 1)));
        Assert.Equal(GateState.Free, result.State);
        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData(UsageKind.Commercial)]
    [InlineData(UsageKind.Authority)]
    [InlineData(UsageKind.NotAsked)]
    public void CommercialUse_FreeUntilTheEnd_ThenLocked(UsageKind usage)
    {
        var lastDay = LicensePolicy.Evaluate(LicensedFeature.Scanning, Context(usage, today: LicensePolicy.IntroductionEnd));
        Assert.Equal(GateState.Introductory, lastDay.State);
        Assert.Equal(1, lastDay.DaysLeft);
        var after = LicensePolicy.Evaluate(LicensedFeature.Scanning, Context(usage, today: LicensePolicy.IntroductionEnd.AddDays(1)));
        Assert.Equal(GateState.Locked, after.State);
        Assert.Equal(KeyProblem.NoKey, after.Problem);
        Assert.False(after.Allowed);
    }

    [Fact]
    public void DaysLeft_CountsToday()
    {
        Assert.Equal(407, LicensePolicy.DaysLeft(new DateOnly(2026, 11, 20), new DateOnly(2027, 12, 31)));
        Assert.Equal(0, LicensePolicy.DaysLeft(new DateOnly(2029, 1, 1), new DateOnly(2028, 12, 31)));
    }

    [Fact]
    public void Key_UnlocksForEverInCoveredVersions_NotInLaterOnes()
    {
        var key = Key(new LicenseCode { UpdatesUntil = new DateOnly(2030, 6, 30), Company = "Muster AG" });
        var late = new DateOnly(2045, 1, 1);
        Assert.Equal(GateState.Licensed, LicensePolicy.Evaluate(LicensedFeature.PdfA, Context(UsageKind.Commercial, key, late, released: new DateOnly(2030, 6, 30))).State);
        var newer = LicensePolicy.Evaluate(LicensedFeature.PdfA, Context(UsageKind.Commercial, key, late, released: new DateOnly(2030, 7, 1)));
        Assert.Equal(GateState.Locked, newer.State);
        Assert.Equal(KeyProblem.VersionNotCovered, newer.Problem);
        // during the introductory period a newer version stays free, the problem is still reported
        var early = LicensePolicy.Evaluate(LicensedFeature.PdfA, Context(UsageKind.Commercial, key, new DateOnly(2027, 1, 1), released: new DateOnly(2030, 7, 1)));
        Assert.Equal(GateState.Introductory, early.State);
        Assert.Equal(KeyProblem.VersionNotCovered, early.Problem);
    }

    [Fact]
    public void Key_Trial_EndsOnItsLastDay()
    {
        var key = Key(new LicenseCode { Kind = LicenseKind.Trial, ExpiresOn = new DateOnly(2029, 3, 31) });
        Assert.Equal(GateState.Licensed, LicensePolicy.Evaluate(LicensedFeature.Measuring, Context(UsageKind.Commercial, key, new DateOnly(2029, 3, 31))).State);
        var after = LicensePolicy.Evaluate(LicensedFeature.Measuring, Context(UsageKind.Commercial, key, new DateOnly(2029, 4, 1)));
        Assert.Equal(KeyProblem.Expired, after.Problem);
        Assert.False(after.Allowed);
    }

    [Fact]
    public void Key_OnlyItsFeatures()
    {
        var key = Key(new LicenseCode { Features = LicensedFeature.Scanning });
        var today = new DateOnly(2030, 1, 1);
        Assert.True(LicensePolicy.Evaluate(LicensedFeature.Scanning, Context(UsageKind.Commercial, key, today)).Allowed);
        var other = LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, key, today));
        Assert.Equal(KeyProblem.FeatureMissing, other.Problem);
        Assert.False(other.Allowed);
    }

    [Fact]
    public void Key_Bindings_EmailHostAndDomain()
    {
        var today = new DateOnly(2030, 1, 1);
        var email = Key(new LicenseCode { BindsEmail = true, EmailHash = LicenseCodec.HashBinding("it@musterfirma.example", BindingKind.Email) });
        Assert.Equal(KeyProblem.EmailMissing, LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, email, today)).Problem);
        Assert.True(LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, email, today, " IT@Musterfirma.example ")).Allowed);
        Assert.Equal(KeyProblem.BindingMismatch,
            LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, email, today, "andere@musterfirma.example")).Problem);

        // a key for the short host name fits the full name; one for the full domain does not fit another ending
        var host = Key(new LicenseCode { BindsHostname = true, HostnameHash = LicenseCodec.HashBinding("arbeitsplatz7", BindingKind.Hostname) });
        Assert.True(LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, host, today, hosts: ["ARBEITSPLATZ7.musterfirma.local"])).Allowed);
        Assert.False(LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, host, today, hosts: ["arbeitsplatz8"])).Allowed);
        var domain = Key(new LicenseCode { BindsDomain = true, DomainHash = LicenseCodec.HashBinding("musterfirma.de", BindingKind.Domain) });
        Assert.False(LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, domain, today, domains: ["musterfirma.local"])).Allowed);
        Assert.True(LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, domain, today, domains: ["musterfirma.de"])).Allowed);
    }

    [Fact]
    public void UnreadableKey_SaysSo()
    {
        var result = LicensePolicy.Evaluate(LicensedFeature.Redaction, Context(UsageKind.Commercial, "ABCDE-FGHJK", new DateOnly(2030, 1, 1)));
        Assert.Equal(KeyProblem.Unreadable, result.Problem);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Clock_TurnedBack_GivesNoDaysBack()
    {
        Assert.Equal(new DateOnly(2029, 2, 1), LicensePolicy.CountingDay(new DateOnly(2028, 1, 1), "2029-02-01"));
        Assert.Equal(new DateOnly(2028, 1, 1), LicensePolicy.CountingDay(new DateOnly(2028, 1, 1), "2027-05-05"));
        Assert.Equal(new DateOnly(2028, 1, 1), LicensePolicy.CountingDay(new DateOnly(2028, 1, 1), "kaputt"));
    }

    [Fact]
    public void Trial_ThreeMonthsFromTheFirstCommercialStart_OnlyAfterTheIntroduction()
    {
        // started during the introductory period: its end counts
        Assert.Equal(LicensePolicy.IntroductionEnd, LicensePolicy.FreeUntil(LicensePolicy.IntroductionEnd, new DateOnly(2027, 3, 1)));
        // started after it: three months from the first commercial day
        Assert.Equal(new DateOnly(2028, 8, 31), LicensePolicy.FreeUntil(LicensePolicy.IntroductionEnd, new DateOnly(2028, 6, 1)));
        // an introductory period extended to 2099: the trial never comes into play
        Assert.Equal(new DateOnly(2099, 12, 31), LicensePolicy.FreeUntil(new DateOnly(2099, 12, 31), new DateOnly(2030, 6, 1)));

        var since = new DateOnly(2028, 6, 1);
        var during = LicensePolicy.Evaluate(LicensedFeature.Scanning, Context(UsageKind.Commercial, today: new DateOnly(2028, 8, 31)) with { CommercialSince = since });
        Assert.Equal(GateState.Introductory, during.State);
        Assert.Equal(1, during.DaysLeft);
        var after = LicensePolicy.Evaluate(LicensedFeature.Scanning, Context(UsageKind.Commercial, today: new DateOnly(2028, 9, 1)) with { CommercialSince = since });
        Assert.Equal(GateState.Locked, after.State);
    }

    [Fact]
    public void OnlyTheLicensedFeatures_AreEverLocked()
    {
        Assert.Equal(LicensePolicy.Licensed, LicensePolicy.Licensed & LicensedFeature.All);
        foreach (LicensedFeature feature in Enum.GetValues<LicensedFeature>())
        {
            if (feature is LicensedFeature.None or LicensedFeature.All) continue;
            var result = LicensePolicy.Evaluate(feature, Context(UsageKind.Commercial, today: new DateOnly(2035, 1, 1)));
            Assert.Equal((LicensePolicy.Licensed & feature) != 0, !result.Allowed);
        }
    }

    [Fact]
    public void IntroductionEnd_ExtendedNeverShortened()
    {
        Assert.Equal(new DateOnly(2029, 12, 31), LicensePolicy.IntroductionEndFor("2029-12-31"));
        Assert.Equal(LicensePolicy.IntroductionEnd, LicensePolicy.IntroductionEndFor("2027-01-01"));
        Assert.Equal(LicensePolicy.IntroductionEnd, LicensePolicy.IntroductionEndFor(null));
        Assert.Equal(LicensePolicy.IntroductionEnd, LicensePolicy.IntroductionEndFor("31.12.2030"));
    }

    [Fact]
    public void VersionFile_CarriesTheExtension_OnlyAsValidDate()
    {
        var manifest = new UpdateManifest(UpdateVersion.Parse("1.3.0")!.Value, "2028-11-01", "", []) { IntroductionUntil = "2029-12-31" };
        Assert.Equal("2029-12-31", UpdateManifest.Parse(manifest.ToJson())?.IntroductionUntil);
        Assert.Null(UpdateManifest.Parse((manifest with { IntroductionUntil = null }).ToJson())?.IntroductionUntil);
        Assert.Null(UpdateManifest.Parse((manifest with { IntroductionUntil = "bald" }).ToJson())?.IntroductionUntil);
        // without the field the file is as before: older signatures and readers are unaffected
        Assert.DoesNotContain("introductionUntil", System.Text.Encoding.UTF8.GetString((manifest with { IntroductionUntil = null }).ToJson()));
    }

    [Fact]
    public void ReleaseDate_IsWrittenIntoTheProgram()
    {
        Assert.True(ReleaseInfo.Date >= new DateOnly(2026, 11, 20));
    }
}
