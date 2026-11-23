using System.Text;
using PdfKnead.Core.Policy;

namespace PdfKnead.Tests.Licensing;

/// <summary>
/// The function file of an organization: written and read back, later functions kept, a damaged file never read as „everything allowed“,
/// and a catalog in which every function has one stable id.
/// </summary>
public sealed class FeaturePolicyTests
{
    [Fact]
    public void Catalog_IdsAreUniqueAndStable()
    {
        var ids = PolicyCatalog.All.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // „tab.function“ in lower case: the ids stand in the files of the organizations and must never change
        Assert.All(ids, id => Assert.Matches("^[a-z]+\\.[a-z-]+$", id));
        Assert.All(PolicyCatalog.Tabs, t => Assert.NotEmpty(t.Functions));
        // a part names a function of the same tab that stands before it
        foreach (var (_, functions) in PolicyCatalog.Tabs)
            for (var i = 0; i < functions.Length; i++)
                if (functions[i].Parent is { } parent) Assert.Contains(functions[..i], f => f.Id == parent);
    }

    /// <summary>
    /// A part of a function (decided by the user on 22.11.2026: „Ohne Rechte-Passwort entfernen“ under „Einschränkungen entfernen“) is
    /// allowed only while its parent is: denied itself, or with the parent denied, it is off; the parent does not depend on its part.
    /// </summary>
    [Fact]
    public void Part_AllowedOnlyWithItsParent()
    {
        const string parent = "secure.remove-restrictions", part = "secure.remove-restrictions-without-password";
        Assert.Equal(parent, PolicyCatalog.Find(part)?.Parent);
        Assert.True(new FeaturePolicy().Allows(part));
        Assert.False(new FeaturePolicy { Denied = new HashSet<string> { part } }.Allows(part));
        Assert.True(new FeaturePolicy { Denied = new HashSet<string> { part } }.Allows(parent));
        Assert.False(new FeaturePolicy { Denied = new HashSet<string> { parent } }.Allows(part));
    }

    [Fact]
    public void File_RoundTrip_KeepsEverything()
    {
        var policy = new FeaturePolicy
        {
            Usage = PolicyUsage.Authority,
            Organization = "Musteramt – IT",
            Denied = new HashSet<string> { "start.print", "secure.remove-restrictions", "zukunft.neu" },
        };
        var read = FeaturePolicy.Parse(policy.ToJson());
        Assert.NotNull(read);
        Assert.Equal(PolicyUsage.Authority, read!.Usage);
        Assert.Equal("Musteramt – IT", read.Organization);
        Assert.True(read.Denied.SetEquals(policy.Denied));
        Assert.False(read.Allows("start.print"));
        Assert.True(read.Allows("start.save"));
        // a function of a later version that this one does not know: allowed here, kept in the file
        Assert.True(read.Allows("start.unbekannt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein json")]
    [InlineData("[]")]
    [InlineData("{\"denied\": []}")]
    [InlineData("{\"format\": 2, \"denied\": []}")]
    [InlineData("{\"format\": 1, \"usage\": \"privat\"}")]
    [InlineData("{\"format\": 1, \"denied\": \"start.print\"}")]
    [InlineData("{\"format\": 1, \"denied\": [1]}")]
    public void File_NotAFunctionFile_IsRefused(string text) => Assert.Null(FeaturePolicy.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Install_LaysOnlyAFunctionFileBesideTheProgram()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdfknead-einspielen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "von-der-it.json");
            File.WriteAllText(source, "kein json");
            Assert.Equal(PolicyInstallResult.NotAFunctionFile, FeaturePolicy.Install(source, folder).Result);
            Assert.Equal(PolicyLoadState.NotThere, FeaturePolicy.Load(folder).State);
            File.WriteAllBytes(source, new FeaturePolicy { Denied = new HashSet<string> { "start.print" } }.ToJson());
            var (result, target) = FeaturePolicy.Install(source, folder);
            Assert.Equal(PolicyInstallResult.Installed, result);
            Assert.Equal(Path.Combine(folder, FeaturePolicy.FileName), target);
            Assert.False(FeaturePolicy.Load(folder).Policy!.Allows("start.print"));
            Assert.False(File.Exists(target + ".neu"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Load_TellsMissingFromDamaged()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdfknead-richtlinie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(PolicyLoadState.NotThere, FeaturePolicy.Load(folder).State);
            var path = Path.Combine(folder, FeaturePolicy.FileName);
            File.WriteAllText(path, "{ beschädigt");
            Assert.Equal(PolicyLoadState.Unreadable, FeaturePolicy.Load(folder).State);
            File.WriteAllBytes(path, new FeaturePolicy { Denied = new HashSet<string> { "form.all" } }.ToJson());
            var load = FeaturePolicy.Load(folder);
            Assert.Equal(PolicyLoadState.Loaded, load.State);
            Assert.False(load.Policy!.Allows("form.all"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
