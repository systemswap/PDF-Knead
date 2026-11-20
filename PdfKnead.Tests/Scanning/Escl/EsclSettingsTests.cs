using PdfKnead.Core.Platform;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>The settings of the eSCL access in the computer's settings file (KONZEPT.md §5.25, S9d).</summary>
public sealed class EsclSettingsTests
{
    [Fact]
    public void SavedAndLoaded_OnlyWhatWasEntered()
    {
        // Fehlergedächtnis 953: the derived lists were written too, and writing an address threw — saving failed
        var folder = Path.Combine(Path.GetTempPath(), "pdfknead-escl-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "settings.json");
        try
        {
            var settings = new AppSettings(ScanNetwork: new EsclSettings(false, ["192.168.178.0/24", "kaputt"], ["https://192.168.178.20", "scanner.local:8080"])
                .WithCertificate("11111111-2222-3333-4444-555555555555", "AB12"));
            settings.Save(file);
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Ranges", text);
            Assert.DoesNotContain("ManualAddresses", text);
            var loaded = AppSettings.Load(file).ScanNetwork!;
            Assert.False(loaded.Direct);
            Assert.Equal(["192.168.178.0/24", "kaputt"], loaded.Networks);
            Assert.Equal(["192.168.178.0/24"], loaded.Ranges.Select(r => r.ToString()));   // what is no longer valid is left out when used
            Assert.Equal(2, loaded.ManualAddresses.Count);
            Assert.Equal("AB12", loaded.CertificateOf("11111111-2222-3333-4444-555555555555"));
            Assert.Null(loaded.CertificateOf("anderes"));
            // a new confirmation replaces the old one
            Assert.Equal("CD34", loaded.WithCertificate("11111111-2222-3333-4444-555555555555", "CD34").CertificateOf("11111111-2222-3333-4444-555555555555"));
            Assert.Single(loaded.WithCertificate("11111111-2222-3333-4444-555555555555", "CD34").Certificates!);
            // without the entry: the defaults
            Assert.True(new AppSettings().ScanNetwork is null);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fehlergedächtnis 926: a scanner taken from the network search is saved with its address; from a settings file edited by hand only
    /// valid entries count, each device once and at most <see cref="EsclSettings.MaxEntries"/>.
    /// </summary>
    [Fact]
    public void ChosenScanners_SavedWithAddress_OnlyValidOnes()
    {
        var found = new EsclDevice("aaaaaaaa-1111-2222-3333-444444444444", "Beispiel Etage 2", [
            new EsclService("Beispiel Etage 2", true, "10.242.66.35", 443, [System.Net.IPAddress.Parse("10.242.66.35")],
                new Dictionary<string, string> { ["uuid"] = "aaaaaaaa-1111-2222-3333-444444444444" }),
            new EsclService("Beispiel Etage 2", false, "10.242.66.35", 8080, [System.Net.IPAddress.Parse("10.242.66.35")],
                new Dictionary<string, string> { ["uuid"] = "aaaaaaaa-1111-2222-3333-444444444444", ["rs"] = "scan" })]);
        var chosen = EsclChosenScanner.Of(found)!;
        Assert.Equal("https://10.242.66.35:443/eSCL", chosen.Address);   // the service with TLS first
        Assert.Null(EsclChosenScanner.Of(found with { Services = [] }));

        var folder = Path.Combine(Path.GetTempPath(), "pdfknead-escl-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "settings.json");
        try
        {
            new AppSettings(ScanNetwork: new EsclSettings().WithChosen(chosen, true)).Save(file);
            Assert.DoesNotContain("ChosenScanners", File.ReadAllText(file));
            var loaded = AppSettings.Load(file).ScanNetwork!;
            Assert.Equal(chosen, Assert.Single(loaded.ChosenScanners).Scanner);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        // edited by hand: an invalid address, an empty or too long device, the same device twice — only valid ones, the first of a device
        var edited = new EsclSettings(Chosen: [
            chosen,
            chosen with { Name = "zweimal" },
            new EsclChosenScanner("bbbbbbbb-1111-2222-3333-444444444444", "kaputt", "nicht gültig/"),
            new EsclChosenScanner("", "leer", "http://10.242.66.40:80/eSCL"),
            new EsclChosenScanner(new string('c', 200), "lang", "http://10.242.66.41:80/eSCL"),
            .. Enumerable.Range(1, 100).Select(i => new EsclChosenScanner($"d{i}", $"Gerät {i}", $"http://10.242.67.{i}:80/eSCL"))]);
        var valid = edited.ChosenScanners;
        Assert.Equal(chosen, valid[0].Scanner);
        Assert.DoesNotContain(valid, c => c.Scanner.Name is "zweimal" or "kaputt" or "leer" or "lang");
        Assert.True(valid.Count < EsclSettings.MaxEntries);   // only the first 64 entries are read at all

        // taken out and in again: once, the newest address
        var again = new EsclSettings().WithChosen(chosen, true).WithChosen(chosen with { Address = "https://10.242.66.40:443/eSCL" }, true);
        Assert.Equal("https://10.242.66.40:443/eSCL", Assert.Single(again.ChosenScanners).Scanner.Address);
        Assert.Empty(again.WithChosen(chosen, false).ChosenScanners);
    }
}
