# ApplicationService Template

The rules define the handler contract; snippets illustrate it. A DomainService is optional. The [connected example](17-connected-use-case-example.md) shows direct handler orchestration of a tracked entity transition and a materialized read.

## Variables

- `$FeatureName$`: use-case name, such as `GetOrder` or `ApproveOrder`
- `$RequestName$`: request type, such as `QueryGetOrder` or `CommandApproveOrder`
- `$RequestRoute$`: request-specific route segment, such as `tree`, `publish`, or `approve`
- `$ResponseName$`: response DTO type when the handler returns data
- `$RepositoryName$`: repository abstraction, such as `IRepositoryOrder`
- `$ApplicationNamespace$`: the application-layer namespace chosen by the architecture skill, such as `OrderingService.API` or `Domains.Ordering.Application`

## Use When

- You need an application use case for a local UI, an internal consumer, or an explicitly published HTTP operation.
- The entry point should return `Res` or `Res<T>`.
- The use case orchestrates domain logic, repositories, and mapping, but does not own the business rules itself.

## Rules

- Use `ApplicationService<TRequest, TResponse>` or `ApplicationService<TRequest>` for ordinary `Res` boundaries. Native `CustomApplicationService<TRequest, TResult>` is available when an adopted contract needs an explicit custom mediator result; it remains the same application role and execution boundary, not a new service family.
- Declare business collaborators in the constructor, preferably a primary constructor, and inject only dependencies the handler uses. Current-user access and ID generation are explicit application dependencies; do not add them to a shared service base or resolve them through `IServiceProvider` / `CachedServiceProvider`.
- Use the inherited `Logger` in handler methods when logging is needed. Monica resolves it after activation from the
  host that owns the service instance; do not access it from a constructor.
- Make the request implement `IResultRequest<TResponse>` or `IResultRequest`. Generated API method names always remove the request type's `Command` or `Query` prefix; choose the request name accordingly.
- Published Query requests default to read-only independently of HTTP. Explicit concrete-handler ExecutionTransaction metadata overrides the default. Internal unmarked reads can use ExecutionTransactionMode.None; ordinary writes need no attribute. See the [transaction contract](../../monica-infra-persistence/references/transactions.md).
- Keep the handler focused on orchestration. Entities own their invariants; select a DomainService only for a meaningful domain rule with no natural entity owner.
- Catch exceptions only when you are adding boundary-specific context. Do not smother useful failures.
- Use `Monica.Application.Abstractions` for application primitives; compose `AddApplication()` for shared execution without HTTP.
- Publish UI and cross-domain operations with the single ApiEndpoint(method, route) in Protocol; callers inject generated domain interfaces. HTTP metadata stays on the request even in Local mode. Unattributed requests remain internal mediator operations.
- Do not put MVC `[Http*]`, `[Route]`, or `[From*]` attributes on the handler. The handler owns execution only.
- Published requests use the strict Protocol published-language namespace. Generate adapters in the host with ApplicationApiGenerationConfig; Protocol itself chooses no transport.
- Keep private HTTP-only requests beside their handlers outside PublishedLanguages. Put wire settings in HttpApiContract and publish owned receivers from the host.
- A local-only `ApplicationService` needs no `[ApiEndpoint]` or Web API generation configuration. Plain `IRequestHandler<TRequest, TResult>` remains available for internal support operations; both shapes execute through the mediator, not direct calls.
- Place handlers in `HandlersCommand/` or `HandlersQuery/` under the application layer chosen by the architecture skill.

## Generation and HTTP Mapping

Use [RPC generation and registration](../../monica-infra-messaging/references/rpc.md) for the authoritative contract. Interfaces are stable across Debug/Release and HTTP publication. A monolith generates Local clients and optionally publishes HTTP. An HTTP microservice generates paired clients and owned receivers in Release too.

ApiEndpoint declares the HTTP method, relative route and binding for every published operation, including Local-only calls. HttpApiContract optionally sets an assembly-wide shared prefix without exposing receivers. Host publication does not change the shared mapping.

## Minimal Query Example

```csharp
using Monica.Core.Results;
using Monica.Core.Execution;
using Monica.ProjectUnits.Annotations;
using Monica.Repository.Persistence.Abstractions;
using Monica.Application.Abstractions;
using Monica.Application.Annotations;

namespace $ApplicationNamespace$.HandlersQuery;

[ApiEndpoint(
    ApiHttpMethod.Get,
    "$RequestRoute$",
    Binding = ApiRequestBinding.Query)]
[ProjectUnitMetadata(
    "$FeatureName$ Query",
    Description = "Requests the $FeatureName$ use case.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed record Query$FeatureName$(long Id) : IResultRequest<$ResponseName$>;

[ProjectUnitMetadata(
    "$FeatureName$",
    Description = "Coordinates the $FeatureName$ query boundary.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class QueryHandler$FeatureName$($RepositoryName$ repository)
    : ApplicationService<Query$FeatureName$, $ResponseName$>
{
    public override async Task<Res<$ResponseName$>> Handle(
        Query$FeatureName$ request,
        CancellationToken cancellationToken)
    {
        var entity = await repository.FindAsync(request.Id, cancellationToken: cancellationToken);
        if (entity is null)
        {
            return Res.Fail("$FeatureName$ target was not found.");
        }

        return Res.Ok(new $ResponseName$
        {
            Id = entity.Id
        });
    }
}
```

## Minimal Command Example

```csharp
using Monica.Core.Results;
using Monica.ProjectUnits.Annotations;
using Monica.Application.Abstractions;
using Monica.Application.Annotations;

namespace $ApplicationNamespace$.HandlersCommand;

[ApiEndpoint(
    ApiHttpMethod.Post,
    "$RequestRoute$",
    Binding = ApiRequestBinding.Body)]
[ProjectUnitMetadata(
    "$FeatureName$ Command",
    Description = "Requests the $FeatureName$ use case.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed record Command$FeatureName$(long Id) : IResultRequest;

[ProjectUnitMetadata(
    "$FeatureName$",
    Description = "Coordinates the $FeatureName$ command boundary.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class CommandHandler$FeatureName$(IRepositoryOrder repository)
    : ApplicationService<Command$FeatureName$>
{
    public override async Task<Res> Handle(
        Command$FeatureName$ request,
        CancellationToken cancellationToken)
    {
        var order = await repository.GetAsync(request.Id, cancellationToken);
        order.Approve();
        return Res.Ok();
    }
}
```

## Notes

- If the success payload type is `string`, use `Res.Ok<string>(value)`.
- If a query becomes complex because of filtering, paging, or policy checks, extract that logic before the handler turns procedural.
- RPC publication is namespace-driven, not handler-driven. Do not move a local request into `PublishedLanguages` merely to share a DTO.
