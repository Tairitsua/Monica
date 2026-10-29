using Monica.EventBus.Abstractions;
using Monica.Core.Execution;
using Monica.EventBus.Models;
using Monica.Repository.UnitOfWork.Services;

namespace Monica.Repository.Outbox.Services;

internal sealed class TransactionalEventSink(UnitOfWorkManager unitOfWork) : ITransactionalEventSink
{
    public ValueTask StageAsync(Func<IReadOnlyList<EventMessage>> prepare, CancellationToken cancellationToken = default)
    {
        try
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
        catch (Exception exception)
        {
            unitOfWork.MarkRollbackOnly(ExecutionTransactionFailure.FromException("Outbox", exception));
            throw;
        }
    }
}
