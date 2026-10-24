# Hosted Service Template

Use a `HostedService` ProjectUnit for an owned connection, listener, or continuous consumer. Prefer `MoHostedService` for resource start/stop and `MoBackgroundService` for a permanent asynchronous loop. Both retain the existing HostedService classification; finite work items and buffers are support types.

## Select the owner

- Finite, idempotent startup preparation is a Seeder. It must not start permanent tasks or timers.
- Independently scheduled or triggered work with history and operator control is a Job.
- Continuous consumption is a hosted service. Expiry checks over its own in-memory state may remain in that service.
- An existing DataChannel owns its endpoints and lifecycle. Do not add a second worker starting the same processor.

Place services in `Roles.{Domain}[.{Purpose}]/HostedServices/`. Name a loop `{Purpose}BackgroundService` and a lifecycle-only service `{Purpose}HostedService`; keep `Worker*` for RecurringJob. Supporting `{Purpose}WorkItem`, buffer, and state types stay beside their owner. Domain rules remain in the core; process queues and timers do not become domain rules.

## Lifetime and operations

Register one host-owned instance through normal composition. Monica derives placement and execution policy from the implementation and captured registration provenance, including factory descriptors without invoking them. Role-owned services start only in Automatic mode; Manual services are held and listed in diagnostics. Constructors must not start work. See [hosted-service integration](../../monica-infra-hosting/references/hosted-services.md).

Use `[ProcessLocal]` only for finite initialization needed by included passive behavior that changes process memory; it never exempts a continuous loop, external mutation, publication or shared claim. Domain-core autonomous initialization needs a same-domain BackgroundRole unless it meets that narrow exception. Held prerequisite resources remain unavailable. Follow the [local execution contract](../../monica-application/references/04-startup-and-local-execution.md) rather than adding a generic manual hosted-service runner or a debug-only registration branch.

A service may retain immutable configuration, thread-safe infrastructure, and process state. It must not retain scoped repositories, application API clients, current-user services, or collaborators capturing them indirectly. Never retain one scope for the permanent loop or resolve scoped dependencies from the root provider.

Use `ExecuteWorkItemAsync<TWorkItem>` or its typed-input/result overload for each finite operation. The framework creates and disposes an async scope and invokes the execution pipeline. Register work items as scoped or transient through normal DI. Their concrete `ExecuteAsync` methods own transaction overrides. Start/stop default to None; work items default to Automatic. A coordinator performing independently committed operations explicitly selects None and preserves those operations' commit boundaries.

Work items may orchestrate bounded operations directly. Do not add a forwarding ApplicationService solely to wrap a work item. Independently callable use cases use ApplicationService; entities and meaningful DomainServices own rules. Never hold a transaction across the permanent loop or an idle wait.

## Continuous consumer example

This role-local consumer uses `IRepositoryOrder` and `Order` from the [connected example](17-connected-use-case-example.md). Split the types into normal files under `Roles.Ordering/HostedServices/`; the buffer, input and work item are support types, not additional ProjectUnit families. The declared policy is serial processing, at most 128 buffered items, immediate rejection before startup or when full or closed, and acceptable loss on process restart. Admission acknowledges memory storage only, not approval or durable delivery. Missing or already approved orders are skipped; an unexpected failure closes admission and faults the observed service without an automatic retry.

```csharp
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Execution;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Modules;
using Monica.ProjectUnits.Annotations;
using Domains.Ordering.Interfaces;

namespace Roles.Ordering.HostedServices;

public sealed record OrderApprovalInput(long OrderId);

public sealed class OrderApprovalBuffer
{
    private int _admission; // 0: not started, 1: accepting, 2: permanently closed.
    private readonly Channel<OrderApprovalInput> _channel =
        Channel.CreateBounded<OrderApprovalInput>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    public bool TryEnqueue(OrderApprovalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.OrderId > 0 && Volatile.Read(ref _admission) == 1
            && _channel.Writer.TryWrite(input);
    }

    internal ChannelReader<OrderApprovalInput> Reader => _channel.Reader;
    internal void StartAccepting() => Interlocked.CompareExchange(ref _admission, 1, 0);

    internal void Complete()
    {
        Interlocked.Exchange(ref _admission, 2);
        _channel.Writer.TryComplete();
    }

    internal void DiscardPending()
    {
        while (_channel.Reader.TryRead(out _)) { }
    }
}

public sealed class OrderApprovalWorkItem(IRepositoryOrder repository)
    : IHostedServiceWorkItem<OrderApprovalInput, bool>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic)]
    public async Task<bool> ExecuteAsync(
        OrderApprovalInput input, CancellationToken cancellationToken)
    {
        var order = await repository.FindAsync(
            input.OrderId, cancellationToken: cancellationToken);
        if (order is null || order.IsApproved) return false;

        order.Approve();
        return true;
    }
}

[ProjectUnitMetadata("Order approval consumer",
    Description = "Serially processes admitted approvals in finite operation scopes.",
    Tags = ["ordering", "approval"])]
public sealed class OrderApprovalBackgroundService(
    OrderApprovalBuffer buffer,
    IObservableInstanceRegistry observableRegistry,
    IOptions<ModuleHostedServiceOption> options,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<OrderApprovalBackgroundService> logger)
    : MoBackgroundService(observableRegistry, options, serviceScopeFactory, logger)
{
    protected override Task OnStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        buffer.StartAccepting();
        return Task.CompletedTask;
    }

    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var input in buffer.Reader.ReadAllAsync(stoppingToken))
            {
                stoppingToken.ThrowIfCancellationRequested();
                var changed = await ExecuteWorkItemAsync<
                    OrderApprovalWorkItem, OrderApprovalInput, bool>(input, stoppingToken);
                RecordState(changed ? "Order approved" : "Order missing or already approved",
                    HostedServiceState.Executing);
            }
        }
        catch (OperationCanceledException exception) when (!stoppingToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Order approval was canceled outside host shutdown.", exception);
        }
        finally
        {
            buffer.Complete();
            buffer.DiscardPending();
        }
    }

    protected override async Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        buffer.Complete();
        if (ExecuteTask is not { } execution) return;

        try
        {
            await execution.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordState("Drain deadline elapsed; cancel remaining work",
                HostedServiceState.Stopping, logLevel: LogLevel.Warning);
        }
    }
}
```

`BoundedChannelFullMode.Wait` combined with `TryWrite` rejects a full queue immediately; it does not wait or silently drop another item. Admission opens only when the consumer starts, so an inactive or Manual role's buffer rejects work. Closing is permanent for that host instance, including a failure racing startup. Producers inject only the singleton buffer and handle `false` explicitly. They do not retain the consumer, its work item or an operation scope. A published admission operation that requires this live role follows the existing execution-placement contract and reports a held resource as unavailable. Do not use this queue when the source requires durable acknowledgment or replay; adopt the existing delivery mechanism instead.

Add these contributions to the role's existing `ApplicationRegistration`, named by its `ApplicationRole.Registration` declaration. The passive buffer remains available to report unavailable admission even when the consumer is held:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.Modules;

namespace Roles.Ordering.HostedServices;

public sealed class RoleRegistration : ApplicationRegistration
{
    public override void ConfigurePassive(ApplicationRegistrationContext context)
    {
        context.Services.AddSingleton<OrderApprovalBuffer>();
        context.Services.AddScoped<OrderApprovalWorkItem>();
    }

    public override void ConfigureActive(ApplicationRegistrationContext context)
    {
        context.Monica.AddHostedService();
        context.Services.AddHostedService<OrderApprovalBackgroundService>();
    }
}
```

Retain the host's native `AddApplicationModel`, annotated role discovery and real persistence composition: `IRepositoryOrder` must resolve against the scoped `OrderingDbContext`, with its provider and `AddUnitOfWork` participation already registered. The host does not call or maintain a list of role registrations. ConfigureActive declares the service in both Manual and Automatic modes; Monica holds its descriptor in Manual without creating the consumer. `AddHostedService` supplies execution and observability dependencies; the DI registration supplies the one host-owned consumer. Do not additionally register a second singleton consumer instance.

Every item resolves a fresh scoped work item and repository. `Automatic` is explicit here and is also the native work-item default; the registered UnitOfWork behavior owns save/commit or rollback before the call completes. The permanent loop and queue wait are outside that transaction. Start/stop lifecycle points default to `None`. A different work item that coordinates independently committed operations selects `[ExecutionTransaction(ExecutionTransactionMode.None)]` on its concrete `ExecuteAsync` and preserves those child operations' scope/commit boundaries; `None` does not suppress an already active transaction. Use the [transaction contract](../../monica-infra-persistence/references/transactions.md), rather than copying a second persistence tutorial here.

On stop, the hook first closes admission and waits for accepted items to drain within the host's shutdown deadline. If that deadline expires, the hook returns so the base stop path cancels the background token; the loop and repository calls must honor it. The loop's `finally` closes admission after cancellation or failure and discards any remaining in-memory items. Cancellation from another source is converted to an observed failure, because the base class treats an escaping `OperationCanceledException` as cancellation. Cancellation cannot force an uncooperative dependency to stop or prove an ambiguous commit rolled back. Await host shutdown before disposing its services. The base class records unexpected loop failures as `Faulted`; this example does not retry them or promise that a fault automatically stops the whole host.

## Concurrency, delivery, and shutdown

Specify serial, per-key serial, or bounded parallel processing. Fresh scopes do not make shared state thread-safe. Preserve ordering when migrating a serial consumer. Process queues, locks, and role activation do not provide cluster-wide exclusivity; replicas require an explicit ownership or persistence contract.

Every queue declares capacity, backpressure or rejection, acknowledgment timing, and whether restart loss is acceptable. Memory admission is not durable handling. Select Inbox/Outbox or durable jobs only for an actual delivery requirement; do not silently change receipt/commit semantics during migration.

Own and await child tasks. Pass cancellation to waits and operations. Stop admission before shutdown and specify whether pending work drains or is abandoned within the shutdown deadline. Dispose timers, connections, and cancellation sources. Avoid fire-and-forget tasks, async-void callbacks, blocking `.Result`, and constructor/seeder startup side effects.

Distinguish invalid items, retryable dependencies, and fatal service failures. Isolate a bad item when the declared policy requires continued consumption. Retry from fresh scopes and account for committed/external effects. Report failures through observed state; heartbeat alone does not establish business readiness.

## Metadata and verification

Concrete services declare their own ProjectUnit metadata and applicable real requirement IDs. Work items need no fake unit classification. Preserve the Start, Stop, and WorkItem execution points.

Verify through the actual owning host composition: inactive and Manual roles do not start or accept buffered work; only one service instance exists when admitted; work gets independent disposable scopes; ordering survives concurrent admission; failed items follow policy; cancellation ends waits and operations; stop awaits owned tasks. Test rules separately and use transaction tests for commit/rollback. Prefer controllable clocks and signals over production-duration sleeps.
