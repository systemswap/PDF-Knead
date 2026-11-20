using System.Net;
using System.Security.Cryptography;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Updates;

/// <summary>What a check for updates found.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>The running version is the newest.</summary>
    Current,

    /// <summary>A newer version for this platform (<see cref="UpdateCheckResult.Manifest"/>).</summary>
    Available,

    /// <summary>No answer, the repository not public (yet), or no network — nothing happened.</summary>
    Unreachable,

    /// <summary>An answer that is not a correctly signed version file — ignored.</summary>
    Invalid,

    /// <summary>The program has no release key yet: no update is accepted.</summary>
    NoKey,
}

/// <summary>The result of a check: what was found and, if a newer version, its version file and package.</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateManifest? Manifest = null, UpdatePackage? Package = null);

/// <summary>
/// Asks the releases of the repository for a newer version and loads its package (KONZEPT.md §5.26). Only the two files of the release and
/// the package; https only; redirections followed only to the download hosts of GitHub; every answer within its limit. Sent is the request
/// alone, with „PDF-Knead/&lt;version&gt;“ as the program name — no identifier, nothing about the user or documents.
/// </summary>
public sealed class UpdateClient(HttpMessageHandler? handler = null)
{
    /// <summary>
    /// The version file of the newest version: every release v&lt;Version&gt; carries its own, signed, and GitHub's „latest“ points to the
    /// newest one — the releases are published as full releases, the betas too, so that „latest“ reaches them (design decision,
    /// 20.11.2026: the release page shows only the versions; before, a fixed release „aktuell“ held the two files). Both files are asked
    /// for under „latest“; should a new release appear between the two requests, the signature does not fit and nothing happens.
    /// </summary>
    public const string ManifestUrl = "https://github.com/systemswap/PDF-Knead/releases/latest/download/version.json";

    /// <summary>Its signature.</summary>
    public const string SignatureUrl = ManifestUrl + ".sig";

    /// <summary>The page of the releases, for the browser when the program cannot replace itself.</summary>
    public const string ReleasesPage = "https://github.com/systemswap/PDF-Knead/releases";

    /// <summary>The hosts a request may be redirected to (GitHub serves release files from these).</summary>
    public static readonly IReadOnlySet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    /// <summary>How long the check may take in all.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private const int MaxRedirects = 5;

    /// <summary>Checks for a newer version than <paramref name="current"/> for <paramref name="platform"/>.</summary>
    /// <param name="current">The running version.</param>
    /// <param name="platform">The platform (<see cref="UpdateManifest.ThisPlatform"/>).</param>
    /// <param name="publicKey">The release key; <see cref="UpdateSignature.ReleaseKey"/> if null.</param>
    /// <param name="cancel">Stops the check.</param>
    public async Task<UpdateCheckResult> CheckAsync(UpdateVersion current, string platform, string? publicKey = null, CancellationToken cancel = default)
    {
        if ((publicKey ?? UpdateSignature.ReleaseKey).Length == 0) return new UpdateCheckResult(UpdateCheckOutcome.NoKey);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(CheckTimeout);
        byte[]? manifest, signature;
        try
        {
            manifest = await GetAsync(ManifestUrl, UpdateManifest.MaxBytes, limit.Token);
            signature = manifest is null ? null : await GetAsync(SignatureUrl, UpdateSignature.MaxBytes, limit.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException && !cancel.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Unreachable);
        }
        if (manifest is null || signature is null) return new UpdateCheckResult(UpdateCheckOutcome.Unreachable);
        // the signature first: nothing of an unsigned file is read
        if (!UpdateSignature.Verify(manifest, signature, publicKey) || UpdateManifest.Parse(manifest) is not { } read)
            return new UpdateCheckResult(UpdateCheckOutcome.Invalid);
        if (!read.Version.IsNewerThan(current) || read.PackageFor(platform) is not { } package) return new UpdateCheckResult(UpdateCheckOutcome.Current, read);
        return new UpdateCheckResult(UpdateCheckOutcome.Available, read, package);
    }

    /// <summary>
    /// Loads a package into <paramref name="target"/>: exactly the size of the version file, the SHA-256 checked; a file that does not match is
    /// deleted. Returns false on any mismatch or failure.
    /// </summary>
    public async Task<bool> DownloadAsync(UpdatePackage package, string target, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var partial = target + ".teil";
        try
        {
            using var response = await SendAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response is null || (response.Content.Headers.ContentLength is { } length && length != package.Size)) return false;
            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                    if (total > package.Size) return false;
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    progress?.Report((double)total / package.Size);
                }
                if (total != package.Size || Convert.ToHexStringLower(sha.GetHashAndReset()) != package.Sha256) return false;
            }
            File.Move(partial, target, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (IOException)
            {
                // left for the next start
            }
        }
    }

    private async Task<byte[]?> GetAsync(string url, int max, CancellationToken cancel)
    {
        using var response = await SendAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        return response is null ? null : LimitedHttp.Read(response, max, cancel);
    }

    /// <summary>A GET that follows redirections only over https and only to <see cref="Hosts"/>; null for anything but success.</summary>
    private async Task<HttpResponseMessage?> SendAsync(string url, HttpCompletionOption completion, CancellationToken cancel)
    {
        // the client lives as long as the response that reads from it (OwnedResponse); without one it goes at once
        var client = LimitedHttp.Client(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;   // the caller limits the time
        try
        {
            var uri = new Uri(url);
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (uri.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(uri.Host) || !uri.IsDefaultPort) break;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("PDF-Knead/" + AppVersion.Raw.Replace(' ', '-'));
                var response = await client.SendAsync(request, completion, cancel);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    response.Dispose();
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    response.Dispose();
                    break;
                }
                return new OwnedResponse(response, client);
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
        client.Dispose();
        return null;
    }

    /// <summary>A response that also disposes the client it came from.</summary>
    private sealed class OwnedResponse : HttpResponseMessage
    {
        private readonly HttpResponseMessage _inner;
        private readonly HttpClient _client;

        public OwnedResponse(HttpResponseMessage inner, HttpClient client) : base(inner.StatusCode)
        {
            _inner = inner;
            _client = client;
            Content = inner.Content;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
