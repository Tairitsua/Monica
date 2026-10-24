using System.Reflection;
using System.Text.Json;
using ApplicationModel.Domain;
using ApplicationModel.Role;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Models;
using Monica.Core;
using Monica.Core.Configuration;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ApplicationModel;

public sealed class ApplicationDeploymentCompositionTests
{
    private const string SPLIT = """{"Application":{"Deployments":{"api":{"AppName":"Order API","Roles":["ordering.api"]},"worker":{"Roles":["ordering.worker"]},"idle":{"Roles":[]}}}}""";
    private const string COMBINED = """{"Application":{"Deployments":{"api":{"AppName":"Combined orders","Roles":["ordering.api","ordering.worker"]},"idle":{"Roles":[]}}}}""";

    [Fact]
    public async Task Configuration_WhenAHigherProviderSuppliesDeployments_ShouldSelectItsWholeMapAndProjectIdentity()
    {
        await using var app = await new DeploymentFactory([SPLIT, COMBINED])
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var model = app.Services.GetRequiredService<IApplicationModel>();
        var deployment = app.Services.GetRequiredService<ApplicationDeployment>();

        Assert.Same(deployment, model.Deployment);
        Assert.Equal("Combined orders", deployment.AppName);
        Assert.Equal("Combined orders", app.Services.GetRequiredService<MonicaApplication>().Application.AppName);
        Assert.Equal(new[] { "api", "idle" }, deployment.Deployments.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(deployment.Deployments["idle"].Roles);
        Assert.Equal("idle", deployment.Deployments["idle"].AppName);
        Assert.Equal("api", model.ResolveRoleAppId("ordering.worker"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)deployment.Deployments["api"].Roles)[0] = "changed");
    }

    [Fact]
    public async Task Configuration_WhenSelectedProviderIsIncomplete_ShouldFailWithoutMergingLowerEntries()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new DeploymentFactory([SPLIT,
            """{"Application":{"Deployments":{"api":{"Roles":["ordering.api"]}}}}"""])
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("ordering.worker", error.ToString());
        Assert.Contains("has no owner", error.ToString());
    }

    [Theory]
    [InlineData("{\"worker\":{\"Roles\":[\"ordering.api\",\"ordering.worker\"]}}", "Current AppId")]
    [InlineData("{\"api\":{\"Roles\":[\"ordering.api\"]}}", "has no owner")]
    [InlineData("{\"api\":{\"Roles\":[\"ordering.api\",\"ordering.worker\",\"unknown\"]}}", "unknown role")]
    [InlineData("{\"api\":{\"Roles\":null}}", "explicit list")]
    public async Task Configuration_WhenSelectedInputFailsValidation_ShouldPreserveItsOriginAndInnerFailure(
        string selected, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"monica-topology-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path,
                "{\"Credentials\":{\"Password\":\"do-not-disclose-input-values\"},\"Application\":{\"Deployments\":" + selected + "}}",
                TestContext.Current.CancellationToken);
            var factory = new DeploymentFactory([SPLIT], jsonFile: path);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => factory
                .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
            var failure = Assert.Single(FailureChain(error).OfType<InvalidOperationException>(),
                candidate => candidate.Message.StartsWith("Application model validation failed using deployment input", StringComparison.Ordinal));

            Assert.Contains("Application:Deployments", failure.Message);
            Assert.Contains("JsonConfigurationProvider", failure.Message);
            Assert.Contains(Path.GetFileName(path), failure.Message);
            Assert.Contains($"at '{factory.SelectedProviderPath}'", failure.Message);
            Assert.Contains(expected, Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.DoesNotContain("do-not-disclose-input-values", error.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Manifest_WhenConstructionFails_ShouldNotLabelItAsADeploymentInputFailure()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new DeploymentFactory([SPLIT],
                configure: options => options.ManifestType = typeof(FailingManifest))
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Manifest constructor failure", error.ToString());
        Assert.DoesNotContain("Application model validation failed using deployment input", error.ToString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"api\":{\"AppName\":\"Missing roles\"}}")]
    [InlineData("{\"api\":{\"Roles\":null}}")]
    [InlineData("{\"api\":{\"Roles\":{}}}")]
    [InlineData("{\"api\":{\"Roles\":[\"ordering.api\",\"ordering.worker\"],\"Unknown\":true}}")]
    [InlineData("{\"api\":{\"AppName\":null,\"Roles\":[\"ordering.api\",\"ordering.worker\"]}}")]
    public async Task Configuration_WhenSelectedValueIsInvalid_ShouldNotFallBack(string selected)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new DeploymentFactory([SPLIT,
            "{\"Application\":{\"Deployments\":" + selected + "}}"])
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("application", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Configuration_WhenTypedAndProviderTopologyAreBothSupplied_ShouldRejectDualAuthority()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new DeploymentFactory([SPLIT], Typed())
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("cannot be supplied both", error.ToString());
    }

    [Fact]
    public async Task Configuration_WhenDirectAppNameContradictsPlacement_ShouldFailBeforeServices()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new DeploymentFactory([SPLIT], directName: "Other name")
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("AppName", error.ToString());
    }

    [Fact]
    public async Task TypedOptions_WhenDefaultsAndHostContributionsExist_ShouldFinalizeOnceInNormalOrder()
    {
        var calls = 0;
        var factory = new DeploymentFactory([], configure: options =>
        {
            calls++;
            Assert.Equal("Default name", options.Deployments!["api"].AppName);
            options.Deployments = Typed();
        }, useDependencyDefaults: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Equal("Typed name", app.Services.GetRequiredService<ApplicationDeployment>().AppName);
    }

    [Theory]
    [InlineData("missing-roles", "must declare its roles")]
    [InlineData("case-app", "AppId 'API'")]
    [InlineData("case-role", "ambiguous")]
    [InlineData("unknown", "unknown role")]
    [InlineData("empty-name", "AppName")]
    public async Task TypedOptions_WhenPlacementIsInvalid_ShouldRejectIt(string defect, string expected)
    {
        var values = Typed();
        switch (defect)
        {
            case "missing-roles": values["api"].Roles = null; break;
            case "case-app": values["API"] = new() { Roles = [] }; break;
            case "case-role": values["worker"].Roles = ["ORDERING.API", "ordering.worker"]; break;
            case "unknown": values["worker"].Roles = ["missing"]; break;
            case "empty-name": values["api"].AppName = " "; break;
        }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new DeploymentFactory([], values)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.ToString());
        var failure = Assert.Single(FailureChain(error).OfType<InvalidOperationException>(),
            candidate => candidate.Message.StartsWith("Application model validation failed using deployment input", StringComparison.Ordinal));
        Assert.Contains("ModuleApplicationModelOption.Deployments", failure.Message);
        Assert.NotNull(failure.InnerException);
    }

    [Fact]
    public async Task TypedOptions_WhenOriginalEntriesChange_ShouldKeepThePublishedMapFrozen()
    {
        var values = Typed();
        await using var app = await new DeploymentFactory([], values)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        values["api"].AppName = "Changed name";
        values["api"].Roles![0] = "changed";
        values.Remove("worker");

        var deployment = app.Services.GetRequiredService<ApplicationDeployment>();
        Assert.Equal("Typed name", deployment.AppName);
        Assert.Equal(new[] { "ordering.api" }, deployment.Deployments["api"].Roles);
        Assert.True(deployment.Deployments.ContainsKey("worker"));
    }

    private static Dictionary<string, ApplicationPlacement> Typed() => new(StringComparer.Ordinal)
    {
        ["api"] = new() { AppName = "Typed name", Roles = ["ordering.api"] },
        ["worker"] = new() { Roles = ["ordering.worker"] }
    };

    private static IEnumerable<Exception> FailureChain(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            yield return current;
    }

    private sealed class DeploymentFactory(string[] json,
        IReadOnlyDictionary<string, ApplicationPlacement>? typed = null, string? directName = null,
        Action<ModuleApplicationModelOption>? configure = null, bool useDependencyDefaults = false,
        string? jsonFile = null)
        : MonicaTestApplicationFactory<ApiQuery>
    {
        public string? SelectedProviderPath { get; private set; }

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies =>
            [typeof(ApiQuery).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            foreach (var source in json)
            {
                using var document = JsonDocument.Parse(source);
                builder.Configuration.Sources.Add(new ConfigurationValueProjectionSource(
                    ConfigurationValueProjectionFactory.CreateDocument(document.RootElement)));
            }
            if (jsonFile is not null) builder.Configuration.AddShapeAwareJsonFile(jsonFile, optional: false, reloadOnChange: false);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:AppId"] = "api" });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            SelectedProviderPath = builder.Composition.StartupConfiguration
                .SelectCompleteSection("Application:Deployments")?.Origin.ProviderPath;
            if (directName is not null) builder.ConfigureApplication(options => options.AppName = directName);
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = typeof(ScenarioManifest);
                if (typed is not null) options.Deployments = typed;
                configure?.Invoke(options);
            });
            if (useDependencyDefaults) builder.AddModule<DefaultApplicationModule, DefaultApplicationOptions>();
        }
    }

    public sealed class DefaultApplicationModule : MonicaModule<DefaultApplicationOptions>
    {
        public override void Describe(ModuleDescriptor module) => module.Require<ModuleApplicationModel, ModuleApplicationModelOption>(options =>
        {
            var values = Typed();
            values["api"].AppName = "Default name";
            options.Deployments = values;
        });
    }

    public sealed class DefaultApplicationOptions : ModuleOptions<DefaultApplicationModule>;

    public sealed class FailingManifest : IApplicationManifest
    {
        public FailingManifest() => throw new InvalidOperationException("Manifest constructor failure");
        public IReadOnlyList<ApplicationDomainDefinition> Domains => [];
        public IReadOnlyList<ApplicationRoleDefinition> Roles => [];
        public IReadOnlyList<ApplicationOperationDefinition> Operations => [];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }
}
