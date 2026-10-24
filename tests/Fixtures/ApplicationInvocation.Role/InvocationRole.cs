using global::ApplicationModel.Domain;
using Microsoft.AspNetCore.Mvc;
using Monica.Application.Abstractions;
using Monica.Application.Annotations;
using Monica.ApplicationModel.Annotations;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;

[assembly: ApplicationRole("ordering.invocation", DomainId.Ordering)]
[assembly: HttpApiContract("api/v1", DomainName = "Ordering")]

namespace ApplicationInvocation.Fixtures;

/// <summary>Reads ordinary state without requiring its ingress role to be active.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "invocation/ordinary")]
public sealed record QueryOrdinary(string Name) : IResultRequest<int>;

/// <summary>Executes an operation requiring its role's runtime ownership.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "invocation/constrained")]
[RunOnRole("ordering.invocation")]
public sealed record CommandConstrained(int Value) : IResultRequest<int>;

public sealed class InvocationLedger : ISingletonDependency
{
    private int _ordinary;
    private int _constrained;
    public int OrdinaryCalls => Volatile.Read(ref _ordinary);
    public int ConstrainedCalls => Volatile.Read(ref _constrained);
    public void RecordOrdinary() => Interlocked.Increment(ref _ordinary);
    public void RecordConstrained() => Interlocked.Increment(ref _constrained);
}

public sealed class LedgerCrudService : ICrudApplicationService
{
    public Res<int> Get() => Res.Ok(43);
}

public sealed class InheritedLedgerCrudService : SharedCrudServiceBase, ICrudApplicationService;

[ApiController, Route("role-owned/manual")]
public sealed class RoleOwnedController : ControllerBase
{
    [HttpGet]
    public string Get() => "role-owned";
}

public sealed class OrdinaryApiHandler(InvocationLedger calls) : ApplicationService<QueryOrdinary, int>
{
    public override Task<Res<int>> Handle(QueryOrdinary request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.RecordOrdinary();
        return Task.FromResult(Res.Ok(41));
    }
}

public sealed class ConstrainedApiHandler(InvocationLedger calls) : ApplicationService<CommandConstrained, int>
{
    public override Task<Res<int>> Handle(CommandConstrained request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.RecordConstrained();
        return Task.FromResult(Res.Ok(42));
    }
}
