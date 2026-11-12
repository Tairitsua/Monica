using System.Reflection;
using ApplicationModel.Domain;
using ApplicationModel.Role;
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ApplicationModel;

public sealed class SelectiveApplicationModelTests
{
    [Fact]
    public async Task Compose_WhenCatalogContainsRemoteImplementations_ShouldLoadOnlyDeclaredLocalBindings()
    {
        await using var app = await new SelectiveFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var model = app.Services.GetRequiredService<IApplicationModel>();

        Assert.True(model.Domains.ContainsKey("Vision"));
        Assert.Equal("ocr", model.ResolveRoleAppId("vision.ocr"));
        Assert.False(model.IsRoleAvailable("vision.ocr"));
        Assert.False(model.IsRoleActive("vision.ocr"));
        Assert.True(model.IsRoleAvailable("ordering.worker"));
        Assert.False(model.IsRoleActive("ordering.worker"));
        Assert.False(model.LocalHandlers.ContainsKey(typeof(ExternalQuery)));
        Assert.Equal(typeof(ApiHandler), model.LocalHandlers[typeof(ApiQuery)]);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "ApplicationModel.UnavailableRole");

        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        Assert.Equal("ordinary", await scope.Resolve<IMediator>().Send(new OrdinaryQuery(), scope.CancellationToken));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Resolve<IMediator>().Send(new ExternalQuery(), scope.CancellationToken));
        Assert.Contains("no local implementation", error.Message);
    }

    [Fact]
    public async Task Compose_WhenAnOmittedRoleIsAssignedHere_ShouldFailBeforeStarting()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new SelectiveFactory("ocr").CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Active role 'vision.ocr' has no implementation", error.ToString());
    }

    [Fact]
    public async Task Compose_WhenLocalInventoryHidesADiscoveredRole_ShouldRejectTheInconsistentManifest()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new SelectiveFactory(manifestType: typeof(InconsistentSelectiveManifest))
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("absent from the local compiled manifest", error.ToString());
    }

    private sealed class SelectiveFactory(string appId = "api", Type? manifestType = null)
        : MonicaTestApplicationFactory<ApiQuery>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies =>
            [typeof(ApiQuery).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly];

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AppId = appId);
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = manifestType ?? typeof(SelectiveManifest);
                options.Applications = new Dictionary<string, ApplicationPlacement>
                {
                    ["api"] = new() { Roles = ["ordering.api"] },
                    ["worker"] = new() { Roles = ["ordering.worker"] },
                    ["ocr"] = new() { Roles = ["vision.ocr"] }
                };
            });
            builder.AddMediator();
            builder.AddDependencyInjection();
        }
    }
}

public sealed record ExternalQuery : IRequest<string>;

[RunOnRole("vision.ocr")]
public sealed record ExternalRequiredQuery : IRequest<string>;

public class SelectiveManifest : IApplicationManifest
{
    private readonly IApplicationManifest _local = new ScenarioManifest();
    public virtual IReadOnlyList<string> LocalAssemblies => _local.LocalAssemblies;
    public IReadOnlyList<ApplicationDomainDefinition> Domains =>
        [.. _local.Domains, new("Vision", "ApplicationModel.UnavailableDomain")];
    public IReadOnlyList<ApplicationRoleDefinition> Roles =>
        [.. _local.Roles, new("vision.ocr", "Vision", "ApplicationModel.UnavailableRole")];
    public IReadOnlyList<ApplicationOperationDefinition> Operations =>
    [
        .. _local.Operations,
        new(typeof(ExternalQuery), "External.QueryHandler", "ApplicationModel.UnavailableRole", "Vision", "vision.ocr", null),
        new(typeof(ExternalRequiredQuery), "External.RequiredHandler", "ApplicationModel.UnavailableRole", "Vision", "vision.ocr", "vision.ocr")
    ];
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
}

public sealed class InconsistentSelectiveManifest : SelectiveManifest
{
    public override IReadOnlyList<string> LocalAssemblies => [typeof(DomainDeclaration).Assembly.GetName().Name!];
}
