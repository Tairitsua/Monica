using ApplicationModel.Domain;
using Microsoft.Extensions.Hosting;
using Monica.ApplicationModel.Annotations;
using Monica.Core.Mediator;
using Monica.DependencyInjection.Abstractions;
using Monica.Core.TypeDiscovery.Annotations;

[assembly: ApplicationRole("ordering.worker", DomainId.Ordering)]

namespace ApplicationModel.Role;

public sealed record OrdinaryQuery : IRequest<string>;
[RunOnRole("ordering.worker")]
public sealed record RoleQuery : IRequest<string>;
public sealed class OrdinaryHandler : IRequestHandler<OrdinaryQuery, string>, ITransientDependency
{
    public Task<string> Handle(OrdinaryQuery request, CancellationToken cancellationToken) => Task.FromResult("ordinary");
}
public sealed class RequiredHandler : IRequestHandler<RoleQuery, string>, ITransientDependency
{
    public Task<string> Handle(RoleQuery request, CancellationToken cancellationToken) => Task.FromResult("required");
}
public sealed class Worker : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[ProcessLocal]
public sealed class WorkerLocalInitialization;
