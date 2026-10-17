using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Application.Abstractions;
using Monica.Application.Invocation;
using Monica.Rpc.Abstractions;
using Monica.Application.Validation;
using Monica.Core;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

namespace Monica.Modules;

/// <summary>Composes use-case execution independently of HTTP exposure.</summary>
public static class ModuleApplicationBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Registers mediator dispatch, dependency injection, authorization, and shared input validation.</summary>
        /// <remarks>Persistence and UnitOfWork remain explicit host choices. This module does not publish endpoints.</remarks>
        public ModuleRegistration<ModuleApplication, ModuleApplicationOption> AddApplication(Action<ModuleApplicationOption>? configure = null)
            => builder.AddModule<ModuleApplication, ModuleApplicationOption>(configure);
    }
}

/// <summary>Owns application execution prerequisites shared by local and HTTP adapters.</summary>
public class ModuleApplication : MonicaModule<ModuleApplicationOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleMediator, ModuleMediatorOption>();
        module.Require<ModuleDependencyInjection, ModuleDependencyInjectionOption>();
        module.Require<ModuleObjectMapping, ModuleObjectMappingOption>();
        module.Require<ModuleAuthorization, ModuleAuthorizationOption>();
        module.Require<ModuleExceptionHandling, ModuleExceptionHandlingOption>(
            options => options.AddExceptionMapper<ValidationExceptionMapper>());
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(options => options.AddBehavior(
            typeof(ApplicationValidationBehavior<,>), ExecutionBehaviorOrder.Validation,
            descriptor => descriptor.Point == MediatorExecutionPoints.Request));
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(options => options.AddBehavior(
            typeof(ApplicationOperationBehavior<,>), ExecutionBehaviorOrder.Diagnostics - 100,
            descriptor => descriptor.Point == MediatorExecutionPoints.Request));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleApplicationOption> context)
    {
        context.Services.TryAddSingleton<ApplicationOperationContext>();
        context.Services.TryAddScoped<IApplicationCallerContext, ApplicationCallerContext>();
        context.Services.Replace(ServiceDescriptor.Scoped<ILocalRpcInvoker, ApplicationRpcInvoker>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadOnlyRequestConvention, ApplicationQueryConvention>());
    }
}

/// <summary>Configures the transport-independent application execution graph.</summary>
public class ModuleApplicationOption : ModuleOptions<ModuleApplication>;
