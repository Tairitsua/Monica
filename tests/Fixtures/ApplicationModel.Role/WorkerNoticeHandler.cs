using ApplicationModel.Domain;
using Microsoft.EntityFrameworkCore;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.Repository.Inbox.Annotations;

namespace ApplicationModel.Role;

[EventName("foreign-message.order-requested")]
public sealed record ForeignOrderNotice(string Name);

[Inbox("ordering.worker.notice")]
public sealed class WorkerNoticeHandler(OrderingContext db) : IDistributedEventHandler<ForeignOrderNotice>
{
    public async Task HandleEventAsync(ForeignOrderNotice eventData, CancellationToken cancellationToken)
    {
        // Query before staging writes, as real event consumers do when matching an incoming document.
        _ = await db.Orders.CountAsync(cancellationToken);
        db.Orders.Add(new StoredOrder { Name = eventData.Name });
    }
}
