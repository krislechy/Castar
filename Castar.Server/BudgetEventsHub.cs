using Castar.Api;
using Microsoft.AspNetCore.SignalR;

namespace Castar.Server;

internal sealed class BudgetEventsHub(ApiKeyStore keys) : Hub
{
    private static readonly object RevocationHandler = new();

    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext()?.Items[typeof(BudgetEventsHub)] is not Guid keyId)
        {
            Context.Abort();
            return;
        }

        var connection = Context;
        Action<Guid> close = revoked =>
        {
            if (revoked == keyId)
                connection.Abort();
        };
        Context.Items[RevocationHandler] = close;
        keys.Revoked += close;
        if (!keys.GetActiveKeyIds().Contains(keyId))
        {
            UnsubscribeRevocation();
            Context.Abort();
            return;
        }

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, keyId.ToString("N"), Context.ConnectionAborted);
            await Clients.Caller.SendAsync(BudgetEvents.Ready, Context.ConnectionAborted);
            await base.OnConnectedAsync();
        }
        catch
        {
            UnsubscribeRevocation();
            throw;
        }
    }

    public override Task OnDisconnectedAsync(Exception? error)
    {
        UnsubscribeRevocation();
        return base.OnDisconnectedAsync(error);
    }

    private void UnsubscribeRevocation()
    {
        if (Context.Items.Remove(RevocationHandler, out var handler) && handler is Action<Guid> close)
            keys.Revoked -= close;
    }
}
