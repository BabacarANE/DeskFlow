using Desk.Api.Http;
using Desk.Infrastructure.Messaging.Consumers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Desk.Api.Realtime;

/// <summary>
/// Real-time channel of the agents (docs/api/events.md, section 5). Only Agent and TenantAdmin of a company get in (policy
/// <see cref="Policies.Agent"/>: a Requester or the SuperAdmin is refused at the handshake). The server alone decides the
/// groups, from the validated JWT: <c>user:{sub}</c> and <c>agents:{tid}</c>. The client has no method to call.
/// </summary>
[Authorize(Policy = Policies.Agent)]
public sealed class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (!Guid.TryParse(user?.FindFirst("sub")?.Value, out var userId) || !Guid.TryParse(user?.FindFirst("tid")?.Value, out var tenantId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(userId));
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Agents(tenantId));
        await base.OnConnectedAsync();
    }
}

/// <summary>Sends to a SignalR group; the consumer only knows <see cref="IRealtimeNotifier"/>.</summary>
public sealed class SignalRRealtimeNotifier(IHubContext<NotificationsHub> hub) : IRealtimeNotifier
{
    public Task SendToGroupAsync<T>(string group, string method, T payload, CancellationToken ct) =>
        hub.Clients.Group(group).SendAsync(method, payload, ct);
}
