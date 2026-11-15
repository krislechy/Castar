namespace Castar.Api;

public sealed record ServerUpdateStatusDto(string State, string InstalledVersion, string? Version = null,
    long DownloadedBytes = 0, long TotalBytes = 0, string? Error = null);
