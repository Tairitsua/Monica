using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Repository.UnitOfWork.Services;

namespace Monica.Repository.Outbox.Services;

internal sealed class TransactionalEventSink(UnitOfWorkManager unitOfWork) : ITransactionalEventSink
{
    public ValueTask StageAsync(Func<IReadOnlyList<EventMessage>> prepare, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = unitOfWork.RequireOutboxOwner();
        var messages = prepare() ?? throw new InvalidOperationException("Durable event preparation returned no message collection.");
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.StagePreparedMessage(message);
        }
        return ValueTask.CompletedTask;
    }
}
