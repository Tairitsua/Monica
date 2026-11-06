using System.Collections;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Annotations;
using Monica.Core.Modularity.Diagnostics.Facades;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

[Collection(BlockingConcurrencyCollection.Name)]
public sealed class ModuleDiagnosticsFacadeTests
{
    [Fact]
    public void GetSnapshot_WhenCompositionIsTerminal_ShouldReturnOneStableRevisionAndReference()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();

        var first = facade.GetSnapshot();
        var second = facade.GetSnapshot();

        first.Status.Should().Be(ResultStatus.Ok);
        first.Data.Should().NotBeNull();
        first.Data!.IsFinal.Should().BeTrue();
        first.Data.Outcome.Should().Be(ModuleCompositionOutcome.Succeeded);
        second.Data.Should().BeSameAs(first.Data);
        second.Data!.Revision.Should().Be(first.Data.Revision);
        first.Data.Summary.PerformanceBudgets.Should().BeEmpty();
    }

    [Fact]
    public void GetSnapshot_WhenConfiguredBudgetIsExceeded_ShouldEmitStructuredWarning()
    {
        using var host = CreateHost(configureBudgets: true);
        var snapshot = host.Services.GetRequiredService<ModuleDiagnosticsFacade>().GetSnapshot().Data!;

        snapshot.Outcome.Should().Be(ModuleCompositionOutcome.Degraded);
        snapshot.Summary.PerformanceBudgets.Should().ContainSingle(evaluation =>
            evaluation.Kind == ModulePerformanceBudgetKind.TotalComposition && evaluation.IsExceeded);
        snapshot.Findings.Should().ContainSingle(finding =>
            finding.Code == ModuleDiagnosticFindingCodes.PERFORMANCE_BUDGET_EXCEEDED
            && finding.Severity == ModuleDiagnosticFindingSeverity.Warning
            && finding.Evidence.Kind == ModuleDiagnosticFindingEvidenceKind.PerformanceBudget);
    }

    [Fact]
    public void GetModuleOptions_WhenNoProjectionPolicyIsConfigured_ShouldExposeAutomaticBoundedConfiguration()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var module = facade.GetSnapshot().Data!.Modules.Single(item =>
            item.TypeName == nameof(DiagnosticsConsumerModule));

        var diagnostics = facade.GetModuleOptions(module.ModuleKey);

        diagnostics.Status.Should().Be(ResultStatus.Ok);
        diagnostics.Data!.IsFinalized.Should().BeTrue();
        diagnostics.Data.ExposureMode.Should().Be(ModuleOptionDiagnosticsExposureMode.Redacted);
        diagnostics.Data.ProfileResolution.Should().Be(ModuleOptionProfileResolution.Default);
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "Mode"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Value
            && entry.Value == "probe");
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "InheritedLabel"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Value
            && entry.Value == "inherited-probe");
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "ApiKey"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Presence
            && entry.IsPresent == true);
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "MaxReadTokens"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Value
            && entry.Value == "4096");
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "AccessTokenExpiration"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Value
            && entry.Value == "00:30:00");
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "ApiKeys"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Count
            && entry.Count == 1);
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "ConnectionStrings"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Count
            && entry.Count == 1);
        diagnostics.Data.Entries.Single(entry => entry.Name == "Credentials")
            .Children.Should().ContainSingle(entry =>
                entry.Name == "Value"
                && entry.Kind == ModuleOptionDiagnosticValueKind.Presence
                && entry.IsPresent == true);
        diagnostics.Data.Entries.Single(entry => entry.Name == "ClientSecretOptions")
            .Children.Should().ContainSingle(entry =>
                entry.Name == "Value"
                && entry.Kind == ModuleOptionDiagnosticValueKind.Presence
                && entry.IsPresent == true);
        diagnostics.Data.Entries.Should().Contain(entry =>
            entry.Name == "ComputedValue"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Unsupported
            && entry.Value == null
            && entry.IsPresent == null
            && entry.Children.IsEmpty);
        var serialized = JsonSerializer.Serialize(diagnostics.Data);
        serialized.Should().NotContain("super-secret");
        serialized.Should().NotContain("credential-with-benign-child-name");
        serialized.Should().NotContain("client-secret-with-benign-child-name");
        serialized.Should().NotContain("connection-string-secret");
        serialized.Should().Contain("diagnostics-tests");
    }

    [Fact]
    public void GetModuleOptions_WhenNestedOptionsAndCollectionsArePresent_ShouldApplyProjectionBounds()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var moduleKey = ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule));

        var diagnostics = facade.GetModuleOptions(moduleKey).Data!;
        var authentication = diagnostics.Entries.Single(entry => entry.Name == "Authentication");
        var endpoints = diagnostics.Entries.Single(entry => entry.Name == "Endpoints");

        authentication.Kind.Should().Be(ModuleOptionDiagnosticValueKind.Object);
        authentication.Children.Should().Contain(entry =>
            entry.Name == "Issuer"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Value
            && entry.Value == "diagnostics-tests"
            && !entry.IsSensitive);
        authentication.Children.Should().Contain(entry =>
            entry.Name == "Secret"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Presence
            && entry.IsSensitive);
        endpoints.Kind.Should().Be(ModuleOptionDiagnosticValueKind.Collection);
        endpoints.Count.Should().Be(25);
        endpoints.Children.Should().HaveCount(20);
        endpoints.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public void GetModuleOptions_WhenCollectionEnumerationFails_ShouldReturnUnavailableTruncatedEntry()
    {
        using var host = CreateHost(configureFaultingCollection: true);
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();

        var diagnostics = facade.GetModuleOptions(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)));

        diagnostics.Status.Should().Be(ResultStatus.Ok);
        diagnostics.Data!.IsTruncated.Should().BeTrue();
        var faultingItems = diagnostics.Data.Entries.Single(entry => entry.Name == "FaultingItems");
        faultingItems.Kind.Should().Be(ModuleOptionDiagnosticValueKind.Collection);
        faultingItems.IsTruncated.Should().BeTrue();
        faultingItems.Children.Should().ContainSingle(entry =>
            entry.Kind == ModuleOptionDiagnosticValueKind.Unavailable && entry.IsTruncated);
    }

    [Fact]
    public void GetModuleOptions_WhenReadRepeatedlyInRedactedMode_ShouldReturnTheCachedProjection()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var moduleKey = ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule));

        var first = facade.GetModuleOptions(moduleKey);
        var second = facade.GetModuleOptions(moduleKey);

        first.Status.Should().Be(ResultStatus.Ok);
        second.Status.Should().Be(ResultStatus.Ok);
        second.Data.Should().BeSameAs(first.Data);
    }

    [Fact]
    public async Task GetModuleOptions_WhenRedactedRequestsRace_ShouldExecuteOneProjection()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var service = host.Services.GetRequiredService<ModuleDiagnosticsService>();
        var moduleKey = ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule));
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectionCount = 0;
        using var callersReady = new CountdownEvent(12);
        using var start = new ManualResetEventSlim();
        using var releaseProjection = new ManualResetEventSlim();
        service.SetOptionProjectionObserver(() =>
        {
            Interlocked.Increment(ref projectionCount);
            releaseProjection.Wait(cancellationToken);
        });

        var requests = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() =>
            {
                callersReady.Signal();
                start.Wait(cancellationToken);
                return facade.GetModuleOptions(moduleKey).Data!;
            }, cancellationToken))
            .ToArray();
        callersReady.Wait(cancellationToken);
        start.Set();
        var projectionStarted = SpinWait.SpinUntil(
            () => Volatile.Read(ref projectionCount) > 0,
            TimeSpan.FromSeconds(5));
        releaseProjection.Set();
        projectionStarted.Should().BeTrue();

        var projections = await Task.WhenAll(requests);

        projectionCount.Should().Be(1);
        projections.Should().OnlyContain(projection => ReferenceEquals(projection, projections[0]));
    }

    [Fact]
    public void GetModuleOptions_WhenSensitiveValueRevealIsEnabled_ShouldRevealBoundedSensitiveValues()
    {
        using var host = CreateHost(
            exposureMode: ModuleOptionDiagnosticsExposureMode.RevealSensitive,
            configureProjectionPolicy: true);
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var moduleKey = ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule));

        var first = facade.GetModuleOptions(moduleKey);
        var second = facade.GetModuleOptions(moduleKey);

        first.Status.Should().Be(ResultStatus.Ok);
        first.Data!.ExposureMode.Should().Be(ModuleOptionDiagnosticsExposureMode.RevealSensitive);
        first.Data.ContainsRevealedSensitiveValues.Should().BeTrue();
        var apiKey = first.Data.Entries.Single(entry => entry.Name == "ApiKey");
        apiKey.Kind.Should().Be(ModuleOptionDiagnosticValueKind.Value);
        apiKey.Value.Should().StartWith("super-secret-");
        apiKey.Value!.Length.Should().Be(256);
        apiKey.IsTruncated.Should().BeTrue();
        apiKey.IsSensitive.Should().BeTrue();
        var apiKeys = first.Data.Entries.Single(entry => entry.Name == "ApiKeys");
        apiKeys.Kind.Should().Be(ModuleOptionDiagnosticValueKind.Collection);
        apiKeys.Children.Should().Contain(entry => entry.Value == "plural-super-secret");
        apiKeys.Children.Should().OnlyContain(entry => entry.IsSensitive);
        var authentication = first.Data.Entries.Single(entry => entry.Name == "Authentication");
        authentication.Kind.Should().Be(ModuleOptionDiagnosticValueKind.Object);
        authentication.Children.Should().Contain(entry =>
            entry.Name == "Issuer"
            && entry.Value == "diagnostics-tests"
            && !entry.IsSensitive);
        authentication.Children.Should().Contain(entry =>
            entry.Name == "Secret"
            && entry.Value == "nested-super-secret"
            && entry.IsSensitive);
        var credentials = first.Data.Entries.Single(entry => entry.Name == "Credentials");
        credentials.Kind.Should().Be(ModuleOptionDiagnosticValueKind.Object);
        credentials.Children.Should().ContainSingle(entry =>
            entry.Value == "credential-with-benign-child-name" && entry.IsSensitive);
        first.Data.Entries.Should().Contain(entry =>
            entry.Name == "PolicySensitive"
            && entry.Value == "policy-sensitive-value"
            && entry.IsSensitive);
        first.Data.Entries.Should().Contain(entry =>
            entry.Name == "AttributeSensitive"
            && entry.Value == "attribute-sensitive-value"
            && entry.IsSensitive);
        second.Data.Should().NotBeSameAs(first.Data);
    }

    [Fact]
    public void GetModuleOptions_WhenPolicyAndAttributeMarkAmbiguousValues_ShouldRedactBothByDefault()
    {
        using var host = CreateHost(configureProjectionPolicy: true);
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();

        var diagnostics = facade.GetModuleOptions(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule))).Data!;

        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == "PolicySensitive"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Presence
            && entry.IsSensitive);
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == "AttributeSensitive"
            && entry.Kind == ModuleOptionDiagnosticValueKind.Presence
            && entry.IsSensitive);
        JsonSerializer.Serialize(diagnostics).Should().NotContain("policy-sensitive-value");
        JsonSerializer.Serialize(diagnostics).Should().NotContain("attribute-sensitive-value");
    }

    [Fact]
    public void Composition_WhenSensitiveValueRevealRunsOutsideDevelopment_ShouldRejectTheHostConfiguration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Production;

        Action compose = () => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.ConfigureModuleSystem(options =>
                options.OptionDiagnosticsExposureMode = ModuleOptionDiagnosticsExposureMode.RevealSensitive);
            monica.AddModuleSystem();
        });

        compose.Should().Throw<InvalidOperationException>()
            .WithMessage("*RevealSensitive*Development*");
    }

    [Fact]
    public void GetModuleOptions_WhenExposureModeBecomesInvalid_ShouldFailClosed()
    {
        using var host = CreateHost();
        host.Services.GetRequiredService<MonicaApplication>()
            .ModuleSystemConfiguration.OptionDiagnosticsExposureMode =
                (ModuleOptionDiagnosticsExposureMode)int.MaxValue;
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();

        var diagnostics = facade.GetModuleOptions(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)));

        diagnostics.Status.Should().Be(ResultStatus.BadRequest);
        diagnostics.Data.Should().BeNull();
        diagnostics.Message.Should().Be("Failed to load the module option diagnostics.");
    }

    [Fact]
    public void GetModuleOptions_WhenSelectingProfiles_ShouldHonorExactAndExplicitFallbackSemantics()
    {
        using var host = CreateHost(configureProfile: true);
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var moduleKey = ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule));

        var named = facade.GetModuleOptions(
            moduleKey,
            ModuleOptionProfileSelector.Named("blue"));
        var namedOrDefault = facade.GetModuleOptions(
            moduleKey,
            ModuleOptionProfileSelector.NamedOrDefault("blue"));
        var fallback = facade.GetModuleOptions(
            moduleKey,
            ModuleOptionProfileSelector.NamedOrDefault("missing"));
        var missing = facade.GetModuleOptions(
            moduleKey,
            ModuleOptionProfileSelector.Named("missing"));

        named.Status.Should().Be(ResultStatus.Ok);
        named.Data!.RequestedProfileName.Should().Be("blue");
        named.Data.ProfileName.Should().Be("blue");
        named.Data.ProfileResolution.Should().Be(ModuleOptionProfileResolution.Named);
        named.Data.Entries.Should().Contain(entry => entry.Name == "Mode" && entry.Value == "profile-blue");
        namedOrDefault.Status.Should().Be(ResultStatus.Ok);
        namedOrDefault.Data!.RequestedProfileName.Should().Be("blue");
        namedOrDefault.Data.ProfileName.Should().Be("blue");
        namedOrDefault.Data.ProfileResolution.Should().Be(ModuleOptionProfileResolution.Named);
        namedOrDefault.Data.Entries.Should().Contain(entry => entry.Name == "Mode" && entry.Value == "profile-blue");
        fallback.Status.Should().Be(ResultStatus.Ok);
        fallback.Data!.RequestedProfileName.Should().Be("missing");
        fallback.Data.ProfileName.Should().BeNull();
        fallback.Data.ProfileResolution.Should().Be(ModuleOptionProfileResolution.DefaultFallback);
        fallback.Data.Entries.Should().Contain(entry => entry.Name == "Mode" && entry.Value == "probe");
        missing.Status.Should().Be(ResultStatus.BadRequest);
        missing.Data.Should().BeNull();
        missing.Message.Should().Be("Failed to load the module option diagnostics.");
    }

    [Fact]
    public void GetModuleOptions_WhenProfileSelectorIsNull_ShouldRemainInsideTheResultBoundary()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();

        var diagnostics = facade.GetModuleOptions(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            null!);

        diagnostics.Status.Should().Be(ResultStatus.BadRequest);
        diagnostics.Data.Should().BeNull();
        diagnostics.Message.Should().Be("Failed to load the module option diagnostics.");
    }

    [Fact]
    public void CreateExport_ShouldUsePortableIdsAndOmitOptionsPathsAndRawFailures()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();

        var export = facade.CreateExport();
        var json = JsonSerializer.Serialize(export.Data);

        export.Status.Should().Be(ResultStatus.Ok);
        export.Data!.Modules.Should().Contain(module =>
            module.ModuleId == ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)).Id);
        export.Data.Edges.Should().Contain(edge =>
            edge.SourceModuleId == ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)).Id
            && edge.TargetModuleId == ModuleKey.FromModuleType(typeof(DiagnosticsProviderModule)).Id);
        var snapshotJson = JsonSerializer.Serialize(facade.GetSnapshot().Data);

        json.Should().NotContain("super-secret");
        snapshotJson.Should().NotContain("super-secret");
        json.Should().NotContain("OptionTypeName");
        snapshotJson.Should().NotContain("OptionTypeName");
        json.Should().NotContain("Location");
        json.Should().NotContain("StackTrace");
    }

    [Fact]
    public void CreateExport_AfterSensitiveOptionsWereMaterialized_ShouldStillOmitEveryOptionValue()
    {
        using var host = CreateHost(
            exposureMode: ModuleOptionDiagnosticsExposureMode.RevealSensitive,
            configureProjectionPolicy: true);
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var moduleKey = ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule));

        var materialized = facade.GetModuleOptions(moduleKey);
        var optionJson = JsonSerializer.Serialize(materialized.Data);
        var exportJson = JsonSerializer.Serialize(facade.CreateExport().Data);
        var snapshotJson = JsonSerializer.Serialize(facade.GetSnapshot().Data);

        materialized.Status.Should().Be(ResultStatus.Ok);
        optionJson.Should().Contain("super-secret");
        exportJson.Should().NotContain("super-secret");
        exportJson.Should().NotContain("credential-with-benign-child-name");
        snapshotJson.Should().NotContain("super-secret");
        snapshotJson.Should().NotContain("credential-with-benign-child-name");
    }

    [Fact]
    public void GetSnapshot_WhenTerminalCollectionsArePublished_ShouldRejectMutationAndKeepCachedIdentity()
    {
        using var host = CreateHost(configureBudgets: true);
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var snapshot = facade.GetSnapshot().Data!;
        IList<ModuleDiagnosticsModule> modules = snapshot.Modules;
        IDictionary<string, string> arguments = snapshot.Findings.Single().Arguments;

        Action replaceModule = () => modules[0] = modules[0];
        Action addArgument = () => arguments.Add("injected", "value");

        replaceModule.Should().Throw<NotSupportedException>();
        addArgument.Should().Throw<NotSupportedException>();
        facade.GetSnapshot().Data.Should().BeSameAs(snapshot);
        snapshot.Findings.Single().Arguments.Should().NotContainKey("injected");
    }

    [Fact]
    public void GetAssemblyInventory_WhenNoPlanRequiresEnumeration_ShouldReportResolvedNotScannedAssemblies()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(ModuleDiagnosticsFacadeTests).Assembly));
            monica.AddModuleSystem();
        });
        using var host = builder.Build();

        var inventory = host.Services.GetRequiredService<ModuleDiagnosticsFacade>()
            .GetAssemblyInventory().Data!;

        inventory.Assemblies.Should().Contain(record =>
            record.Name == typeof(ModuleDiagnosticsFacadeTests).Assembly.GetName().Name
            && record.Outcome == TypeDiscoveryAssemblyOutcome.ResolvedNotScanned);
        inventory.ScannedCount.Should().Be(0);
        inventory.NotScannedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetSnapshot_WhenWebCompositionCallbackFails_ShouldBecomeTerminalWithOneSanitizedFinding()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModuleSystem();
            monica.AddModule<FailingDiagnosticsWebModule, FailingDiagnosticsWebModuleOption>();
        });
        using var app = builder.Build();

        Action configurePipeline = () => app.UseMonica();

        configurePipeline.Should().Throw<InvalidOperationException>()
            .WithMessage("*sensitive-web-failure*");
        var snapshot = app.Services.GetRequiredService<ModuleDiagnosticsFacade>().GetSnapshot().Data!;
        snapshot.IsFinal.Should().BeTrue();
        snapshot.Outcome.Should().Be(ModuleCompositionOutcome.Failed);
        snapshot.Findings.Should().ContainSingle(finding =>
            finding.Code == ModuleDiagnosticFindingCodes.COMPOSITION_FAILED
            && finding.Evidence.Kind == ModuleDiagnosticFindingEvidenceKind.CompositionFailure);
        JsonSerializer.Serialize(snapshot.Findings).Should().NotContain("sensitive-web-failure");
    }

    [Fact]
    public void GetAssemblyInventory_WhenReadRepeatedly_ShouldReturnTheCachedInventory()
    {
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();

        var first = facade.GetAssemblyInventory();
        var second = facade.GetAssemblyInventory();

        first.Status.Should().Be(ResultStatus.Ok);
        second.Data.Should().BeSameAs(first.Data);
        first.Data!.Assemblies.Should().OnlyContain(assembly =>
            Enum.IsDefined(assembly.Outcome));
    }

    [Fact]
    public void GetSnapshot_ShouldExposeDirectEdgesAndLongestDependencyDepth()
    {
        using var host = CreateHost();
        var snapshot = host.Services.GetRequiredService<ModuleDiagnosticsFacade>().GetSnapshot().Data!;
        var consumer = snapshot.Modules.Single(module =>
            module.TypeName == nameof(DiagnosticsConsumerModule));

        snapshot.Topology.Edges.Should().ContainSingle(edge =>
            edge.SourceModule == consumer.ModuleKey
            && edge.TargetModule == ModuleKey.FromModuleType(typeof(DiagnosticsProviderModule)));
        consumer.DependencyDepth.Should().Be(1);
        snapshot.Topology.MaximumDepth.Should().BeGreaterThanOrEqualTo(1);
    }

    private static IHost CreateHost(
        bool configureBudgets = false,
        ModuleOptionDiagnosticsExposureMode exposureMode = ModuleOptionDiagnosticsExposureMode.Redacted,
        bool configureProjectionPolicy = false,
        bool configureProfile = false,
        bool configureFaultingCollection = false)
    {
        var builder = Host.CreateApplicationBuilder();
        if (exposureMode == ModuleOptionDiagnosticsExposureMode.RevealSensitive)
        {
            builder.Environment.EnvironmentName = Environments.Development;
        }

        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            if (configureBudgets || exposureMode != ModuleOptionDiagnosticsExposureMode.Redacted)
            {
                monica.ConfigureModuleSystem(options =>
                {
                    options.OptionDiagnosticsExposureMode = exposureMode;
                    if (configureBudgets)
                    {
                        options.StartupPerformanceBudgets = new()
                        {
                            TotalComposition = TimeSpan.FromTicks(1)
                        };
                    }
                });
            }

            if (configureProjectionPolicy)
            {
                monica.AddModuleSystem(options => options
                    .ConfigureModuleOptionDiagnostics<DiagnosticsConsumerModule, DiagnosticsConsumerModuleOption>(
                        policy => policy
                            .MarkSensitive(static option => option.PolicySensitive)));
            }
            else
            {
                monica.AddModuleSystem();
            }

            var consumer = monica.AddModule<DiagnosticsConsumerModule, DiagnosticsConsumerModuleOption>(options =>
            {
                options.Mode = "probe";
                options.ApiKey = $"super-secret-{new string('x', 300)}";
                options.InheritedLabel = "inherited-probe";
                options.MaxReadTokens = 4096;
                options.AccessTokenExpiration = TimeSpan.FromMinutes(30);
                options.ApiKeys = ["plural-super-secret"];
                options.ConnectionStrings = new Dictionary<string, string>
                {
                    ["primary"] = "connection-string-secret"
                };
                options.Credentials.Value = "credential-with-benign-child-name";
                options.ClientSecretOptions.Value = "client-secret-with-benign-child-name";
                options.AttributeSensitive = "attribute-sensitive-value";
                if (configureProjectionPolicy)
                {
                    options.PolicySensitive = "policy-sensitive-value";
                }
                options.Authentication.Issuer = "diagnostics-tests";
                options.Authentication.Secret = "nested-super-secret";
                options.Endpoints = Enumerable.Range(0, 25)
                    .Select(static index => $"endpoint-{index:D2}")
                    .ToList();
                if (configureFaultingCollection)
                {
                    options.FaultingItems = ArrayList.Adapter(new ThrowingList());
                }
            });
            if (configureProfile)
            {
                consumer.ConfigureProfile("blue", options =>
                {
                    options.Mode = "profile-blue";
                    options.ApiKey = "profile-super-secret";
                });
            }
        });
        return builder.Build();
    }
}

internal sealed class DiagnosticsConsumerModule : MonicaModule<DiagnosticsConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DiagnosticsProviderModule, DiagnosticsProviderModuleOption>();
    }
}

internal abstract class DiagnosticsOptionBase<TModule> : ModuleOptions<TModule>
    where TModule : IModule
{
    public string InheritedLabel { get; set; } = "inherited-default";
}

internal sealed class DiagnosticsConsumerModuleOption : DiagnosticsOptionBase<DiagnosticsConsumerModule>
{
    public string Mode { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    public int MaxReadTokens { get; set; }

    public TimeSpan AccessTokenExpiration { get; set; }

    public List<string> ApiKeys { get; set; } = [];

    public Dictionary<string, string> ConnectionStrings { get; set; } = [];

    public DiagnosticsAuthenticationOption Authentication { get; set; } = new();

    public DiagnosticsCredentialOption Credentials { get; set; } = new();

    public DiagnosticsCredentialOption ClientSecretOptions { get; set; } = new();

    public List<string> Endpoints { get; set; } = [];

    public ArrayList? FaultingItems { get; set; }

    public string PolicySensitive { get; set; } = string.Empty;

    [ModuleOptionDiagnosticsSensitive]
    public string AttributeSensitive { get; set; } = string.Empty;

    public string ComputedValue => throw new InvalidOperationException("Computed getters must not be invoked.");
}

internal sealed class DiagnosticsAuthenticationOption
{
    public string Issuer { get; set; } = string.Empty;

    public string? Secret { get; set; }
}

internal sealed class DiagnosticsCredentialOption
{
    public string Value { get; set; } = string.Empty;
}

internal sealed class ThrowingList : IList
{
    public object? this[int index]
    {
        get => throw new InvalidOperationException("Collection item became unavailable.");
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public int Count => 3;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => false;

    public int IndexOf(object? value) => -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index) => throw new InvalidOperationException("Collection copy failed.");

    public IEnumerator GetEnumerator() =>
        throw new InvalidOperationException("Collection enumeration failed.");
}

internal sealed class DiagnosticsProviderModule : MonicaModule<DiagnosticsProviderModuleOption>;

internal sealed class DiagnosticsProviderModuleOption : ModuleOptions<DiagnosticsProviderModule>;

internal sealed class FailingDiagnosticsWebModule
    : MonicaModule<FailingDiagnosticsWebModuleOption>, IWebModule
{
    public override void ConfigureApplicationBuilder(WebModuleContext<FailingDiagnosticsWebModuleOption> context)
    {
        throw new InvalidOperationException("sensitive-web-failure");
    }
}

internal sealed class FailingDiagnosticsWebModuleOption : ModuleOptions<FailingDiagnosticsWebModule>;
