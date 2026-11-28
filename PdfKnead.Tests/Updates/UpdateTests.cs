using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Updates;

namespace PdfKnead.Tests.Updates;

/// <summary>
/// The updates (KONZEPT.md §5.26): versions, the signed version file, the check and the loading against answers in software (no request
/// leaves the test), the waiting area and the exchange with the previous version. Every rule against an attacker is tried: an unsigned or
/// differently signed file, an older version, a package address outside the releases, redirections elsewhere, a wrong size or checksum,
/// paths out of the waiting area, links in the package.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pdfknead-update-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static UpdateVersion V(string text) => UpdateVersion.Parse(text)!.Value;

    [Theory]
    [InlineData("1.2.172-beta", "1.2.171-beta", true)]
    [InlineData("1.3.0", "1.3.0-beta", true)]       // the release after its beta
    [InlineData("1.3.0-beta", "1.3.0", false)]
    [InlineData("1.10.0", "1.9.9", true)]           // numbers, not text
    [InlineData("1.2.171-beta", "1.2.171-beta", false)]
    [InlineData("1.2", "1.2.0", false)]
    public void Versions_Compare(string a, string b, bool newer) => Assert.Equal(newer, V(a).IsNewerThan(V(b)));

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-be ta")]
    [InlineData("1234567890.1")]
    public void Versions_Invalid(string text) => Assert.Null(UpdateVersion.Parse(text));

    private static UpdatePackage Package(string platform, byte[] data, string version = "9.9.9")
    {
        var name = $"PDF-Knead-{version}-{platform}" + (platform.StartsWith("linux", StringComparison.Ordinal) ? ".tar.gz" : ".zip");
        return new UpdatePackage(platform, name, $"{UpdateManifest.ReleasePrefix}v{version}/{name}", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    [Fact]
    public void Manifest_RoundTrip_AndEveryRuleOnAddresses()
    {
        var manifest = new UpdateManifest(V("9.9.9"), "2026-11-18", "Neu: alles.", [Package("windows-x64", [1, 2, 3])]);
        var read = UpdateManifest.Parse(manifest.ToJson())!;
        Assert.Equal("9.9.9", read.Version.ToString());
        Assert.Equal(manifest.Packages[0], read.Packages[0]);
        string Json(string url, string file = "PDF-Knead-9.9.9-windows-x64.zip", string sha = "", long size = 3) =>
            $$"""{"version":"9.9.9","packages":[{"platform":"windows-x64","file":"{{file}}","url":"{{url}}","size":{{size}},"sha256":"{{(sha.Length > 0 ? sha : new string('a', 64))}}"}]}""";
        var good = UpdateManifest.ReleasePrefix + "v9.9.9/PDF-Knead-9.9.9-windows-x64.zip";
        Assert.NotNull(UpdateManifest.Parse(Encoding.UTF8.GetBytes(Json(good))));
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes(Json("https://example.org/PDF-Knead-9.9.9-windows-x64.zip"))));   // another server
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes(Json(UpdateManifest.ReleasePrefix + "../x/PDF-Knead-9.9.9-windows-x64.zip"))));
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes(Json(good, file: "../PDF-Knead.exe"))));
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes(Json(good, sha: new string('G', 64)))));
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes(Json(good, size: UpdateManifest.MaxPackage + 1))));
        Assert.Null(UpdateManifest.Parse(new byte[UpdateManifest.MaxBytes + 1]));
        Assert.Null(UpdateManifest.Parse("[1,2]"u8));
        Assert.Equal("ab", UpdateManifest.CleanNotes("a" + (char)0x202E + "b"));
    }

    [Fact]
    public void Signature_OnlyTheKeyOfTheProgram()
    {
        var (secret, publicKey) = UpdateSignature.CreateKey();
        var (otherSecret, _) = UpdateSignature.CreateKey();
        var data = "version"u8.ToArray();
        var signature = UpdateSignature.Sign(data, secret);
        Assert.True(UpdateSignature.Verify(data, signature, publicKey));
        Assert.False(UpdateSignature.Verify("versioN"u8, signature, publicKey));                  // one byte changed
        Assert.False(UpdateSignature.Verify(data, UpdateSignature.Sign(data, otherSecret), publicKey));
        Assert.False(UpdateSignature.Verify(data, "kein base64"u8, publicKey));
        Assert.False(UpdateSignature.Verify(data, new byte[UpdateSignature.MaxBytes + 1], publicKey));
        Assert.False(UpdateSignature.Verify(data, signature, ""));                                // no key: never
    }

    /// <summary>The key built into the program is a P-256 public key; with it, nothing signed by another key passes.</summary>
    [Fact]
    public void ReleaseKey_IsAP256PublicKey()
    {
        Assert.NotEmpty(UpdateSignature.ReleaseKey);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateSignature.ReleaseKey), out _);
        Assert.Equal(256, ecdsa.KeySize);
        var (other, _) = UpdateSignature.CreateKey();
        Assert.False(UpdateSignature.Verify("version"u8, UpdateSignature.Sign("version"u8, other)));
    }

    /// <summary>Answers in software: address → (status, body, redirection).</summary>
    private sealed class Server : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode Status, byte[]? Body, string? Location)> Answers = [];
        public readonly List<string> Asked = [];
        public string? UserAgent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            UserAgent = request.Headers.UserAgent.ToString();
            if (!Answers.TryGetValue(url, out var answer)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(answer.Status) { Content = new ByteArrayContent(answer.Body ?? []) };
            if (answer.Location is { } location) response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }

    private static (Server Server, string PublicKey, byte[] Package) Release(string version, string platform = "windows-x64", string? sign = null)
    {
        var (secret, publicKey) = UpdateSignature.CreateKey();
        var package = Encoding.UTF8.GetBytes("Paket " + version);
        var manifest = new UpdateManifest(V(version), "2026-11-18", "Änderungen", [Package(platform, package, version)]).ToJson();
        var server = new Server();
        server.Answers[UpdateClient.ManifestUrl] = (HttpStatusCode.OK, manifest, null);
        server.Answers[UpdateClient.SignatureUrl] = (HttpStatusCode.OK, UpdateSignature.Sign(manifest, sign ?? secret), null);
        return (server, publicKey, package);
    }

    [Fact]
    public async Task Check_NewerSignedVersion_Available_TheRestNot()
    {
        var current = V("1.2.171-beta");
        var (server, key, _) = Release("1.2.180-beta");
        var client = new UpdateClient(server);
        var found = await client.CheckAsync(current, "windows-x64", key);
        Assert.Equal(UpdateCheckOutcome.Available, found.Outcome);
        Assert.Equal("1.2.180-beta", found.Manifest!.Version.ToString());
        Assert.StartsWith("PDF-Knead/", server.UserAgent);                                         // the program name, nothing more
        Assert.Equal([UpdateClient.ManifestUrl, UpdateClient.SignatureUrl], server.Asked);         // only the two files
        // no package for this platform, an older or the same version: nothing to do
        Assert.Equal(UpdateCheckOutcome.Current, (await client.CheckAsync(current, "linux-x64", key)).Outcome);
        Assert.Equal(UpdateCheckOutcome.Current, (await client.CheckAsync(V("1.2.180-beta"), "windows-x64", key)).Outcome);
        var older = Release("1.2.100-beta");
        Assert.Equal(UpdateCheckOutcome.Current, (await new UpdateClient(older.Server).CheckAsync(current, "windows-x64", older.PublicKey)).Outcome);
        // signed with another key, or by nobody: ignored
        var (_, otherSecretPublic) = UpdateSignature.CreateKey();
        Assert.Equal(UpdateCheckOutcome.Invalid, (await client.CheckAsync(current, "windows-x64", otherSecretPublic)).Outcome);
        server.Answers.Remove(UpdateClient.SignatureUrl);
        Assert.Equal(UpdateCheckOutcome.Unreachable, (await client.CheckAsync(current, "windows-x64", key)).Outcome);
        // the repository not public (yet): „not found“ for the version file — quietly nothing
        Assert.Equal(UpdateCheckOutcome.Unreachable, (await new UpdateClient(new Server()).CheckAsync(current, "windows-x64", key)).Outcome);
        // the program without a release key accepts nothing
        Assert.Equal(UpdateCheckOutcome.NoKey, (await client.CheckAsync(current, "windows-x64", "")).Outcome);
    }

    [Fact]
    public async Task Check_RedirectionsOnlyToGitHubOverHttps()
    {
        var (server, key, _) = Release("1.2.180-beta");
        var stored = "https://objects.githubusercontent.com/version.json";
        server.Answers[stored] = server.Answers[UpdateClient.ManifestUrl];
        server.Answers[UpdateClient.ManifestUrl] = (HttpStatusCode.Found, null, stored);
        Assert.Equal(UpdateCheckOutcome.Available, (await new UpdateClient(server).CheckAsync(V("1.0.0"), "windows-x64", key)).Outcome);
        foreach (var elsewhere in new[] { "https://example.org/version.json", "http://objects.githubusercontent.com/version.json", "https://github.com:8443/version.json" })
        {
            server.Answers[elsewhere] = server.Answers[stored];
            server.Answers[UpdateClient.ManifestUrl] = (HttpStatusCode.Found, null, elsewhere);
            server.Asked.Clear();
            Assert.Equal(UpdateCheckOutcome.Unreachable, (await new UpdateClient(server).CheckAsync(V("1.0.0"), "windows-x64", key)).Outcome);
            Assert.DoesNotContain(elsewhere, server.Asked);
        }
    }

    [Fact]
    public async Task Download_ExactSizeAndChecksum_OtherwiseNothing()
    {
        var (server, key, package) = Release("1.2.180-beta");
        var client = new UpdateClient(server);
        var found = await client.CheckAsync(V("1.0.0"), "windows-x64", key);
        var target = Path.Combine(_folder, "paket.zip");
        server.Answers[found.Package!.Url] = (HttpStatusCode.OK, package, null);
        Assert.True(await client.DownloadAsync(found.Package, target));
        Assert.Equal(package, File.ReadAllBytes(target));
        File.Delete(target);
        server.Answers[found.Package.Url] = (HttpStatusCode.OK, [.. package, 0], null);           // one byte more
        Assert.False(await client.DownloadAsync(found.Package, target));
        package[0] ^= 1;
        server.Answers[found.Package.Url] = (HttpStatusCode.OK, package, null);                   // same size, other content
        Assert.False(await client.DownloadAsync(found.Package, target));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_folder, "*.teil"));
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create))
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        return buffer.ToArray();
    }

    [Fact]
    public void Unpack_TakesTheTopFolderOff_RefusesPathsOut()
    {
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand"));
        var file = Path.Combine(_folder, "p.zip");
        File.WriteAllBytes(file, Zip(("windows-x64/PDF-Knead.exe", "neu"), ("windows-x64/ocr/modell.onnx", "m")));
        var files = installer.UnpackPackage(file, V("2.0.0"))!;
        Assert.Equal(["PDF-Knead.exe", "ocr/modell.onnx"], files);
        Assert.Equal("neu", File.ReadAllText(Path.Combine(installer.Staged(V("2.0.0")), "PDF-Knead.exe")));
        foreach (var evil in new[] { "../aussen.txt", "windows-x64/../../aussen.txt", "/abs.txt", "C:/abs.txt", "a//b.txt" })
        {
            File.WriteAllBytes(file, Zip(("windows-x64/PDF-Knead.exe", "neu"), (evil, "böse")));
            Assert.Null(installer.UnpackPackage(file, V("2.0.1")));
            Assert.False(Directory.Exists(installer.Staged(V("2.0.1"))));
        }
        Assert.False(File.Exists(Path.Combine(_folder, "aussen.txt")));
        File.WriteAllBytes(file, [1, 2, 3]);                                                     // not a zip
        Assert.Null(installer.UnpackPackage(file, V("2.0.2")));
    }

    [Fact]
    public void Unpack_TarGz_RegularFilesOnly()
    {
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand"));
        string TarGz(bool withLink)
        {
            var file = Path.Combine(_folder, "p.tar.gz");
            using var stream = File.Create(file);
            using var gzip = new GZipStream(stream, CompressionLevel.Fastest);
            using var tar = new TarWriter(gzip);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "linux-x64/PDF-Knead-x86_64.AppImage") { DataStream = new MemoryStream("neu"u8.ToArray()) });
            if (withLink) tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "linux-x64/link") { LinkName = "/etc/passwd" });
            return file;
        }
        Assert.Equal(["PDF-Knead-x86_64.AppImage"], installer.UnpackPackage(TarGz(false), V("2.0.0")));
        Assert.Null(installer.UnpackPackage(TarGz(true), V("2.0.1")));
    }

    [Fact]
    public void Apply_OnlyThePackageFiles_UserFilesStay_PreviousVersionPutBack()
    {
        var program = Path.Combine(_folder, "programm");
        Directory.CreateDirectory(Path.Combine(program, "languages"));
        File.WriteAllText(Path.Combine(program, "PDF-Knead.exe"), "alt");
        File.WriteAllText(Path.Combine(program, "languages", "eigene.json"), "meins");
        File.WriteAllText(Path.Combine(program, "PDF-Knead.log"), "protokoll");
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand"));
        var file = Path.Combine(_folder, "p.zip");
        File.WriteAllBytes(file, Zip(("windows-x64/PDF-Knead.exe", "neu"), ("windows-x64/ocr/neu.onnx", "m")));
        var files = installer.UnpackPackage(file, V("2.0.0"))!;
        installer.MarkReady(V("2.0.0"), files);
        Assert.Equal("2.0.0", installer.Waiting()!.Value.Version.ToString());

        Assert.True(installer.Apply(program, V("2.0.0"), files));
        Assert.Equal("neu", File.ReadAllText(Path.Combine(program, "PDF-Knead.exe")));
        Assert.Equal("meins", File.ReadAllText(Path.Combine(program, "languages", "eigene.json")));   // what the user put there stays
        Assert.Equal("protokoll", File.ReadAllText(Path.Combine(program, "PDF-Knead.log")));
        Assert.Equal("alt", File.ReadAllText(Path.Combine(installer.Previous, "PDF-Knead.exe")));
        Assert.Null(installer.Waiting());

        // the first start of the new version counts; it did not reach its window, so the next start puts the previous one back
        Assert.False(installer.NeedsRestore());
        installer.CountStart();
        Assert.True(installer.NeedsRestore());
        Assert.True(installer.CheckStart(program));
        Assert.Equal("alt", File.ReadAllText(Path.Combine(program, "PDF-Knead.exe")));
        Assert.False(File.Exists(Path.Combine(program, "ocr", "neu.onnx")));                         // what the update added goes again
        Assert.Equal("meins", File.ReadAllText(Path.Combine(program, "languages", "eigene.json")));
        Assert.False(installer.NeedsRestore());
    }

    [Fact]
    public void Confirm_TheNewVersionStays()
    {
        var program = Path.Combine(_folder, "programm");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "PDF-Knead.exe"), "alt");
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand"));
        var file = Path.Combine(_folder, "p.zip");
        File.WriteAllBytes(file, Zip(("PDF-Knead.exe", "neu")));
        var files = installer.UnpackPackage(file, V("2.0.0"))!;
        Assert.True(installer.Apply(program, V("2.0.0"), files));
        installer.CountStart();
        installer.Confirm();                                                                          // the window opened
        Assert.False(installer.NeedsRestore());
        installer.CountStart();
        Assert.False(installer.NeedsRestore());
        Assert.Equal("neu", File.ReadAllText(Path.Combine(program, "PDF-Knead.exe")));
    }

    [Fact]
    public void Apply_FailsHalfway_EverythingPutBack()
    {
        // the second file cannot be exchanged: under Windows it is held open (another window of the program); elsewhere an open file does not
        // stop a rename, so its folder may not be written instead — either way the first file is already exchanged when the second fails
        var program = Path.Combine(_folder, "programm");
        var second = OperatingSystem.IsWindows() ? "b.dat" : "teile/b.dat";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(program, second))!);
        File.WriteAllText(Path.Combine(program, "PDF-Knead.exe"), "alt");
        File.WriteAllText(Path.Combine(program, second), "alt-b");
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand")) { Patience = TimeSpan.FromMilliseconds(300) };
        var file = Path.Combine(_folder, "p.zip");
        File.WriteAllBytes(file, Zip(("PDF-Knead.exe", "neu"), (second, "neu-b")));
        var files = installer.UnpackPackage(file, V("2.0.0"))!;
        installer.MarkReady(V("2.0.0"), files);
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(Path.Combine(program, second), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.False(installer.Apply(program, V("2.0.0"), files));
        }
        else
        {
            var folder = Path.Combine(program, "teile");
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.False(installer.Apply(program, V("2.0.0"), files));
            }
            finally
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        Assert.Equal("alt", File.ReadAllText(Path.Combine(program, "PDF-Knead.exe")));
        Assert.Equal("alt-b", File.ReadAllText(Path.Combine(program, second)));
        Assert.NotNull(installer.Waiting());                                                           // tried again at the next start
        Assert.StartsWith(second + ": ", installer.LastFailure);                                       // named for the log, without a path
    }

    /// <summary>
    /// A file of the exchange is held open for a moment (a virus scanner reading the fresh files, a sync client of the folder): the exchange
    /// waits and tries again instead of giving up at once — before, the old version was started again without a word
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1265).
    /// </summary>
    [Fact]
    public void Apply_FileHeldForAMoment_ExchangedAfterWaiting()
    {
        var program = Path.Combine(_folder, "programm");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "PDF-Knead.exe"), "alt");
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand"));
        var file = Path.Combine(_folder, "p.zip");
        File.WriteAllBytes(file, Zip(("PDF-Knead.exe", "neu")));
        var files = installer.UnpackPackage(file, V("2.0.0"))!;
        installer.MarkReady(V("2.0.0"), files);
        var held = new FileStream(Path.Combine(installer.Staged(V("2.0.0")), "PDF-Knead.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(700);
            held.Dispose();
        });
        Assert.True(installer.Apply(program, V("2.0.0"), files));
        release.Wait();
        Assert.Equal("neu", File.ReadAllText(Path.Combine(program, "PDF-Knead.exe")));
        Assert.Null(installer.Waiting());
    }

    /// <summary>
    /// The exchange does not succeed (a file stays in use): the program started afterwards is the previous version, marked so that it says so
    /// and offers the restart again; the waiting version stays for the next try (docs/FEHLERGEDAECHTNIS.md, No. 1265).
    /// </summary>
    [Fact]
    public void Start_ExchangeFails_ThePreviousVersionStartsMarked_AndSaysSo()
    {
        const string exe = "PDF-Knead.exe";
        var program = Path.Combine(_folder, "programm");
        var second = OperatingSystem.IsWindows() ? "b.dat" : "teile/b.dat";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(program, second))!);
        File.WriteAllText(Path.Combine(program, exe), "alt");
        File.WriteAllText(Path.Combine(program, second), "alt-b");
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand")) { Patience = TimeSpan.FromMilliseconds(300) };
        var file = Path.Combine(_folder, "p.zip");
        File.WriteAllBytes(file, Zip((exe, "neu"), (second, "neu-b")));
        var version = V("999.0.0");
        installer.MarkReady(version, installer.UnpackPackage(file, version)!);
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        (string, string) location = (program, Path.Combine(program, exe));
        bool Exchange() => UpdateStart.Run([UpdateStart.ApplyArgument, program, "-1", "999.0.0"], installer, info =>
        {
            started.Add(info);
            return true;
        }, location);
        // held as in Apply_FailsHalfway_EverythingPutBack: open under Windows, its folder not writable elsewhere
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(Path.Combine(program, second), FileMode.Open, FileAccess.Read, FileShare.None)) Assert.True(Exchange());
        }
        else
        {
            var folder = Path.Combine(program, "teile");
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.True(Exchange());
            }
            finally
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        Assert.Equal("alt", File.ReadAllText(Path.Combine(program, exe)));
        var restart = Assert.Single(started);
        Assert.Equal(Path.Combine(program, exe), restart.FileName);
        Assert.Equal("1", restart.Environment[UpdateStart.AfterExchange]);
        Assert.Equal("1", restart.Environment[UpdateStart.ExchangeFailed]);
        Assert.NotNull(installer.Waiting());

        // the previous version, started so, shows the hint and offers the restart again; a start without the mark says nothing
        var marked = new PdfKnead.Services.Updates(installer: installer);
        marked.NoteStart(exchangeFailed: true);
        Assert.Equal(PdfKnead.Services.UpdateState.Ready, marked.State);
        Assert.True(marked.CanRestart);
        Assert.Contains("999.0.0", marked.Message);
        var plain = new PdfKnead.Services.Updates(installer: installer);
        plain.NoteStart(exchangeFailed: false);
        Assert.Equal(PdfKnead.Services.UpdateState.Idle, plain.State);
        Assert.Null(plain.Message);
    }

    /// <summary>After an exchange that succeeds the program starts without the mark of a failure.</summary>
    [Fact]
    public void Start_ExchangeSucceeds_NoMarkOfAFailure()
    {
        const string exe = "PDF-Knead.exe";
        var program = Path.Combine(_folder, "programm");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, exe), "alt");
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand"));
        var file = Path.Combine(_folder, "p.zip");
        File.WriteAllBytes(file, Zip((exe, "neu")));
        var version = V("999.0.0");
        installer.MarkReady(version, installer.UnpackPackage(file, version)!);
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        Assert.True(UpdateStart.Run([UpdateStart.ApplyArgument, program, "-1", "999.0.0"], installer, info =>
        {
            started.Add(info);
            return true;
        }, (program, Path.Combine(program, exe))));
        Assert.Equal("neu", File.ReadAllText(Path.Combine(program, exe)));
        Assert.False(Assert.Single(started).Environment.ContainsKey(UpdateStart.ExchangeFailed));
        Assert.Null(installer.LastFailure);
    }

    [Fact]
    public void Start_HandsOverToTheWaitingVersion_TheNewOneExchangesAndStarts()
    {
        var installer = new UpdateInstaller(Path.Combine(_folder, "zustand"));
        var file = Path.Combine(_folder, "p.zip");
        const string exe = "PDF-Knead.exe";
        var running = Path.Combine(_folder, "laufend");
        Directory.CreateDirectory(running);
        (string, string) location = (running, Path.Combine(running, exe));
        File.WriteAllBytes(file, Zip((exe, "neu")));
        var newer = V("999.0.0");
        installer.MarkReady(newer, installer.UnpackPackage(file, newer)!);
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        // the old program hands over to the new one in the waiting area and ends
        Assert.True(UpdateStart.Run([], installer, info =>
        {
            started.Add(info);
            return true;
        }, location));
        var handOver = Assert.Single(started);
        Assert.Equal(Path.Combine(installer.Staged(newer), exe), handOver.FileName);
        Assert.Equal([UpdateStart.ApplyArgument, running, Environment.ProcessId.ToString(), "999.0.0"], handOver.ArgumentList);
        // the new one exchanges in a program folder of the test and starts the program there, marked so that it does not hand over again
        var program = Path.Combine(_folder, "programm");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, exe), "alt");
        started.Clear();
        Assert.True(UpdateStart.Run([UpdateStart.ApplyArgument, program, "-1", "999.0.0"], installer, info =>
        {
            started.Add(info);
            return true;
        }, location));
        Assert.Equal("neu", File.ReadAllText(Path.Combine(program, exe)));
        var restart = Assert.Single(started);
        Assert.Equal(Path.Combine(program, exe), restart.FileName);
        Assert.Equal("1", restart.Environment[UpdateStart.AfterExchange]);
        // a waiting version that is not newer is forgotten, nothing handed over
        installer.MarkReady(V("0.0.1"), installer.UnpackPackage(file, V("0.0.1"))!);
        Assert.False(UpdateStart.Run([], installer, _ => throw new InvalidOperationException("nichts starten"), location));
        Assert.Null(installer.Waiting());
    }
}
