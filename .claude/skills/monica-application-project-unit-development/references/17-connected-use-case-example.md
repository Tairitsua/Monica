# Connected Use-Case Example

This illustrative domain-owned role use case connects local HTTP requests, handlers, an entity, a narrow repository, and its EF implementation. An approval command loads the tracked state owner and calls its invariant directly. A query receives a completed read projection. Neither use case needs a DomainService, event, or job for this requirement.

`Order` and the `Ordering` names are examples. Replace `$RequirementId$` with an applicable actual project requirement when available; follow [metadata debt handling](03-project-unit-context-metadata.md) otherwise. Split these sections into the project's normal source files. Place handlers in Roles.Ordering and the entity/repository in Domains.Ordering under the unified layout.

## Local Requests and Handlers

The attributed requests are outside `PublishedLanguages`, so they are local HTTP contracts. Put the wire declaration in the role assembly's HttpApiContract.cs, reference the declared role assembly from the complete ApplicationHost, the command in `Roles.Ordering/HandlersCommand/CommandHandlerApproveOrder.cs`, and the query in `Roles.Ordering/HandlersQuery/QueryHandlerGetOrder.cs`.

```csharp
using System.Threading;
using System.Threading.Tasks;
using Monica.Core.Results;
using Monica.ProjectUnits.Annotations;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.Application.Annotations;
using Domains.Ordering.Interfaces;
using Domains.Ordering.Models;

[assembly: HttpApiContract("api/v1", DomainName = "Ordering")]

namespace Domains.Ordering.Application.HandlersCommand
{
    [ApiEndpoint(ApiHttpMethod.Post, "approve", Binding = ApiRequestBinding.Body)]
    [ProjectUnitMetadata("Approve Order Command",
        Description = "Requests approval of an existing order.", Tags = ["ordering", "approval"])]
    [ProjectUnitRequirement("$RequirementId$")]
    public sealed record CommandApproveOrder(long Id) : IResultRequest;

    [ProjectUnitMetadata("Approve Order",
        Description = "Loads the order and coordinates its approval transition.", Tags = ["ordering", "approval"])]
    [ProjectUnitRequirement("$RequirementId$")]
    public sealed class CommandHandlerApproveOrder(IRepositoryOrder repository)
        : ApplicationService<CommandApproveOrder>
    {
        public override async Task<Res> Handle(CommandApproveOrder request, CancellationToken cancellationToken)
        {
            var order = await repository.GetAsync(request.Id, cancellationToken);
            order.Approve();
            return Res.Ok();
        }
    }
}

namespace Domains.Ordering.Application.HandlersQuery
{
    [ApiEndpoint(ApiHttpMethod.Get, "summary", Binding = ApiRequestBinding.Query)]
    [ProjectUnitMetadata("Order Summary Query",
        Description = "Requests an order summary without persistence internals.", Tags = ["ordering", "approval"])]
    [ProjectUnitRequirement("$RequirementId$")]
    public sealed record QueryGetOrder(long Id) : IResultRequest<OrderSummary>;

    [ProjectUnitMetadata("Get Order Summary",
        Description = "Returns a materialized order summary from its owning repository.", Tags = ["ordering", "approval"])]
    [ProjectUnitRequirement("$RequirementId$")]
    public sealed class QueryHandlerGetOrder(IRepositoryOrder repository)
        : ApplicationService<QueryGetOrder, OrderSummary>
    {
        public override async Task<Res<OrderSummary>> Handle(QueryGetOrder request, CancellationToken cancellationToken)
        {
            var summary = await repository.FindSummaryAsync(request.Id, cancellationToken);
            return summary is null
                ? Res.Fail("Order was not found.", ResStatus.NotFound)
                : Res.Ok<OrderSummary>(summary);
        }
    }
}
```

## Entity

Place the state owner in `Entities/Order.cs`. Approval rejects a repeated transition independently of the caller; the handler does not assign the approval flag.

```csharp
using System;
using Monica.ProjectUnits.Annotations;
using Monica.Repository.Entity.Abstractions;

namespace Domains.Ordering.Entities;

[ProjectUnitMetadata("Order", Description = "Owns order naming and approval invariants.",
    Tags = ["ordering", "approval"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class Order : Entity<long>
{
    public string Number { get; private set; }
    public bool IsApproved { get; private set; }

    public Order(long id, string number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new ArgumentException("Order number is required.", nameof(number));
        }

        Id = id;
        Number = number.Trim();
    }

    public void Approve()
    {
        if (IsApproved)
        {
            throw new InvalidOperationException("Order is already approved.");
        }

        IsApproved = true;
    }
}
```

## Repository and Projection

Place the interface in `Interfaces/IRepositoryOrder.cs`, the implementation in `Repository/RepositoryOrder.cs`, and the domain-private read DTO in `Models/OrderSummary.cs`. The pair's exact names satisfy runtime and source repository discovery. Only the implementation sees EF query operators.

```csharp
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monica.ProjectUnits.Annotations;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Services;
using Domains.Ordering.Entities;
using Domains.Ordering.Models;

namespace Domains.Ordering.Models
{
    public sealed record OrderSummary(long Id, string Number, bool IsApproved);
}

namespace Domains.Ordering.Interfaces
{
    public interface IRepositoryOrder : IRepository<Order, long>
    {
        Task<OrderSummary?> FindSummaryAsync(long id, CancellationToken cancellationToken = default);
    }
}

namespace Domains.Ordering.Repository
{
    [ProjectUnitMetadata("Order Repository",
        Description = "Stages order changes and materializes order summaries.", Tags = ["ordering", "approval"])]
    [ProjectUnitRequirement("$RequirementId$")]
    public sealed class RepositoryOrder(OrderingDbContext context)
        : EfRepository<OrderingDbContext, Order, long>(context), Interfaces.IRepositoryOrder
    {
        public Task<OrderSummary?> FindSummaryAsync(long id, CancellationToken cancellationToken = default)
            => DbContext.Orders.AsNoTracking().Where(order => order.Id == id)
                .Select(order => new OrderSummary(order.Id, order.Number, order.IsApproved))
                .SingleOrDefaultAsync(cancellationToken);
    }
}
```

## Context

Place the context in `Repository/OrderingDbContext.cs`. The example uses caller-assigned keys. Apply the project's adopted ID policy when creating orders; the command above mutates an existing order.

```csharp
using Microsoft.EntityFrameworkCore;
using Monica.DependencyInjection.Abstractions;
using Monica.Repository.Persistence.Services;
using Domains.Ordering.Entities;

namespace Domains.Ordering.Repository;

public sealed class OrderingDbContext(
    DbContextOptions<OrderingDbContext> options,
    ICachedServiceProvider serviceProvider)
    : RepositoryDbContext<OrderingDbContext>(options, serviceProvider)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>().Property(order => order.Id).ValueGeneratedNever();
    }
}
```

## Composition and Verification

Use the established host modules to register the context with `AddRepositoryDbContext<OrderingDbContext>` and the provider, discover the domain assembly, and expose `IRepositoryOrder` and its implementation against the same scoped context. The command executes through the existing mediator/execution pipeline; that operation owns commit and rollback. A direct `Handle` call alone does not establish transaction semantics.

The canonical template test compiles these connected sections, checks runtime/source discovery parity and repository/entity/request associations, and executes the handler's transition through a test repository. Project adoption also verifies scoped identity, persisted approval, rejection/rollback, and the materialized read through its real host using [application testing](../../monica-application-unit-testing/SKILL.md). No production repository save loop or detached update is involved.
