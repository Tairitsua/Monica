using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Mediator;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Services.Support;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.TypeDiscovery.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleMediatorBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Mediator module.
        /// </summary>
        public ModuleRegistration<ModuleMediator, ModuleMediatorOption> AddMediator(
            Action<ModuleMediatorOption>? action = null)
        {
            return builder.AddModule<ModuleMediator, ModuleMediatorOption>(action);
        }
    }
}

public class ModuleMediator : MonicaModule<ModuleMediatorOption>
{
    public override void ConfigureServices(ModuleContext<ModuleMediatorOption> context)
    {
        context.Services.TryAddTransient<IMediator, Mediator>();
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>();
    }

    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleMediatorOption> discovery)
    {
        discovery.Match(
            TypeQuery.ConcreteClass.ImplementsOpenGeneric(typeof(IRequestHandler<,>)),
            (context, matches) =>
            {
                foreach (var match in matches)
                {
                    if (Application.TypeActivation?.GetActivation(match.Type, TypeActivationKind.RequestHandler) == ActivationMode.Disabled) continue;
                    RegisterRequestHandlers(context.Registrations, match);
                }
            });
    }

    private static void RegisterRequestHandlers(
        ModuleServiceRegistrationWriter registrations,
        BusinessTypeMatch match)
    {
        var implementationType = match.Type;

        if (implementationType.IsGenericTypeDefinition)
        {
            foreach (var serviceType in match.OpenGenericInterfaces)
            {
                registrations.TryAdd(ServiceDescriptor.Transient(
                    serviceType.ClosedInterface.GetGenericTypeDefinition(),
                    implementationType));
            }

            return;
        }

        // Keep one canonical activation path for closed handlers. Other modules may enrich or replace the concrete
        // registration, while the mediator contract remains an alias to that final registration.
        registrations.TryAdd(ServiceDescriptor.Transient(implementationType, implementationType));
        foreach (var serviceType in match.OpenGenericInterfaces)
        {
            registrations.TryAdd(ServiceDescriptor.Transient(
                serviceType.ClosedInterface,
                provider => provider.GetRequiredService(implementationType)));
        }
    }
}

public class ModuleMediatorOption : ModuleOptions<ModuleMediator>;
