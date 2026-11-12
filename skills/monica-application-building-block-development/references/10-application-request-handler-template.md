# ApplicationRequestHandler Template

The rules define the handler contract; snippets illustrate it. A DomainService is optional. The [connected example](17-connected-use-case-example.md) shows direct handler orchestration of a tracked entity transition and a materialized read.

## Variables

- `$FeatureName$`: use-case name, such as `GetOrder` or `ApproveOrder`
- `$RequestName$`: request type, such as `QueryGetOrder` or `CommandApproveOrder`
- `$RequestRoute$`: request-specific route segment, such as `tree`, `publish`, or `approve`
- `$ResponseName$`: response DTO type when the handler returns data
- `$RepositoryName$`: repository abstraction, such as `IRepositoryOrder`
- `$ApplicationNamespace$`: the application-layer namespace chosen by the architecture skill, such as `Roles.Ordering` or `Roles.Ordering.Reporting`

## Use When

- You need an application use case for a local UI, an internal consumer, or an explicitly published HTTP operation.
- The entry point should return `Result` or `Result<T>`.
- The use case orchestrates domain logic, repositories, and mapping, but does not own the business rules itself.

## Rules

- Use `ApplicationRequestHandler<TRequest, TResponse>`; `TResponse` always names the complete mediator response. Ordinary boundaries use `Result<TData>` or `Result`; an adopted custom response uses its exact type with the same base. The request implements the matching `IRequest<TResponse>` contract. There is no separate custom handler family or implicit result wrapping.
- Declare business collaborators in the constructor, preferably a primary constructor, and inject only dependencies the handler uses. Current-user access and ID generation are explicit application dependencies; do not add them to a shared service base or resolve them through `IServiceProvider` / `CachedServiceProvider`.
- Use the inherited `Logger` in handler methods when logging is needed. Monica resolves it after activation from the
  host that owns the service instance; do not access it from a constructor.
- Make ordinary result requests implement `IQuery<TData>` or `ICommand<TData>`/`ICommand` according to their intent; `TData` is the payload inside the result envelope. Generated API method names always remove the request type's `Command` or `Query` prefix; choose the request name accordingly.
- Typed Query requests default to no root transaction independently of HTTP, publication and naming. Explicit concrete-handler ExecutionTransaction metadata may request a read-only consistency transaction. Ordinary queries and commands need no transaction attribute; orchestration commands may still select None. See the [transaction contract](../../monica-infra-persistence/references/transactions.md).
- Keep the handler focused on orchestration. Entities own their invariants; select a DomainService only for a meaningful domain rule with no natural entity owner.
- Catch exceptions only when you are adding boundary-specific context. Do not smother useful failures.
- Use `Monica.Application.Abstractions` for application primitives; compose `AddApplication()` for shared execution without HTTP.
- Publish UI and cross-domain operations with the single ApiEndpoint(method, route) in Protocol; callers inject generated domain interfaces. HTTP metadata stays on the request even in Local mode. Unattributed requests remain internal mediator operations.
- Do not put MVC `[Http*]`, `[Route]`, or `[From*]` attributes on the handler. The handler owns execution only.
- Published requests use the strict Protocol published-language namespace. Generate callers and receivers in the complete ApplicationHost from role metadata; Protocol itself chooses no transport.
- Keep private HTTP-only requests beside their handlers outside PublishedLanguages. Put wire settings in HttpApiContract and publish owned receivers from the host.
- A local-only `ApplicationRequestHandler` needs no `[ApiEndpoint]` or Web API generation configuration. Plain `IRequestHandler<TRequest, TResult>` remains available for internal support operations; both shapes execute through the mediator, not direct calls.
- Place handlers in `HandlersCommand/` or `HandlersQuery/` directly under the owning Roles.* project.

## Generation and HTTP Mapping

Use [API generation and registration](../../monica-infra-messaging/references/invocation.md) for the authoritative contract. Interfaces are stable across Debug/Release and HTTP publication. The complete ApplicationHost generates per-operation clients and owned HTTP receivers in Release too. Ordinary operations prefer Local, independently of active role ownership; only a real execution requirement warrants RunOnRole.

ApiEndpoint declares the HTTP method, relative route and binding for every published operation, including Local-only calls. HttpApiContract optionally sets an assembly-wide shared prefix without exposing receivers. Host publication does not change the shared mapping.

## Minimal Query Example

```csharp
using Monica.Core.Results;
using Monica.Core.Execution;
using Monica.BuildingBlocks.Annotations;
using Monica.Repository.Persistence.Abstractions;
using Monica.Application.Abstractions;
using Monica.Application.Annotations;

namespace $ApplicationNamespace$.HandlersQuery;

[ApiEndpoint(
    ApiHttpMethod.Get,
    "$RequestRoute$",
    Binding = ApiRequestBinding.Query)]
[BuildingBlockMetadata(
    "$FeatureName$ Query",
    Description = "Requests the $FeatureName$ use case.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[BuildingBlockRequirement("$RequirementId$")]
public sealed record Query$FeatureName$(long Id) : IQuery<$ResponseName$>;

[BuildingBlockMetadata(
    "$FeatureName$",
    Description = "Coordinates the $FeatureName$ query boundary.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[BuildingBlockRequirement("$RequirementId$")]
public sealed class QueryHandler$FeatureName$($RepositoryName$ repository)
    : ApplicationRequestHandler<Query$FeatureName$, Result<$ResponseName$>>
{
    public override async Task<Result<$ResponseName$>> Handle(
        Query$FeatureName$ request,
        CancellationToken cancellationToken)
    {
        var entity = await repository.FindAsync(request.Id, cancellationToken: cancellationToken);
        if (entity is null)
        {
            return Result.Failure<$ResponseName$>("$FeatureName$ target was not found.", ResultStatus.NotFound);
        }

        return Result.Success(new $ResponseName$
        {
            Id = entity.Id
        });
    }
}
```

## Minimal Command Example

```csharp
using Monica.Core.Results;
using Monica.BuildingBlocks.Annotations;
using Monica.Application.Abstractions;
using Monica.Application.Annotations;

namespace $ApplicationNamespace$.HandlersCommand;

[ApiEndpoint(
    ApiHttpMethod.Post,
    "$RequestRoute$",
    Binding = ApiRequestBinding.Body)]
[BuildingBlockMetadata(
    "$FeatureName$ Command",
    Description = "Requests the $FeatureName$ use case.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[BuildingBlockRequirement("$RequirementId$")]
public sealed record Command$FeatureName$(long Id) : ICommand;

[BuildingBlockMetadata(
    "$FeatureName$",
    Description = "Coordinates the $FeatureName$ command boundary.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[BuildingBlockRequirement("$RequirementId$")]
public sealed class CommandHandler$FeatureName$(IRepositoryOrder repository)
    : ApplicationRequestHandler<Command$FeatureName$, Result>
{
    public override async Task<Result> Handle(
        Command$FeatureName$ request,
        CancellationToken cancellationToken)
    {
        var order = await repository.GetAsync(request.Id, cancellationToken);
        order.Approve();
        return Result.Success();
    }
}
```

## Notes

- `Result.Success(value)` always carries data, including strings. Use `WithMessage` for a message and `Result.FromFailure<TData>` to forward an existing failure without losing metadata.
- If a query becomes complex because of filtering, paging, or policy checks, extract that logic before the handler turns procedural.
- API publication is namespace-driven, not handler-driven. Do not move a local request into `PublishedLanguages` merely to share a DTO.
