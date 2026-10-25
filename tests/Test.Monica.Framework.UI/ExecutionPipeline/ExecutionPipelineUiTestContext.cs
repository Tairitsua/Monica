using Bunit;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.Execution;
using Monica.Core.Execution.Abstractions;
using Monica.Core.Execution.Facades;
using Monica.Core.Execution.Models;
using Monica.Core.Modularity.Models;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIExecutionPipeline.State;
using Monica.Modules;
using Monica.Testing.Localization;
using MudBlazor;
using MudBlazor.Services;

namespace Test.Monica.Framework.UI.ExecutionPipeline;

internal sealed class ExecutionPipelineUiTestContext : BunitContext
{
    internal ExecutionPipelineUiTestContext(ExecutionPipelineCatalogSnapshot snapshot)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        Services.AddSingleton<IStringLocalizer<ExecutionPipelineResource>, EchoStringLocalizer<ExecutionPipelineResource>>();
        Services.AddSingleton<IExecutionPipelineCatalog>(new StubExecutionPipelineCatalog(snapshot));
        Services.AddSingleton(serviceProvider => new ExecutionPipelineCatalogFacade(
            serviceProvider.GetRequiredService<IExecutionPipelineCatalog>(),
            NullLogger<ExecutionPipelineCatalogFacade>.Instance));
        Services.AddScoped<ExecutionPipelinePageState>();
        _ = Render<MudPopoverProvider>();
        _ = Render<MudSnackbarProvider>();
    }

    internal static ExecutionPipelineCatalogSnapshot CreateSnapshot(
        IReadOnlyList<ExecutionPipelinePlanSnapshot>? plans = null,
        IReadOnlyList<ExecutionBehaviorRegistrationSnapshot>? registrations = null)
    {
        return new ExecutionPipelineCatalogSnapshot(
            DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
            registrations?.ToImmutableArray() ?? [],
            plans?.ToImmutableArray() ?? []);
    }

    internal static ExecutionPipelinePlanSnapshot CreatePlan(
        string name,
        ExecutionPipelinePlanStatus status = ExecutionPipelinePlanStatus.Ready,
        bool isBusinessOperation = true,
        IReadOnlyList<ExecutionPipelineAppliedBehaviorSnapshot>? behaviors = null,
        ExecutionPipelinePlanErrorSnapshot? error = null)
    {
        var operationKey = $"operation:{name}";
        var planKey = $"plan:{name}";
        return new ExecutionPipelinePlanSnapshot(
            new ExecutionPlanId($"plan_{name}"),
            status,
            DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
            status == ExecutionPipelinePlanStatus.Building
                ? null
                : DateTimeOffset.Parse("2026-07-29T00:00:01Z"),
            new ExecutionDescriptorSnapshot(
                new ExecutionOperationId($"op_{name}"),
                new ExecutionPoint("Mediator.Handler"),
                Type("Example.Component"),
                Type("Example.Contract"),
                new ExecutionMethodSnapshot(
                    name,
                    $"Example.Component.{name}() → Example.Result",
                    new ExecutionMethodDiagnosticsSnapshot($"Example.Component, Example.Assembly.{name}()")),
                Type("Example.Input"),
                Type("Example.Result"),
                isBusinessOperation,
                ExecutionTransactionMode.Automatic,
                new ExecutionOperationDiagnosticsSnapshot(operationKey)),
            behaviors?.ToImmutableArray() ?? [],
            error,
            new ExecutionPlanDiagnosticsSnapshot(planKey));
    }

    internal static ExecutionPipelineAppliedBehaviorSnapshot CreateBehavior(
        int position,
        string name,
        int order)
    {
        return new ExecutionPipelineAppliedBehaviorSnapshot(
            position,
            Type($"Example.{name}<,>"),
            Type($"Example.{name}<Example.Input, Example.Result>"),
            order,
            ServiceLifetime.Scoped,
            ModuleKey.FromModuleType(typeof(ModuleMediator)));
    }

    internal static ExecutionBehaviorRegistrationSnapshot CreateRegistration(
        string name,
        int order)
    {
        return new ExecutionBehaviorRegistrationSnapshot(
            Type($"Example.{name}<,>"),
            order,
            ServiceLifetime.Scoped,
            ModuleKey.FromModuleType(typeof(ModuleMediator)),
            IsOpenGeneric: true,
            HasDescriptorFilter: true);
    }

    private static ExecutionTypeSnapshot Type(string name) => new(
        name,
        name,
        "Example.Assembly",
        new ExecutionTypeDiagnosticsSnapshot(
            $"{name}, Example.Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));

    private sealed class StubExecutionPipelineCatalog(ExecutionPipelineCatalogSnapshot snapshot)
        : IExecutionPipelineCatalog
    {
        public ExecutionPipelineCatalogSnapshot GetSnapshot() => snapshot;

        public ExecutionPipelinePlanSnapshot InspectPlan(ExecutionDescriptor descriptor) =>
            throw new NotSupportedException();
    }
}
