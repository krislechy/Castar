using System;

namespace Castar.Updates;

public sealed class CastarUpdate
{
    internal CastarUpdate(Version version, string tagName, Uri releasePage, long sizeBytes, string sha256)
    {
        Version = version;
        TagName = tagName;
        ReleasePage = releasePage;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    public Version Version { get; }

    public string TagName { get; }

    public Uri ReleasePage { get; }

    public long SizeBytes { get; }

    public string Sha256 { get; }
}
