using Castar.Api;
using Microsoft.AspNetCore.SignalR;

namespace Castar.Server;

internal sealed class BudgetEventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext()?.Items[typeof(BudgetEventsHub)] is not Guid keyId)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, keyId.ToString("N"), Context.ConnectionAborted);
        await Clients.Caller.SendAsync(BudgetEvents.Ready, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
