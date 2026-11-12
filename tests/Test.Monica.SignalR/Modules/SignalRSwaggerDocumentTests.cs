using System.Reflection;
using global::ApplicationModel.Domain;
using global::ApplicationModel.Role;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Authority.Identity.Abstractions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Monica.SignalR.Abstractions;
using Monica.SignalR.Metrics;
using Monica.Testing.Hosting;
using Monica.WebApi.Swagger.Models;
using SignalRSwaggerGen;
using SignalRSwaggerGen.Attributes;
using SignalRSwaggerGen.Enums;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;

[assembly: ApplicationRole("ordering.realtime", DomainId.Ordering)]

namespace Test.Monica.SignalR.Modules;

public sealed class SignalRSwaggerDocumentTests
{
    private const string REALTIME = "role.ordering.realtime";
    private const string WORKER = "role.ordering.worker";
    private const string ECHO = "/hubs/realtime/Echo";

    [Fact]
    public async Task Swagger_WhenApplicationModelOwnsHub_ShouldRouteDeclaredAndInheritedMethodsToItsRole()
    {
        await using var host = await new SwaggerFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var swagger = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        var realtime = await swagger.GetSwaggerAsync(REALTIME);
        Assert.Contains(ECHO, realtime.Paths.Keys);
        Assert.Contains("/hubs/realtime/OnConnectedAsync", realtime.Paths.Keys);
        Assert.Equal(typeof(Hub), typeof(RealtimeHub).GetMethod(nameof(Hub.OnConnectedAsync))!.DeclaringType);
        foreach (var document in new[] { WORKER, "v1", "monica" })
        {
            var paths = (await swagger.GetSwaggerAsync(document)).Paths.Keys;
            Assert.DoesNotContain(ECHO, paths);
            Assert.DoesNotContain("/hubs/realtime/OnConnectedAsync", paths);
        }
        Assert.Contains("/swagger-test/http", (await swagger.GetSwaggerAsync("v1")).Paths.Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Swagger_WhenScannedRoleIsRemote_ShouldExcludeItsHubsEvenWithExplicitDocuments(bool available)
    {
        await using var host = await new SwaggerFactory(active: false, available: available)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var documents = host.Services.GetRequiredService<IOptions<SwaggerGenOptions>>().Value.SwaggerGeneratorOptions.SwaggerDocs;
        Assert.DoesNotContain(REALTIME, documents.Keys);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        foreach (var document in new[] { WORKER, "v1", "monica", "explicit", "grouped" })
        {
            var paths = (await provider.GetSwaggerAsync(document)).Paths.Keys;
            Assert.DoesNotContain(ECHO, paths);
            Assert.DoesNotContain("/hubs/explicit/Echo", paths);
            Assert.DoesNotContain("/hubs/grouped/Echo", paths);
            Assert.DoesNotContain("/hubs/all/Echo", paths);
        }
    }

    [Fact]
    public async Task Swagger_WhenPackageNarrowsCandidateDocuments_ShouldStillApplyNativeRoleOwnership()
    {
        await using var host = await new SwaggerFactory(package: options => options.DisplayInDocuments(REALTIME, WORKER))
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        Assert.Contains(ECHO, (await provider.GetSwaggerAsync(REALTIME)).Paths.Keys);
        Assert.DoesNotContain(ECHO, (await provider.GetSwaggerAsync(WORKER)).Paths.Keys);
        Assert.DoesNotContain(ECHO, (await provider.GetSwaggerAsync("v1")).Paths.Keys);
    }

    [Fact]
    public async Task Swagger_WhenHubHasExplicitTargetsOrRecognizedGroup_ShouldHonorOverridesAndHostExclusions()
    {
        await using var host = await new SwaggerFactory(configure: options =>
            options.ExtendSwaggerGenAction = swagger => swagger.DocInclusionPredicate((_, description) =>
                description.RelativePath != "/hubs/explicit/Hidden"))
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        var explicitDocument = await provider.GetSwaggerAsync("explicit");
        Assert.Contains("/hubs/explicit/Echo", explicitDocument.Paths.Keys);
        Assert.DoesNotContain("/hubs/explicit/Hidden", explicitDocument.Paths.Keys);
        Assert.Contains("/hubs/grouped/Echo", (await provider.GetSwaggerAsync("grouped")).Paths.Keys);
        var realtime = await provider.GetSwaggerAsync(REALTIME);
        Assert.DoesNotContain("/hubs/explicit/Echo", realtime.Paths.Keys);
        Assert.DoesNotContain("/hubs/grouped/Echo", realtime.Paths.Keys);
    }

    [Fact]
    public async Task Swagger_WhenHubExplicitlySelectsAllDocuments_ShouldPreserveThePackageOverride()
    {
        await using var host = await new SwaggerFactory(package: options => options.DisplayInDocument(REALTIME))
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        foreach (var document in new[] { REALTIME, WORKER, "v1", "monica", "explicit", "grouped" })
            Assert.Contains("/hubs/all/Echo", (await provider.GetSwaggerAsync(document)).Paths.Keys);
        Assert.DoesNotContain(ECHO, (await provider.GetSwaggerAsync(WORKER)).Paths.Keys);
    }

    [Fact]
    public async Task Swagger_WhenResolverOrHostDocumentOverridesRouting_ShouldRetainThoseContracts()
    {
        await using var host = await new SwaggerFactory(configure: options =>
        {
            options.DocumentNameResolver = description => description.RelativePath == ECHO ? "GROUPED" : null;
            options.ExtendSwaggerGenAction = swagger =>
            {
                // Deliberately duplicate titles: document identity comes from SchemaRepository, not Info.Title.
                swagger.SwaggerDoc("external", new OpenApiInfo { Title = "ordering.realtime", Version = "v1" });
                swagger.DocInclusionPredicate((_, _) => true);
            };
        }).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        Assert.Contains(ECHO, (await provider.GetSwaggerAsync("grouped")).Paths.Keys);
        Assert.DoesNotContain(ECHO, (await provider.GetSwaggerAsync(REALTIME)).Paths.Keys);
        Assert.Contains(ECHO, (await provider.GetSwaggerAsync("external")).Paths.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Swagger_WhenStandalone_ShouldPreservePackageDocumentSelection(bool restrict)
    {
        await using var host = await new SwaggerFactory(model: false, package: options =>
        {
            if (restrict) options.DisplayInDocument("v1");
        }).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        Assert.Contains(ECHO, (await provider.GetSwaggerAsync("v1")).Paths.Keys);
        Assert.Equal(!restrict, (await provider.GetSwaggerAsync("monica")).Paths.ContainsKey(ECHO));
    }

    [Fact]
    public async Task Swagger_WhenDescriptionIsPackageOnlyInterface_ShouldPreserveExplicitSelection()
    {
        await using var host = await new SwaggerFactory(active: false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = host.Services.GetRequiredService<IAsyncSwaggerProvider>();
        Assert.Contains("/contract/Notify", (await provider.GetSwaggerAsync("explicit")).Paths.Keys);
        Assert.DoesNotContain("/contract/Notify", (await provider.GetSwaggerAsync("v1")).Paths.Keys);
    }

    private sealed class SwaggerFactory(bool model = true, bool active = true, bool available = true,
        Action<SignalRSwaggerGenOptions>? package = null, Action<ModuleSwaggerOption>? configure = null)
        : MonicaTestApplicationFactory<SignalRSwaggerDocumentTests>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => model
            ? available
                ? [typeof(SignalRSwaggerDocumentTests).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly]
                : [typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly]
            : [typeof(SignalRSwaggerDocumentTests).Assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder) => builder.Services.AddEndpointsApiExplorer();

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => { options.AppId = "local"; options.AutonomousWork = false; });
            if (model)
            {
                builder.AddApplicationModel(options =>
                {
                    options.ManifestType = available ? typeof(SwaggerManifest) : typeof(PartialSwaggerManifest);
                    options.Applications = new Dictionary<string, ApplicationPlacement>
                    {
                        ["local"] = new() { Roles = active ? ["ordering.worker", "ordering.realtime"] : ["ordering.worker"] },
                        ["remote"] = new() { Roles = active ? [] : ["ordering.realtime"] }
                    };
                });
            }
            builder.AddSwagger(options =>
            {
                options.DisableXmlDocumentation = true;
                options.AdditionalDocuments.Add(new SwaggerDocumentDescriptor { Name = "explicit" });
                options.AdditionalDocuments.Add(new SwaggerDocumentDescriptor { Name = "grouped" });
                options.DocumentNameResolver = description => description.RelativePath == "swagger-test/http" ? "v1" : null;
                configure?.Invoke(options);
            });
            var signalR = builder.AddSignalR()
                .AddSignalR<ISignalRHubOperator<IClient, ICurrentUser>, TestHubOperator, IClient, ICurrentUser>()
                .AddSignalRSwagger(options =>
                {
                    options.ScanAssembly(typeof(RealtimeHub).Assembly);
                    package?.Invoke(options);
                });
            if (active) signalR.MapSignalRHub<RealtimeHub>("/hubs/realtime");
            builder.AddModule<HttpEndpointModule, HttpEndpointOptions>();
        }
    }

    public class SwaggerManifest : IApplicationManifest
    {
        public IReadOnlyList<ApplicationDomainDefinition> Domains => [new("Ordering", typeof(DomainDeclaration).Assembly.GetName().Name!)];
        public IReadOnlyList<ApplicationRoleDefinition> Roles =>
        [
            new("ordering.worker", "Ordering", typeof(OrdinaryHandler).Assembly.GetName().Name!),
            new("ordering.realtime", "Ordering", typeof(RealtimeHub).Assembly.GetName().Name!)
        ];
        public virtual IReadOnlyList<string> LocalAssemblies => [.. Domains.Select(domain => domain.AssemblyName), .. Roles.Select(role => role.AssemblyName)];
        public IReadOnlyList<ApplicationOperationDefinition> Operations =>
        [
            new(typeof(OrdinaryQuery), typeof(OrdinaryHandler).FullName!, typeof(OrdinaryHandler).Assembly.GetName().Name!, "Ordering", "ordering.worker", null),
            new(typeof(RoleQuery), typeof(RequiredHandler).FullName!, typeof(RequiredHandler).Assembly.GetName().Name!, "Ordering", "ordering.worker", "ordering.worker")
        ];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }

    public sealed class PartialSwaggerManifest : SwaggerManifest
    {
        public override IReadOnlyList<string> LocalAssemblies => [typeof(DomainDeclaration).Assembly.GetName().Name!, typeof(OrdinaryHandler).Assembly.GetName().Name!];
    }

    [SignalRHub("/hubs/realtime", autoDiscover: AutoDiscover.MethodsAndParams, hubMethodsScan: HubMethodsScan.IncludeInherited)]
    public sealed class RealtimeHub : Hub
    {
        public string Echo(string value) => value;
    }

    [SignalRHub("/hubs/explicit", autoDiscover: AutoDiscover.MethodsAndParams, documentNames: new[] { "explicit" })]
    public sealed class ExplicitHub : Hub
    {
        public string Echo(string value) => value;
        public string Hidden(string value) => value;
    }

    [SignalRHub("/hubs/grouped", autoDiscover: AutoDiscover.MethodsAndParams, tag: "grouped")]
    public sealed class GroupedHub : Hub
    {
        public string Echo(string value) => value;
    }

    [SignalRHub("/hubs/all", autoDiscover: AutoDiscover.MethodsAndParams, documentNames: new string[0])]
    public sealed class AllDocumentsHub : Hub
    {
        public string Echo(string value) => value;
    }

    public interface IBaseContract { Task Notify(string value); }

    [SignalRHub("/contract", autoDiscover: AutoDiscover.MethodsAndParams, documentNames: new[] { "explicit" }, hubMethodsScan: HubMethodsScan.IncludeInherited)]
    public interface IDocumentedContract : IBaseContract;

    private interface IClient : ISignalRHubContract;

    private sealed class OperatorHub(ISignalRConnectionRegistry registry, ILogger<OperatorHub> logger)
        : SignalRHub<IClient>(registry, logger);

    private sealed class TestHubOperator(IHubContext<OperatorHub, IClient> context,
        ISignalRConnectionRegistry registry, SignalRSendMetrics metrics)
        : CurrentUserSignalRHubOperator<IClient, OperatorHub>(context, registry, metrics);

    private sealed class HttpEndpointModule : MonicaModule<HttpEndpointOptions>, IWebHostRequiredModule
    {
        public override void ConfigureEndpoints(WebModuleContext<HttpEndpointOptions> context)
            => context.ApplicationBuilder.UseEndpoints(endpoints => endpoints.MapGet("/swagger-test/http", () => "http"));
    }

    private sealed class HttpEndpointOptions : ModuleOptions<HttpEndpointModule>;
}
