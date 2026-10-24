using System.Reflection;
using global::ApplicationInvocation.Fixtures;
using global::ApplicationModel.Domain;
using global::ApplicationModel.Role;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Monica.ApplicationModel.Models;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Monica.WebApi.Swagger.Models;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;

namespace Test.Monica.ApplicationInvocation;

public sealed class SwaggerRoleDocumentTests
{
    private const string WORKER_DOCUMENT = "role.ordering.worker";
    private const string INVOCATION_DOCUMENT = "role.ordering.invocation";

    [Fact]
    public async Task Swagger_WhenManualHostContainsMixedRoleActionsAndInheritedCrud_ShouldUseEachMappedOperationOwner()
    {
        await using var host = await new SwaggerFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var swagger = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        var worker = await swagger.GetSwaggerAsync(WORKER_DOCUMENT);
        var invocation = await swagger.GetSwaggerAsync(INVOCATION_DOCUMENT);
        var business = await swagger.GetSwaggerAsync("v1");
        var framework = await swagger.GetSwaggerAsync("monica");

        Assert.Contains("/invocation-selection/worker", worker.Paths.Keys);
        Assert.DoesNotContain("/invocation-selection/ordinary", worker.Paths.Keys);
        Assert.Contains("/invocation-selection/ordinary", invocation.Paths.Keys);
        Assert.Contains("/invocation-selection/constrained", invocation.Paths.Keys);
        Assert.DoesNotContain("/invocation-selection/worker", invocation.Paths.Keys);
        Assert.Contains("/role-owned/manual", invocation.Paths.Keys);
        Assert.Contains("/api/v1/Ordering/ledger", invocation.Paths.Keys);
        Assert.Contains("/api/v1/Ordering/inherited-ledger", invocation.Paths.Keys);
        Assert.Contains("/invocation-selection/unrelated", business.Paths.Keys);
        Assert.Contains("/role-swagger/default", business.Paths.Keys);
        Assert.Contains("/role-swagger/framework", framework.Paths.Keys);
        Assert.DoesNotContain("/role-swagger/framework", business.Paths.Keys);
        Assert.Equal("ordering.worker", worker.Info.Title);
        Assert.Equal("ordering.invocation", invocation.Info.Title);

        var inherited = Descriptions(host).Single(description => description.ActionDescriptor is ControllerActionDescriptor action
            && action.ControllerTypeInfo.AsType() == typeof(InheritedLedgerCrudService));
        var inheritedAction = Assert.IsType<ControllerActionDescriptor>(inherited.ActionDescriptor);
        Assert.Equal(typeof(SharedCrudServiceBase), inheritedAction.MethodInfo.DeclaringType);
        Assert.NotEqual(inheritedAction.ControllerTypeInfo.Assembly, inheritedAction.MethodInfo.DeclaringType!.Assembly);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().OrdinaryCalls);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().ConstrainedCalls);
    }

    [Fact]
    public async Task Swagger_WhenAvailableRoleIsPlacedRemotely_ShouldNotRegisterItsDocumentOrRestoreItsReceivers()
    {
        await using var host = await new SwaggerFactory(colocated: false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var documents = host.Services.GetRequiredService<IOptions<SwaggerGenOptions>>().Value.SwaggerGeneratorOptions.SwaggerDocs;
        Assert.Contains(WORKER_DOCUMENT, documents.Keys);
        Assert.DoesNotContain(INVOCATION_DOCUMENT, documents.Keys);
        var worker = await host.Services.GetRequiredService<IAsyncSwaggerProvider>().GetSwaggerAsync(WORKER_DOCUMENT);
        Assert.Contains("/invocation-selection/worker", worker.Paths.Keys);
        Assert.DoesNotContain(Descriptions(host), description => description.RelativePath == "invocation-selection/ordinary"
            || description.RelativePath == "role-owned/manual" || description.RelativePath == "api/v1/Ordering/ledger");
    }

    [Fact]
    public async Task Swagger_WhenSelectedRolesHaveNoMappedOperations_ShouldKeepPredictableEmptyDocuments()
    {
        await using var host = await new SwaggerFactory(receivers: false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var swagger = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        Assert.Empty((await swagger.GetSwaggerAsync(WORKER_DOCUMENT)).Paths);
        Assert.Empty((await swagger.GetSwaggerAsync(INVOCATION_DOCUMENT)).Paths);
        Assert.Contains("/role-swagger/default", (await swagger.GetSwaggerAsync("v1")).Paths.Keys);
    }

    [Fact]
    public async Task Swagger_WhenResolverAndRecognizedGroupOverrideOwnership_ShouldHonorExistingExplicitRouting()
    {
        await using var host = await new SwaggerFactory(groupConstrained: true, configure: options =>
        {
            options.AdditionalDocuments.Add(new SwaggerDocumentDescriptor { Name = "selected" });
            options.AdditionalDocuments.Add(new SwaggerDocumentDescriptor { Name = "grouped" });
            options.DocumentNameResolver = description => description.RelativePath == "invocation-selection/ordinary"
                ? "SELECTED" : description.RelativePath == "invocation-selection/worker" ? "unknown-document" : null;
        }).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var swagger = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        Assert.Contains("/invocation-selection/ordinary", (await swagger.GetSwaggerAsync("selected")).Paths.Keys);
        Assert.Contains("/invocation-selection/constrained", (await swagger.GetSwaggerAsync("grouped")).Paths.Keys);
        var invocation = await swagger.GetSwaggerAsync(INVOCATION_DOCUMENT);
        Assert.DoesNotContain("/invocation-selection/ordinary", invocation.Paths.Keys);
        Assert.DoesNotContain("/invocation-selection/constrained", invocation.Paths.Keys);
        Assert.Contains("/invocation-selection/worker", (await swagger.GetSwaggerAsync(WORKER_DOCUMENT)).Paths.Keys);
    }

    [Fact]
    public async Task Swagger_WhenHostPredicateExcludesOneOperation_ShouldRetainNativeRoutingAndOwnExternalDocuments()
    {
        await using var host = await new SwaggerFactory(configure: options => options.ExtendSwaggerGenAction = swagger =>
        {
            swagger.SwaggerDoc("external", new OpenApiInfo { Title = "Host-owned", Version = "v1" });
            swagger.DocInclusionPredicate((_, description) => description.RelativePath != "invocation-selection/constrained");
        }).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        var worker = await provider.GetSwaggerAsync(WORKER_DOCUMENT);
        var invocation = await provider.GetSwaggerAsync(INVOCATION_DOCUMENT);
        var external = await provider.GetSwaggerAsync("external");
        Assert.DoesNotContain("/invocation-selection/ordinary", worker.Paths.Keys);
        Assert.DoesNotContain("/invocation-selection/worker", invocation.Paths.Keys);
        Assert.Contains("/invocation-selection/ordinary", invocation.Paths.Keys);
        Assert.DoesNotContain("/invocation-selection/constrained", invocation.Paths.Keys);
        Assert.Contains("/invocation-selection/worker", external.Paths.Keys);
        Assert.Contains("/invocation-selection/ordinary", external.Paths.Keys);
        Assert.DoesNotContain("/invocation-selection/constrained", external.Paths.Keys);
    }

    [Theory]
    [InlineData("role.ordering.worker")]
    [InlineData("ROLE.ORDERING.WORKER")]
    public async Task Swagger_WhenConfiguredDocumentCollidesWithRoleDocument_ShouldRejectAmbiguousNames(string name)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new SwaggerFactory(configure: options =>
            options.AdditionalDocuments.Add(new SwaggerDocumentDescriptor { Name = name }))
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Swagger document names must be unique", error.ToString());
    }

    [Fact]
    public async Task Swagger_WhenExtensionAddsCaseCollidingDocument_ShouldRejectAmbiguousNames()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var host = await new SwaggerFactory(configure: options => options.ExtendSwaggerGenAction = swagger =>
                swagger.SwaggerDoc("ROLE.ORDERING.WORKER", new OpenApiInfo { Title = "Conflicting", Version = "v1" }))
                .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.Services.GetRequiredService<IAsyncSwaggerProvider>().GetSwaggerAsync(WORKER_DOCUMENT);
        });
        Assert.Contains("Swagger document names must be unique", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Swagger_WhenNoApplicationModelIsRegistered_ShouldRetainExistingBusinessAndFrameworkDefaults(bool frameworkEnabled)
    {
        await using var host = await new SwaggerFactory(model: false, receivers: false,
            configure: options => options.Monica.Enabled = frameworkEnabled)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var documents = host.Services.GetRequiredService<IOptions<SwaggerGenOptions>>().Value.SwaggerGeneratorOptions.SwaggerDocs;
        Assert.DoesNotContain(documents.Keys, name => name.StartsWith("role.", StringComparison.Ordinal));
        Assert.Equal(frameworkEnabled ? 2 : 1, documents.Count);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        var business = await provider.GetSwaggerAsync("v1");
        Assert.Contains("/role-swagger/default", business.Paths.Keys);
        var framework = frameworkEnabled ? await provider.GetSwaggerAsync("monica") : business;
        Assert.Contains("/role-swagger/framework", framework.Paths.Keys);
    }

    private static IEnumerable<ApiDescription> Descriptions(MonicaTestApplication host)
        => host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items);

    private sealed class SwaggerFactory(bool model = true, bool colocated = true, bool receivers = true,
        bool groupConstrained = false, Action<ModuleSwaggerOption>? configure = null)
        : MonicaTestApplicationFactory<SwaggerRoleDocumentTests>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => model
            ? [typeof(SwaggerRoleDocumentTests).Assembly, typeof(DomainDeclaration).Assembly,
                typeof(OrdinaryHandler).Assembly, typeof(OrdinaryApiHandler).Assembly]
            : [typeof(SwaggerRoleDocumentTests).Assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder) => builder.Services.AddEndpointsApiExplorer();

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => { options.AppId = "local"; options.AutonomousWork = false; });
            if (model)
            {
                builder.AddApplicationModel(options =>
                {
                    options.ManifestType = typeof(InvocationScenarioManifest);
                    options.Deployments = new Dictionary<string, ApplicationPlacement>
                    {
                        ["local"] = new() { Roles = colocated ? ["ordering.worker", "ordering.invocation"] : ["ordering.worker"] },
                        ["remote"] = new() { Roles = colocated ? [] : ["ordering.invocation"] }
                    };
                });
                builder.AddApplication();
            }
            if (receivers) builder.AddAutoControllers(configureCrud: options => options.RoutePath = "api/v1/{domain}");
            if (groupConstrained) builder.AddControllers().ConfigMvcOption(options => options.Conventions.Add(new GroupConstrainedAction()));
            builder.AddSwagger(options => { options.DisableXmlDocumentation = true; configure?.Invoke(options); });
            builder.AddModule<SwaggerTestEndpoints, SwaggerTestEndpointsOptions>();
        }
    }

    private sealed class GroupConstrainedAction : IApplicationModelConvention
    {
        public void Apply(Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel application)
        {
            foreach (var action in application.Controllers.SelectMany(controller => controller.Actions))
                if (action.ActionMethod.DeclaringType == typeof(ApplicationApiReceiverController)
                    && action.ActionMethod.Name == nameof(ApplicationApiReceiverController.Constrained))
                    action.ApiExplorer.GroupName = "grouped";
        }
    }

    private sealed class SwaggerTestEndpoints : MonicaModule<SwaggerTestEndpointsOptions>, IWebHostRequiredModule
    {
        public override void ConfigureEndpoints(WebModuleContext<SwaggerTestEndpointsOptions> context)
            => context.ApplicationBuilder.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/role-swagger/default", () => "default");
                endpoints.MapGet("/role-swagger/framework", () => "framework")
                    .WithMetadata(new MonicaEndpointMetadata(MonicaEndpointKind.MinimalApi));
            });
    }

    private sealed class SwaggerTestEndpointsOptions : ModuleOptions<SwaggerTestEndpoints>;
}
