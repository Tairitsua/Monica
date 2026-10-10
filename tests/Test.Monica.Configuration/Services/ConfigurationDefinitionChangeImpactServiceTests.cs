using AwesomeAssertions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Core.Results;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationDefinitionChangeImpactServiceTests
{
    [Fact]
    public async Task GetImpactAsync_WhenPublishersOverlap_ShouldReturnDeterministicParameterStrategies()
    {
        var definitions = new[]
        {
            CreateDefinition(
                "A.Definition",
                "Alpha options",
                ConfigurationReloadBehavior.RequiresRestart),
            CreateDefinition("Z.Definition", "Zulu options"),
            CreateDefinition("Only.Neutral", "Neutral options"),
            CreateDefinition("No.State", "Unclaimed options")
        };
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.GetDefinitionPublisherStatesAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["A.Definition"] =
                [
                    CreateState(
                        "service.shared",
                        ConfigurationReloadBehaviorObservationKind.Inferred,
                        ConfigurationReloadBehavior.OnlineReloadable),
                    CreateState(
                        "Service.Neutral",
                        ConfigurationReloadBehaviorObservationKind.NotConsumed,
                        ConfigurationReloadBehavior.Unknown)
                ],
                ["Z.Definition"] =
                [
                    CreateState(
                        "Service.Shared",
                        ConfigurationReloadBehaviorObservationKind.Unresolved,
                        ConfigurationReloadBehavior.Unknown),
                    CreateState(
                        "Service.Second",
                        ConfigurationReloadBehaviorObservationKind.Declared,
                        ConfigurationReloadBehavior.RequiresRestart)
                ],
                ["Only.Neutral"] =
                [
                    CreateState(
                        "Service.Neutral",
                        ConfigurationReloadBehaviorObservationKind.NotConsumed,
                        ConfigurationReloadBehavior.Unknown)
                ],
                ["No.State"] = []
            });
        var service = CreateService(metadataStore, definitions);

        var impact = await service.GetImpactAsync(
            [
                Target(" Z.Definition "),
                Target("A.Definition"),
                Target("a.definition"),
                Target("No.State"),
                Target("Only.Neutral")
            ],
            TestContext.Current.CancellationToken);

        impact.AffectedPublishers.Select(static publisher => publisher.PublisherKey)
            .Should().Equal("Service.Second", "Service.Shared");

        var shared = impact.AffectedPublishers.Single(static publisher =>
            publisher.PublisherKey == "Service.Shared");
        shared.Parameters.Select(static parameter => parameter.DefinitionKey)
            .Should().Equal("A.Definition", "Z.Definition");
        var nodeOverride = shared.Parameters[0];
        nodeOverride.DefinitionDisplayName.Should().Be("Alpha options");
        nodeOverride.ParameterDisplayName.Should().Be("Configured value");
        nodeOverride.ObservationKind.Should().Be(ConfigurationReloadBehaviorObservationKind.Declared);
        nodeOverride.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.RequiresRestart);
        nodeOverride.RequiresRestart.Should().BeTrue();

        var unresolved = shared.Parameters[1];
        unresolved.ObservationKind.Should().Be(ConfigurationReloadBehaviorObservationKind.Unresolved);
        unresolved.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.Unknown);
        unresolved.RequiresRestart.Should().BeTrue();
        shared.RequiresRestart.Should().BeTrue();

        impact.ParametersWithoutKnownConsumers
            .Select(static parameter => parameter.DefinitionKey)
            .Should().Equal("No.State", "Only.Neutral");
        impact.ParametersWithoutKnownConsumers[0].LogicalPath.Should().Be(LogicalPath.FromProperties("Value"));
        impact.RequiresRestart.Should().BeTrue();
        await metadataStore.Received(1).GetDefinitionPublisherStatesAsync(
            Arg.Is<IReadOnlyCollection<string>>(keys =>
                keys.SequenceEqual(new[] { "A.Definition", "No.State", "Only.Neutral", "Z.Definition" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetImpactAsync_WhenAllParametersAreOnline_ShouldNotRequireRestart()
    {
        var definition = CreateDefinition("Online.Definition", "Online options");
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.GetDefinitionPublisherStatesAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [definition.DefinitionKey] =
                [
                    CreateState(
                        "Service.Online",
                        ConfigurationReloadBehaviorObservationKind.Inferred,
                        ConfigurationReloadBehavior.OnlineReloadable)
                ]
            });
        var service = CreateService(metadataStore, [definition]);

        var impact = await service.GetImpactAsync(
            [Target(definition.DefinitionKey)],
            TestContext.Current.CancellationToken);

        var publisher = impact.AffectedPublishers.Should().ContainSingle().Which;
        var parameter = publisher.Parameters.Should().ContainSingle().Which;
        parameter.ObservationKind.Should().Be(ConfigurationReloadBehaviorObservationKind.Inferred);
        parameter.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.OnlineReloadable);
        parameter.RequiresRestart.Should().BeFalse();
        publisher.RequiresRestart.Should().BeFalse();
        impact.RequiresRestart.Should().BeFalse();
    }

    [Theory]
    [InlineData(ConfigurationReloadBehavior.OnlineReloadable, false)]
    [InlineData(ConfigurationReloadBehavior.RequiresRestart, true)]
    [InlineData(ConfigurationReloadBehavior.StaticAfterStartup, true)]
    public async Task GetImpactAsync_WhenNodeDeclaresBehavior_ShouldOverridePublisherObservation(
        ConfigurationReloadBehavior nodeBehavior,
        bool requiresRestart)
    {
        var definition = CreateDefinition(
            "Override.Definition",
            "Override options",
            nodeBehavior,
            nodeBehavior == ConfigurationReloadBehavior.OnlineReloadable
                ? ConfigurationReloadBehavior.RequiresRestart
                : ConfigurationReloadBehavior.OnlineReloadable);
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.GetDefinitionPublisherStatesAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [definition.DefinitionKey] =
                [
                    CreateState(
                        "Service.Consumer",
                        ConfigurationReloadBehaviorObservationKind.Unresolved,
                        ConfigurationReloadBehavior.Unknown)
                ]
            });
        var service = CreateService(metadataStore, [definition]);

        var impact = await service.GetImpactAsync(
            [Target(definition.DefinitionKey)],
            TestContext.Current.CancellationToken);

        var parameter = impact.AffectedPublishers.Should().ContainSingle().Which
            .Parameters.Should().ContainSingle().Which;
        parameter.ObservationKind.Should().Be(ConfigurationReloadBehaviorObservationKind.Declared);
        parameter.ReloadBehavior.Should().Be(nodeBehavior);
        parameter.RequiresRestart.Should().Be(requiresRestart);
    }

    [Theory]
    [InlineData(NestedContainerKind.Object)]
    [InlineData(NestedContainerKind.List)]
    [InlineData(NestedContainerKind.Dictionary)]
    public async Task GetImpactAsync_WhenContainerDeclaresBehavior_ShouldInheritNearestOverride(
        NestedContainerKind containerKind)
    {
        var (definition, targetPath) = CreateNestedDefinition(containerKind);
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.GetDefinitionPublisherStatesAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>
            {
                [definition.DefinitionKey] =
                [
                    CreateState(
                        "Service.Online",
                        ConfigurationReloadBehaviorObservationKind.Inferred,
                        ConfigurationReloadBehavior.OnlineReloadable)
                ]
            });
        var service = CreateService(metadataStore, [definition]);

        var impact = await service.GetImpactAsync(
            [
                new ConfigurationParameterChangeTarget
                {
                    DefinitionKey = definition.DefinitionKey,
                    LogicalPath = targetPath
                }
            ],
            TestContext.Current.CancellationToken);

        var parameter = impact.AffectedPublishers.Should().ContainSingle().Which
            .Parameters.Should().ContainSingle().Which;
        var targetNode = ConfigurationSchemaNavigator.ResolveNode(definition.Root, targetPath);
        targetNode.Should().NotBeNull();
        targetNode!.ResolveEffectiveReloadBehavior(definition)
            .Should().Be(ConfigurationReloadBehavior.RequiresRestart);
        parameter.LogicalPath.Should().Be(targetPath);
        parameter.ObservationKind.Should().Be(ConfigurationReloadBehaviorObservationKind.Declared);
        parameter.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.RequiresRestart);
        parameter.RequiresRestart.Should().BeTrue();
    }

    [Fact]
    public async Task GetImpactAsync_WhenTargetsDifferOnlyBySchemaCasing_ShouldReturnOneCanonicalParameter()
    {
        var definition = CreateDefinition("Canonical.Definition", "Canonical options");
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.GetDefinitionPublisherStatesAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>
            {
                [definition.DefinitionKey] =
                [
                    CreateState(
                        "Service.Online",
                        ConfigurationReloadBehaviorObservationKind.Inferred,
                        ConfigurationReloadBehavior.OnlineReloadable)
                ]
            });
        var service = CreateService(metadataStore, [definition]);

        var impact = await service.GetImpactAsync(
            [
                Target("canonical.definition"),
                new ConfigurationParameterChangeTarget
                {
                    DefinitionKey = "CANONICAL.DEFINITION",
                    LogicalPath = LogicalPath.FromProperties("VALUE")
                }
            ],
            TestContext.Current.CancellationToken);

        var parameter = impact.AffectedPublishers.Should().ContainSingle().Which
            .Parameters.Should().ContainSingle().Which;
        parameter.DefinitionKey.Should().Be(definition.DefinitionKey);
        parameter.LogicalPath.Should().Be(LogicalPath.FromProperties("Value"));
    }

    [Fact]
    public async Task GetImpactAsync_WhenTargetsAreEmpty_ShouldRejectRequestBeforeReadingDefinitionsOrStore()
    {
        var registry = Substitute.For<IConfigurationDefinitionRegistry>();
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        var service = new ConfigurationDefinitionChangeImpactService(
            metadataStore,
            new ConfigurationDefinitionResolver(registry));

        var act = () => service.GetImpactAsync([], TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one configuration parameter target*");
        registry.DidNotReceive().GetAll();
        await metadataStore.DidNotReceive().GetDefinitionPublisherStatesAsync(
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetImpactAsync_WhenTargetPathIsMissing_ShouldFailBeforeReadingPublisherState()
    {
        var definition = CreateDefinition("Test.Definition", "Test options");
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        var service = CreateService(metadataStore, [definition]);

        var act = () => service.GetImpactAsync(
            [
                new ConfigurationParameterChangeTarget
                {
                    DefinitionKey = definition.DefinitionKey,
                    LogicalPath = LogicalPath.FromProperties("Missing")
                }
            ],
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Logical path*Missing*does not exist*");
        await metadataStore.DidNotReceive().GetDefinitionPublisherStatesAsync(
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetImpactAsync_WhenStoreOmitsRequestedDefinition_ShouldFailClosed()
    {
        var definition = CreateDefinition("Test.Omitted", "Omitted options");
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.GetDefinitionPublisherStatesAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>());
        var service = CreateService(metadataStore, [definition]);

        var act = () => service.GetImpactAsync(
            [Target(definition.DefinitionKey)],
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*omitted publisher state*Test.Omitted*");
    }

    [Fact]
    public async Task GetDefinitionChangeImpactAsync_WhenStoreFails_ShouldReturnRecursiveDiagnostic()
    {
        var impactService = Substitute.For<IConfigurationDefinitionChangeImpactService>();
        impactService.GetImpactAsync(
                Arg.Any<IReadOnlyCollection<ConfigurationParameterChangeTarget>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ConfigurationDefinitionChangeImpact>(new InvalidOperationException(
                "outer impact failure",
                new InvalidDataException("inner metadata failure"))));
        var facade = CreateFacade(impactService);

        var result = await facade.GetDefinitionChangeImpactAsync([Target("Test.Definition")]);

        result.IsFailed(out var error, out _).Should().BeTrue();
        error!.Message.Should().Contain("outer impact failure");
        error.Message.Should().Contain("inner metadata failure");
    }

    private static ConfigurationDefinitionChangeImpactService CreateService(
        IConfigurationMetadataStore metadataStore,
        IReadOnlyList<ConfigurationDefinition> definitions)
    {
        var registry = Substitute.For<IConfigurationDefinitionRegistry>();
        registry.GetAll().Returns(definitions);
        return new ConfigurationDefinitionChangeImpactService(
            metadataStore,
            new ConfigurationDefinitionResolver(registry));
    }

    private static ConfigurationParameterChangeTarget Target(string definitionKey)
    {
        return new ConfigurationParameterChangeTarget
        {
            DefinitionKey = definitionKey,
            LogicalPath = LogicalPath.FromProperties("Value")
        };
    }

    private static ConfigurationDefinitionPublisherState CreateState(
        string publisherKey,
        ConfigurationReloadBehaviorObservationKind observationKind,
        ConfigurationReloadBehavior reloadBehavior)
    {
        return new ConfigurationDefinitionPublisherState
        {
            PublisherKey = publisherKey,
            ObservationKind = observationKind,
            ReloadBehavior = reloadBehavior
        };
    }

    private static ConfigurationDefinition CreateDefinition(
        string definitionKey,
        string displayName,
        ConfigurationReloadBehavior? nodeReloadBehavior = null,
        ConfigurationReloadBehavior? rootReloadBehavior = null)
    {
        return new ConfigurationDefinition
        {
            DefinitionKey = definitionKey,
            SectionPath = definitionKey,
            DisplayName = displayName,
            ClrTypeName = typeof(object).AssemblyQualifiedName!,
            FromProject = "Test.Monica.Configuration",
            SchemaHash = $"sha256:{definitionKey}",
            Root = new ConfigurationNodeDefinition
            {
                NodeKey = "root",
                Name = definitionKey,
                RelativePath = LogicalPath.Root,
                ClrTypeName = typeof(object).AssemblyQualifiedName!,
                NodeKind = ConfigurationNodeKind.Object,
                ReloadBehavior = rootReloadBehavior,
                Children =
                [
                    new ConfigurationNodeDefinition
                    {
                        NodeKey = "value",
                        Name = "Value",
                        DisplayName = "Configured value",
                        RelativePath = LogicalPath.FromProperties("Value"),
                        ClrTypeName = typeof(string).AssemblyQualifiedName!,
                        NodeKind = ConfigurationNodeKind.Scalar,
                        ValueKind = ConfigurationValueKind.String,
                        ReloadBehavior = nodeReloadBehavior
                    }
                ]
            }
        };
    }

    private static (ConfigurationDefinition Definition, LogicalPath TargetPath) CreateNestedDefinition(
        NestedContainerKind containerKind)
    {
        var containerName = containerKind switch
        {
            NestedContainerKind.Object => "Container",
            NestedContainerKind.List => "Items",
            NestedContainerKind.Dictionary => "Entries",
            _ => throw new ArgumentOutOfRangeException(nameof(containerKind), containerKind, null)
        };
        var containerPath = LogicalPath.FromProperties(containerName);
        var targetPath = containerKind switch
        {
            NestedContainerKind.Object => containerPath.Append(new PropertySegment("Value")),
            NestedContainerKind.List => containerPath
                .Append(new ListIndexSegment(0))
                .Append(new PropertySegment("Value")),
            NestedContainerKind.Dictionary => containerPath
                .Append(new DictionaryKeySegment("primary"))
                .Append(new PropertySegment("Value")),
            _ => throw new ArgumentOutOfRangeException(nameof(containerKind), containerKind, null)
        };
        var templatePath = containerKind switch
        {
            NestedContainerKind.Object => containerPath,
            NestedContainerKind.List => containerPath.Append(new ListItemKeySegment("*")),
            NestedContainerKind.Dictionary => containerPath.Append(new DictionaryKeySegment("*")),
            _ => throw new ArgumentOutOfRangeException(nameof(containerKind), containerKind, null)
        };
        var valueNode = CreateScalarNode("Value", templatePath.Append(new PropertySegment("Value")));
        var templateNode = new ConfigurationNodeDefinition
        {
            NodeKey = $"{containerName}.template",
            Name = containerKind == NestedContainerKind.Object ? containerName : "Value",
            RelativePath = templatePath,
            ClrTypeName = typeof(object).AssemblyQualifiedName!,
            NodeKind = ConfigurationNodeKind.Object,
            Children = [valueNode]
        };
        var containerNode = new ConfigurationNodeDefinition
        {
            NodeKey = containerName,
            Name = containerName,
            RelativePath = containerPath,
            ClrTypeName = typeof(object).AssemblyQualifiedName!,
            NodeKind = containerKind switch
            {
                NestedContainerKind.Object => ConfigurationNodeKind.Object,
                NestedContainerKind.List => ConfigurationNodeKind.List,
                NestedContainerKind.Dictionary => ConfigurationNodeKind.Dictionary,
                _ => throw new ArgumentOutOfRangeException(nameof(containerKind), containerKind, null)
            },
            ReloadBehavior = ConfigurationReloadBehavior.RequiresRestart,
            Children = containerKind == NestedContainerKind.Object ? templateNode.Children : [],
            ListTemplate = containerKind == NestedContainerKind.List
                ? new ConfigurationListTemplate { ItemTemplate = templateNode }
                : null,
            DictionaryTemplate = containerKind == NestedContainerKind.Dictionary
                ? new ConfigurationDictionaryTemplate
                {
                    KeyClrTypeName = typeof(string).AssemblyQualifiedName!,
                    KeyKind = ConfigurationValueKind.String,
                    ValueTemplate = templateNode
                }
                : null
        };
        var definition = new ConfigurationDefinition
        {
            DefinitionKey = $"Nested.{containerKind}",
            SectionPath = $"Nested:{containerKind}",
            DisplayName = $"Nested {containerKind}",
            ClrTypeName = typeof(object).AssemblyQualifiedName!,
            FromProject = "Test.Monica.Configuration",
            SchemaHash = $"sha256:nested-{containerKind}",
            Root = new ConfigurationNodeDefinition
            {
                NodeKey = "root",
                Name = "Root",
                RelativePath = LogicalPath.Root,
                ClrTypeName = typeof(object).AssemblyQualifiedName!,
                NodeKind = ConfigurationNodeKind.Object,
                Children = [containerNode]
            }
        };

        return (definition, targetPath);
    }

    private static ConfigurationNodeDefinition CreateScalarNode(string name, LogicalPath relativePath)
    {
        return new ConfigurationNodeDefinition
        {
            NodeKey = relativePath.ToCanonicalString(),
            Name = name,
            DisplayName = "Configured value",
            RelativePath = relativePath,
            ClrTypeName = typeof(string).AssemblyQualifiedName!,
            NodeKind = ConfigurationNodeKind.Scalar,
            ValueKind = ConfigurationValueKind.String
        };
    }

    public enum NestedContainerKind
    {
        Object,
        List,
        Dictionary
    }

    private static ConfigurationFacade CreateFacade(
        IConfigurationDefinitionChangeImpactService definitionChangeImpactService)
    {
        return new ConfigurationFacade(
            definitionResolver: null!,
            definitionRegistry: null!,
            definitionMaintenanceStore: null!,
            definitionChangeImpactService,
            mutationGroupApplyService: null!,
            historyService: null!,
            mutationGroupService: null!,
            rollbackService: null!,
            unifiedVersionService: null!,
            effectiveValueStore: null!,
            historyStore: null!,
            metadataStore: null!,
            changeNotifiers: [],
            storeStateTracker: null!,
            effectiveStateReader: null!,
            sourceInspector: null!,
            sourceWriter: null!,
            runtimeContext: null!,
            runtimeValidationService: null!,
            optionsValidationDiagnostics: Substitute.For<IConfigurationOptionsValidationDiagnostics>(),
            candidateValidationService: null!,
            runtimeReloadService: null!,
            reloadBroadcastService: null!);
    }
}
