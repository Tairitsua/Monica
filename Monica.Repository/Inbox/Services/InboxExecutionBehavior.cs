using System.Reflection;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Monica.Core.Execution;
using Monica.EventBus;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Repository.Inbox.Annotations;
using Monica.Repository.Inbox.Models;
using Monica.Repository.UnitOfWork.Services;

namespace Monica.Repository.Inbox.Services;

/// <summary>Stages a consumer receipt and its handler effects inside the same operation transaction.</summary>
public sealed class InboxExecutionBehavior<TInput, TResult>(
    UnitOfWorkManager unitOfWork,
    IEventDeliveryContext delivery,
    TimeProvider time) : IExecutionBehavior<TInput, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync(ExecutionContext<TInput> context, ExecutionDelegate<TResult> next)
    {
        var descriptor = context.Descriptor;
        if (descriptor.Point != EventBusExecutionPoints.LocalHandler
            && descriptor.Point != EventBusExecutionPoints.DistributedHandler
            && descriptor.Point != EventBusExecutionPoints.DistributedBatchHandler)
            return await next();

        var attribute = descriptor.EntryMethod?.GetCustomAttribute<InboxAttribute>(inherit: true)
            ?? descriptor.ComponentType.GetCustomAttribute<InboxAttribute>(inherit: true);
        if (attribute is null) return await next();
        if (descriptor.TransactionMode != ExecutionTransactionMode.Automatic)
            throw new InvalidOperationException($"Inbox handler '{descriptor.ComponentType}' must use an automatic transaction.");

        if (descriptor.Point == EventBusExecutionPoints.DistributedBatchHandler)
        {
            if (typeof(TResult) != typeof(ExecutionUnit))
                throw new InvalidOperationException("Inbox deduplication requires a no-result EventBus handler boundary.");
            var batch = context.Features.GetRequired<EventBatchExecutionFeature>();
            await StageBatchAsync(batch, attribute.Consumer, context.CancellationToken);
            return batch.Metadata.Count == 0 ? (TResult)(object)ExecutionUnit.Value : await next();
        }

        var metadata = delivery.Current;
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.MessageId) || string.IsNullOrWhiteSpace(metadata.Source))
            throw new InvalidOperationException($"Inbox handler '{descriptor.ComponentType}' requires stable incoming source and message identity.");
        var db = unitOfWork.RequireInboxOwner();
        var receipts = db.Set<InboxReceipt>();
        var consumer = attribute.Consumer;
        var source = metadata.Source;
        var messageId = metadata.MessageId;
        if (await receipts.AsNoTracking().AnyAsync(x => x.Consumer == consumer && x.Source == source
                && x.MessageId == messageId, context.CancellationToken))
        {
            if (typeof(TResult) != typeof(ExecutionUnit))
                throw new InvalidOperationException("Inbox deduplication requires a no-result EventBus handler boundary.");
            return (TResult)(object)ExecutionUnit.Value;
        }

        // The database composite primary key arbitrates concurrent deliveries. A losing transaction
        // rolls back and its broker retry observes the winner's committed receipt.
        receipts.Add(new InboxReceipt
        {
            Consumer = consumer,
            Source = source,
            MessageId = messageId,
            ReceivedAtUtc = time.GetUtcNow().UtcDateTime
        });
        return await next();
    }

    private async Task StageBatchAsync(EventBatchExecutionFeature batch, string consumer, CancellationToken cancellationToken)
    {
        if (batch.Metadata.Any(metadata => string.IsNullOrWhiteSpace(metadata.MessageId)
                || string.IsNullOrWhiteSpace(metadata.Source)))
            throw new InvalidOperationException("Batch Inbox requires stable source and message identity for every input.");
        var receipts = unitOfWork.RequireInboxOwner().Set<InboxReceipt>();
        var identities = batch.Metadata.Select(metadata => (Source: metadata.Source!, MessageId: metadata.MessageId!))
            .Distinct().ToArray();
        var known = new HashSet<(string Source, string MessageId)>();
        // Bound SQL parameters and expression depth even for explicitly enlarged receive groups.
        foreach (var chunk in identities.Chunk(100))
        {
            Expression<Func<InboxReceipt, bool>> predicate = receipt => false;
            foreach (var identity in chunk)
            {
                Expression<Func<InboxReceipt, bool>> match = receipt => receipt.Source == identity.Source
                    && receipt.MessageId == identity.MessageId;
                var body = new ReceiptParameterReplacer(match.Parameters[0], predicate.Parameters[0]).Visit(match.Body)!;
                predicate = Expression.Lambda<Func<InboxReceipt, bool>>(
                    Expression.OrElse(predicate.Body, body), predicate.Parameters);
            }
            var committed = await receipts.AsNoTracking().Where(receipt => receipt.Consumer == consumer)
                .Where(predicate).Select(receipt => new { receipt.Source, receipt.MessageId }).ToListAsync(cancellationToken);
            foreach (var receipt in committed) known.Add((receipt.Source, receipt.MessageId));
        }
        var selected = new List<int>();
        var staged = new List<InboxReceipt>();
        var now = time.GetUtcNow().UtcDateTime;
        for (var index = 0; index < batch.Metadata.Count; index++)
        {
            var metadata = batch.Metadata[index];
            if (!known.Add((metadata.Source!, metadata.MessageId!))) continue;
            selected.Add(index);
            staged.Add(new InboxReceipt
            {
                Consumer = consumer, Source = metadata.Source!, MessageId = metadata.MessageId!, ReceivedAtUtc = now
            });
        }
        receipts.AddRange(staged);
        batch.Retain(selected);
    }

    private sealed class ReceiptParameterReplacer(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : node;
    }
}
