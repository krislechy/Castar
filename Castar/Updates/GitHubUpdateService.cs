using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Updates;

public sealed class GitHubUpdateService : IDisposable
{
    private readonly GitHubReleaseAssetService updates;

    public GitHubUpdateService() : this(null, GitHubReleaseAssetService.ReadInstalledVersion(Assembly.GetExecutingAssembly()))
    {
    }

    internal GitHubUpdateService(HttpMessageHandler? handler, Version installedVersion) =>
        updates = new("Castar-Update-win-x64.zip", installedVersion, handler);

    public Version InstalledVersion => updates.InstalledVersion;

    public Task<CastarUpdate?> CheckAsync(CancellationToken cancellationToken = default) =>
        updates.CheckAsync(cancellationToken);

    public Task<string> DownloadAsync(CastarUpdate update, CancellationToken cancellationToken = default,
        IProgress<long>? progress = null) => updates.DownloadAsync(update, cancellationToken, progress);

    public void Dispose() => updates.Dispose();
}
