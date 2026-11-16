namespace Castar.Api;

public sealed record ServerSynchronizationDto(string Kind, long? Revision = null);

public sealed record ServerUpdateSnapshotDto(long Sequence, ServerUpdateStatusDto? Status);

public static class ServerSynchronization
{
    public const string Users = "users";
    public const string Settings = "settings";
    public const string Changed = "ServerConfigurationChanged";
    public const string SubscribeUpdates = "SubscribeServerUpdates";
    public const string UpdateChanged = "ServerUpdateChanged";
}
