using System.Collections.Immutable;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.UI.UIModuleSystem.State;

namespace Test.Monica.UI.UIModuleSystem;

internal static class ModuleSystemWorkbenchTestData
{
    internal static readonly ModuleKey AlphaKey = ModuleKey.FromModuleType(typeof(AlphaModule));
    internal static readonly ModuleKey BetaKey = ModuleKey.FromModuleType(typeof(BetaModule));

    internal static ModuleDiagnosticsSnapshot Snapshot(
        long revision = 1,
        bool isFinal = true,
        IReadOnlyList<ModuleDiagnosticFinding>? findings = null,
        double? applicationStartupDurationMs = 36.125)
    {
        var alpha = Module(AlphaKey, nameof(AlphaModule), "Test.Core", 0, callbackMs: 6, dependencyCount: 0);
        var beta = Module(
            BetaKey,
            nameof(BetaModule),
            "Test.Web",
            1,
            callbackMs: 2,
            startupMs: 12,
            dependencyCount: 1,
            isWeb: true);
        return new ModuleDiagnosticsSnapshot
        {
            CompositionId = "test-composition",
            Revision = revision,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            IsFinal = isFinal,
            Outcome = isFinal ? ModuleCompositionOutcome.Succeeded : null,
            Summary = new ModuleDiagnosticsSummary
            {
                ModuleCount = 2,
                ActiveModuleCount = 2,
                TotalCompositionDurationMs = 24,
                ApplicationStartupDurationMs = applicationStartupDurationMs,
                ServiceRegistrationDurationMs = 18,
                TypeDiscoveryDurationMs = 7
            },
            Modules = [alpha, beta],
            Topology = new ModuleDiagnosticsTopology
            {
                Edges =
                [
                    new ModuleDiagnosticsDependencyEdge
                    {
                        SourceModule = BetaKey,
                        TargetModule = AlphaKey
                    }
                ],
                TopologicalOrder = [AlphaKey, BetaKey],
                MaximumDepth = 1
            },
            TraceSpans =
            [
                new ModuleDiagnosticsTraceSpan
                {
                    SpanId = "span-alpha",
                    Kind = ModuleDiagnosticsTraceSpanKind.ModuleCallback,
                    ModuleKey = AlphaKey,
                    StartedOffsetMs = 1,
                    EndedOffsetMs = 7
                }
            ],
            BlockingChain =
            [
                new ModuleBlockingChainSegment
                {
                    SegmentId = "blocking-beta",
                    BarrierSpanId = "barrier-beta",
                    WorkItemId = "work-beta",
                    WorkSpanId = "work-span-beta",
                    ModuleKey = BetaKey,
                    Barrier = ModuleStartupWorkBarrier.BeforePostConfigureServices,
                    BlockingDurationMs = 9,
                    IsBarrierReleaser = true
                }
            ],
            Findings = findings?.ToImmutableArray() ?? []
        };
    }

    internal static ModuleDiagnosticsCalls Calls(
        Func<Result<ModuleDiagnosticsSnapshot>>? snapshot = null,
        Func<Result<TypeDiscoveryAssemblyInventory>>? inventory = null,
        Func<ModuleKey, Result<ModuleOptionDiagnostics>>? options = null,
        Func<Result<ModuleDiagnosticsExport>>? export = null) => new(
        snapshot ?? (() => Result.Success(Snapshot())),
        inventory ?? (() => Result.Success(Inventory())),
        options ?? (key => Result.Success(Options(key))),
        export ?? (() => Result.Success(Export())));

    internal static TypeDiscoveryAssemblyInventory Inventory() => new()
    {
        CompositionId = "test-composition",
        CapturedAtUtc = new DateTimeOffset(2026, 8, 6, 1, 2, 3, TimeSpan.Zero),
        UsesDefaultProjectAssemblies = true,
        IncludePatterns = ["Test.*"],
        ExcludePatterns = ["Test.Legacy.*"],
        Assemblies =
        [
            new TypeDiscoveryAssemblyRecord
            {
                Name = "Scanned.Assembly",
                Version = "1.2.3",
                Location = @"D:\app\Scanned.Assembly.dll",
                IsEntryAssembly = true,
                Outcome = TypeDiscoveryAssemblyOutcome.Scanned
            },
            new TypeDiscoveryAssemblyRecord
            {
                Name = "Failed.Assembly",
                Outcome = TypeDiscoveryAssemblyOutcome.ResolutionFailed,
                Failure = "unavailable"
            }
        ],
        ScannedCount = 1,
        ResolutionFailedCount = 1
    };

    internal static ModuleOptionDiagnostics Options(
        ModuleKey key,
        ModuleOptionDiagnosticsExposureMode exposureMode = ModuleOptionDiagnosticsExposureMode.Redacted) => new()
        {
            ModuleKey = key,
            OptionTypeName = "TestOptions",
            ExposureMode = exposureMode,
            IsFinalized = true,
            SensitiveEntryCount = 1,
            ContainsRevealedSensitiveValues = exposureMode == ModuleOptionDiagnosticsExposureMode.RevealSensitive,
            Entries =
            [
                new ModuleOptionDiagnosticEntry
                {
                    Name = "Transport",
                    Path = "Transport",
                    TypeName = "TestTransportOptions",
                    Kind = ModuleOptionDiagnosticValueKind.Object,
                    Children =
                    [
                        new ModuleOptionDiagnosticEntry
                        {
                            Name = "Endpoint",
                            Path = "Transport.Endpoint",
                            TypeName = "Uri",
                            Kind = ModuleOptionDiagnosticValueKind.Value,
                            Value = "https://service.test"
                        },
                        SensitiveEntry(exposureMode),
                        new ModuleOptionDiagnosticEntry
                        {
                            Name = "RetryDelays",
                            Path = "Transport.RetryDelays",
                            TypeName = "TimeSpan[]",
                            Kind = ModuleOptionDiagnosticValueKind.Collection,
                            Count = 2,
                            Children =
                            [
                                new ModuleOptionDiagnosticEntry
                                {
                                    Name = "[0]",
                                    Path = "Transport.RetryDelays[0]",
                                    TypeName = "TimeSpan",
                                    Kind = ModuleOptionDiagnosticValueKind.Value,
                                    Value = "00:00:01"
                                },
                                new ModuleOptionDiagnosticEntry
                                {
                                    Name = "[1]",
                                    Path = "Transport.RetryDelays[1]",
                                    TypeName = "TimeSpan",
                                    Kind = ModuleOptionDiagnosticValueKind.Value,
                                    Value = "00:00:03"
                                }
                            ]
                        }
                    ]
                },
                new ModuleOptionDiagnosticEntry
                {
                    Name = "EndpointCount",
                    Path = "EndpointCount",
                    TypeName = "Int32",
                    Kind = ModuleOptionDiagnosticValueKind.Count,
                    Count = 2
                },
                new ModuleOptionDiagnosticEntry
                {
                    Name = "RuntimeClient",
                    Path = "RuntimeClient",
                    TypeName = "TestRuntimeClient",
                    Kind = ModuleOptionDiagnosticValueKind.Unsupported
                },
                new ModuleOptionDiagnosticEntry
                {
                    Name = "FaultingGetter",
                    Path = "FaultingGetter",
                    TypeName = "String",
                    Kind = ModuleOptionDiagnosticValueKind.Unavailable
                }
            ]
        };

    private static ModuleOptionDiagnosticEntry SensitiveEntry(ModuleOptionDiagnosticsExposureMode exposureMode) =>
        exposureMode == ModuleOptionDiagnosticsExposureMode.RevealSensitive
            ? new ModuleOptionDiagnosticEntry
            {
                Name = "ApiToken",
                Path = "Transport.ApiToken",
                TypeName = "String",
                Kind = ModuleOptionDiagnosticValueKind.Value,
                Value = "debug-secret-value",
                IsSensitive = true
            }
            : new ModuleOptionDiagnosticEntry
            {
                Name = "ApiToken",
                Path = "Transport.ApiToken",
                TypeName = "String",
                Kind = ModuleOptionDiagnosticValueKind.Presence,
                IsPresent = true,
                IsSensitive = true
            };

    internal static ModuleDiagnosticsExport Export(
        int schemaVersion = ModuleDiagnosticsSnapshot.CURRENT_SCHEMA_VERSION,
        double? applicationStartupDurationMs = 30) => new()
    {
        SchemaVersion = schemaVersion,
        CompositionId = "baseline-composition",
        Revision = 1,
        IsFinal = true,
        Summary = new ModuleDiagnosticsSummary
        {
            TotalCompositionDurationMs = 20,
            ApplicationStartupDurationMs = applicationStartupDurationMs,
            TypeDiscoveryDurationMs = 5
        },
        TypeDiscovery = new ModuleDiagnosticsExportTypeDiscovery(),
        Modules =
        [
            new ModuleDiagnosticsExportModule
            {
                ModuleId = AlphaKey.Id,
                TypeName = nameof(AlphaModule),
                AssemblyName = "Test.Core",
                IsActive = true
            }
        ],
        Edges = [],
        TraceSpans = [],
        Findings = []
    };

    internal static ModuleDiagnosticsModule Module(
        ModuleKey key,
        string typeName,
        string assembly,
        int order,
        double callbackMs,
        double startupMs = 0,
        int dependencyCount = 0,
        bool isWeb = false) => new()
    {
        ModuleKey = key,
        TypeName = typeName,
        FullTypeName = key.Value,
        AssemblyName = assembly,
        RegistrationOrder = order,
        Phase = ModulePhase.PostConfigureServices,
        IsActive = true,
        IsWebModule = isWeb,
        DirectDependencyCount = dependencyCount,
        SerialCallbackDurationMs = callbackMs,
        StartupWorkDurationMs = startupMs
    };

    private sealed class AlphaModule;
    private sealed class BetaModule;
}
