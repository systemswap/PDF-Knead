using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Services;

/// <summary>
/// The scanner interface of the program: the system's (SANE or WIA) together with the own eSCL access (KONZEPT §5.25, S9d), one per run — its
/// diagnosis log and the devices for which eSCL is disturbed last until the program ends. Settings and confirmed certificates live in the
/// user's settings.
/// </summary>
public static class NetworkScanning
{
    private static readonly Lazy<CombinedScanBackend> Combined = new(() =>
        new CombinedScanBackend(ScanBackends.ForThisSystem(), new EsclBackend(() => Settings, new SettingsPins())));

    /// <summary>The scanner interface for the scan window.</summary>
    public static IScanBackend Backend() => Combined.Value;

    /// <summary>The settings of the eSCL access as saved now.</summary>
    public static EsclSettings Settings => UserSettings.Current.ScanNetwork ?? new EsclSettings();

    /// <summary>Changes the settings of the eSCL access and saves them.</summary>
    public static void Update(Func<EsclSettings, EsclSettings> change) =>
        UserSettings.Update(s => s with { ScanNetwork = change(s.ScanNetwork ?? new EsclSettings()) });

    // the confirmed certificates, read at every connection (possibly off the user interface thread: the settings are an immutable record)
    private sealed class SettingsPins : IEsclCertificatePins
    {
        public string? Get(string key) => Settings.CertificateOf(key);

        public void Set(string key, string fingerprint) => Update(s => s.WithCertificate(key, fingerprint));
    }
}
