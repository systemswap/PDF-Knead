using System.Security.Cryptography;
using PdfKnead.Core.Updates;

namespace PdfKnead.Tests.Updates;

/// <summary>
/// <c>pdf-knead release-pruefen</c>: a downloaded version file is checked as the update check of the program checks it — the signature, the
/// file itself — and every package of the folder against its size and SHA-256; a release can so be checked before the program can reach it
/// (docs/UEBERGABE.md, „Veröffentlichung und öffentliches Repository“).
/// </summary>
public sealed class ReleaseVerifyTests
{
    [Fact]
    public void Verify_SignatureVersionFileAndPackages()
    {
        var folder = Directory.CreateTempSubdirectory("release-pruefen").FullName;
        try
        {
            byte[] package = [1, 2, 3, 4, 5];
            const string name = "PDF-Knead-1.2.3-beta-linux-x64.tar.gz";
            File.WriteAllBytes(Path.Combine(folder, name), package);
            var sha = Convert.ToHexStringLower(SHA256.HashData(package));
            var manifest = new UpdateManifest(UpdateVersion.Parse("1.2.3-beta")!.Value, "2026-11-20", "",
                [new UpdatePackage("linux-x64", name, $"{UpdateManifest.ReleasePrefix}v1.2.3-beta/{name}", package.Length, sha)]);
            var json = manifest.ToJson();
            var (secret, publicKey) = UpdateSignature.CreateKey();
            File.WriteAllBytes(Path.Combine(folder, "version.json"), json);
            File.WriteAllBytes(Path.Combine(folder, "version.json.sig"), UpdateSignature.Sign(json, secret));
            Assert.Equal(0, PdfKnead.Cli.ReleaseTools.Verify(folder, publicKey));

            // signed with another key: the program would refuse it
            var (_, otherKey) = UpdateSignature.CreateKey();
            Assert.Equal(1, PdfKnead.Cli.ReleaseTools.Verify(folder, otherKey));

            // a package changed after signing
            File.WriteAllBytes(Path.Combine(folder, name), [1, 2, 3, 4, 6]);
            Assert.Equal(1, PdfKnead.Cli.ReleaseTools.Verify(folder, publicKey));

            // the version file changed after signing
            File.WriteAllBytes(Path.Combine(folder, name), package);
            var changed = (byte[])json.Clone();
            changed[changed.Length / 2] ^= 1;
            File.WriteAllBytes(Path.Combine(folder, "version.json"), changed);
            Assert.Equal(1, PdfKnead.Cli.ReleaseTools.Verify(folder, publicKey));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
