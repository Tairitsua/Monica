using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.Execution;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Providers.AspNetCore;
using Monica.Framework.ChainTracing.Providers.Execution;
using Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;
using Monica.Framework.ChainTracing.Providers.Actor;
using Monica.Framework.ChainTracing.Services;
using Monica.Framework.ChainTracing.Services.Support;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleChainTracingBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the ChainTracing module.
        /// </summary>
        public ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> AddChainTracing(Action<ModuleChainTracingOption>? action = null)
        {
            return builder.AddModule<ModuleChainTracing, ModuleChainTracingOption>(action);
        }
    }
}

/// <summary>
/// Chain tracing module.
/// </summary>
public class ModuleChainTracing : MonicaModule<ModuleChainTracingOption>, IWebModule
{
    public override void ConfigureServices(ModuleContext<ModuleChainTracingOption> context)
    {
        var services = context.Services;
        services.AddSingleton<IChainTracing, AsyncLocalChainTracingService>();
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
    }
}

/// <summary>
/// Registration extensions for opt-in chain tracing integrations.
/// </summary>
public static class ModuleChainTracingRegistrationExtensions
{
    /// <summary>
    /// Traces all business operations executed through Monica's execution pipeline, including Task-based
    /// event handlers and jobs. An execution creates and completes its own chain when none is inherited.
    /// This integration does not persist background chains or attach HTTP response metadata.
    /// </summary>
    public static ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> UseExecutionTracing(
        this ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> module)
    {
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>()
            .AddBehavior(
                typeof(ChainTracingExecutionBehavior<,>),
                ExecutionBehaviorOrder.Diagnostics,
                static descriptor => descriptor.IsBusinessOperation);
        return module;
    }

    /// <summary>
    /// Traces ASP.NET Core controller actions.
    /// </summary>
    public static ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> UseControllerTracing(
        this ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> module)
    {
        module.Require<ModuleControllers, ModuleControllersOption>()
            .ConfigMvcOption(options => options.Filters.Add<ChainTracingControllerActionFilter>());
        return module;
    }

    /// <summary>
    /// Completes controller tracing and attaches a public trace identifier to result envelopes. Hosts that
    /// enable <see cref="ModuleResultEnvelopeOption.ExposeDiagnosticDetails"/> also expose the call chain
    /// and a shared exception catalog. The same policy applies to exception-built responses.
    /// </summary>
    public static ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> AttachControllerTraceMetadata(
        this ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> module)
    {
        module.Require<ModuleExceptionHandling, ModuleExceptionHandlingOption>();
        module.Require<ModuleControllers, ModuleControllersOption>()
            .ConfigMvcOption(options => options.Filters.Add<ChainTracingResultMetadataActionFilter>(
                ChainTracingResultMetadataActionFilter.ExecutionOrder));
        module.ConfigureServices(context =>
        {
            context.Services.TryAddSingleton<ChainResultMetadataAttacher>();
            context.Services.TryAddSingleton<ExceptionResponseChainDiagnostics>();
            context.Services.TryAddSingleton<IExceptionResponseDiagnostics, ExceptionResponseChainDiagnostics>();
        });
        return module;
    }

    /// <summary>
    /// Registers a scoped EF Core command interceptor. Attach it to the application's DbContext options
    /// to capture SQL beneath an active controller, execution, or explicit chain scope. Commands without
    /// an active parent are skipped; this integration never creates a chain root.
    /// </summary>
    public static ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> UseDatabaseTracing(this ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> module)
    {
        module.ConfigureServices(context => { context.Services.TryAddScoped<ChainTracingDbCommandInterceptor>(); });
        return module;
    }

    /// <summary>
    /// Enables actor invocation tracing without buffering or rewriting response bodies.
    /// </summary>
    public static ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> UseActorInvocationTracing(this ModuleRegistration<ModuleChainTracing, ModuleChainTracingOption> module)
    {
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
        module.RequireWebHost("Actor invocation tracing middleware must run in an ASP.NET Core request pipeline.");
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
        module.Require<ModuleExceptionHandling, ModuleExceptionHandlingOption>();

        module.ConfigureServices(context => { context.Services.TryAddTransient<ActorInvocationTracingMiddleware>(); });
        module.ConfigureApplicationBuilder(
            context => { context.ApplicationBuilder.UseMiddleware<ActorInvocationTracingMiddleware>(); },
            ModuleWebStage.AfterRouting);

        return module;
    }

}

/// <summary>
/// Configuration options for the chain tracing module.
/// </summary>
public class ModuleChainTracingOption : ModuleOptions<ModuleChainTracing>
{
    /// <summary>
    /// Maximum chain depth to prevent unbounded recursion.
    /// </summary>
    public int MaxChainDepth { get; set; } = 50;

    /// <summary>
    /// Maximum node count to avoid unbounded memory growth.
    /// </summary>
    public int MaxNodeCount { get; set; } = 1000;

    /// <summary>
    /// Service identity (for example the Dapr app id) of this host. When set, the root node of every chain
    /// carries it, so debug output that aggregates several hosts (a forwarded response) shows which service
    /// recorded each chain. Leave empty for single-process hosts where the origin is obvious.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Deployed version recorded on each chain root. Supply the host assembly's informational or product
    /// version to retain build and commit metadata, preferably from the same source as service discovery.
    /// Defaults to null, which omits the version. This value is independent of <see cref="ServiceName"/>.
    /// </summary>
    public string? ServiceVersion { get; set; }
}
