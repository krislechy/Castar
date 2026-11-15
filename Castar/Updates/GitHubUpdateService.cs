using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Updates;

public sealed class GitHubUpdateService : IDisposable
{
    private const string InstallerName = "Castar-Setup-win-x64.exe";
    private const string ReleaseBase = "https://github.com/krislechy/Castar/releases";
    private const long MaxInstallerBytes = 512L * 1024 * 1024;
    private static readonly Uri LatestReleaseUrl = new("https://api.github.com/repos/krislechy/Castar/releases/latest");
    private readonly HttpClient _client;

    public GitHubUpdateService() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }, ReadInstalledVersion())
    {
    }

    internal GitHubUpdateService(HttpMessageHandler handler, Version installedVersion)
    {
        InstalledVersion = installedVersion;
        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Castar", InstalledVersion.ToString()));
    }

    public Version InstalledVersion { get; }

    public async Task<CastarUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        Release? release;
        try
        {
            release = await response.Content.ReadFromJsonAsync<Release>(timeout.Token).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub вернул сведения о выпуске в неожиданном формате.", exception);
        }

        if (release is null)
            throw new InvalidDataException("GitHub вернул пустые сведения о выпуске.");

        var tag = release.TagName;
        if (release.Draft || release.Prerelease || !TryParseReleaseTag(tag, out var version)
            || version <= InstalledVersion)
        {
            return null;
        }

        var installer = release.Assets?.Find(asset => asset?.Name == InstallerName);
        if (installer is null || installer.State != "uploaded")
            throw new InvalidDataException("В выпуске GitHub отсутствует установщик Castar для Windows x64.");

        if (installer.Size is <= 0 or > MaxInstallerBytes
            || !string.Equals(installer.BrowserDownloadUrl, InstallerUrl(tag).AbsoluteUri, StringComparison.Ordinal)
            || !TryParseDigest(installer.Digest, out var digest))
        {
            throw new InvalidDataException("В выпуске GitHub отсутствуют корректные данные для проверки установщика.");
        }

        return new CastarUpdate(version, tag, new Uri($"{ReleaseBase}/tag/{tag}"), installer.Size, digest);
    }

    public async Task<string> DownloadAsync(CastarUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        using var response = await OpenInstallerAsync(InstallerUrl(update.TagName), token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long reportedSize && reportedSize != update.SizeBytes)
            throw new InvalidDataException("Размер установщика отличается от указанного в выпуске GitHub.");

        var directory = Directory.CreateTempSubdirectory($"Castar-{update.TagName}-").FullName;
        var temporaryFile = Path.Combine(directory, "installer.download");
        var installerFile = Path.Combine(directory, InstallerName);
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (var output = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long bytesWritten = 0;
                while (await content.ReadAsync(buffer, token).ConfigureAwait(false) is var count and > 0)
                {
                    bytesWritten += count;
                    if (bytesWritten > update.SizeBytes)
                        throw new InvalidDataException("Размер установщика превышает указанный в выпуске GitHub.");

                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }

                if (bytesWritten != update.SizeBytes)
                    throw new InvalidDataException("Установщик загружен не полностью.");

                await output.FlushAsync(token).ConfigureAwait(false);
            }

            if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("Контрольная сумма SHA-256 установщика отличается от указанной в выпуске GitHub.");

            token.ThrowIfCancellationRequested();
            File.Move(temporaryFile, installerFile);
            return installerFile;
        }
        catch
        {
            try
            {
                File.Delete(temporaryFile);
                Directory.Delete(directory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private async Task<HttpResponseMessage> OpenInstallerAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
                return response;

            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                {
                    throw new HttpRequestException("GitHub не вернул установщик Castar.", null, response.StatusCode);
                }

                if (response.Headers.Location is not { IsAbsoluteUri: true } location || !IsReleaseAssetsUri(location))
                    throw new InvalidDataException("GitHub вернул неподдерживаемый адрес загрузки установщика.");

                uri = location;
            }
        }

        throw new InvalidDataException("GitHub вернул слишком много перенаправлений при загрузке установщика.");
    }

    private static bool IsReleaseAssetsUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.IdnHost is "release-assets.githubusercontent.com" or "objects.githubusercontent.com")
        && (uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal)
            || uri.AbsolutePath.StartsWith("/github-production-release-asset-2e65be/", StringComparison.Ordinal));

    private static Uri InstallerUrl(string tag) => new($"{ReleaseBase}/download/{tag}/{InstallerName}");

    private static bool TryParseReleaseTag([NotNullWhen(true)] string? tag, [NotNullWhen(true)] out Version? version)
    {
        if (tag is { Length: > 1 } && tag[0] == 'v' && Version.TryParse(tag.AsSpan(1), out var candidate)
            && candidate.Build >= 0 && candidate.Revision < 0 && tag == $"v{candidate}")
        {
            version = candidate;
            return true;
        }

        version = null;
        return false;
    }

    private static bool TryParseDigest(string? value, out string digest)
    {
        const string prefix = "sha256:";
        if (value is not null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var hex = value[prefix.Length..];
            if (hex.Length == 64 && hex.All(Uri.IsHexDigit))
            {
                digest = hex;
                return true;
            }
        }

        digest = string.Empty;
        return false;
    }

    private static Version ReadInstalledVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var numericVersion = informationalVersion?.Split(['+', '-'], 2)[0];
        if (Version.TryParse(numericVersion, out var version) && version.Build >= 0)
            return new Version(version.Major, version.Minor, version.Build);

        version = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public void Dispose() => _client.Dispose();

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? TagName,
        bool Draft,
        bool Prerelease,
        List<ReleaseAsset?>? Assets);

    private sealed record ReleaseAsset(
        string? Name,
        string? State,
        long Size,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        string? Digest);
}
