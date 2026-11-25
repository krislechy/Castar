namespace Castar.Server;

/// <summary>Readiness barrier shared by HTTP and local services during manual installation.</summary>
internal sealed class ServerActivationState
{
    private readonly string? gatePath;

    public ServerActivationState(IConfiguration configuration)
    {
        VerifyingUpdate = configuration.GetValue<bool>("Castar:UpdateVerification");
        gatePath = configuration["Castar:UpdateActivationGate"];
        if (gatePath is not null && !Path.IsPathFullyQualified(gatePath))
            throw new InvalidOperationException("UpdateActivationGate must be absolute.");
    }

    public bool VerifyingUpdate { get; }

    public bool IsBlocked() => VerifyingUpdate || (gatePath is not null && File.Exists(gatePath));

    public async Task WaitUntilReadyAsync(CancellationToken stopping)
    {
        while (IsBlocked())
            await Task.Delay(TimeSpan.FromMilliseconds(250), stopping);
        stopping.ThrowIfCancellationRequested();
    }
}
