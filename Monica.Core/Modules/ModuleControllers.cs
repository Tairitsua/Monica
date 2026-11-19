using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Execution.Mvc;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleControllersBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Controllers module.
        /// </summary>
        public ModuleRegistration<ModuleControllers, ModuleControllersOption> AddControllers(
            Action<ModuleControllersOption>? action = null)
        {
            return builder.AddModule<ModuleControllers, ModuleControllersOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleControllers, ModuleControllersOption> registration)
    {
        public ModuleRegistration<ModuleControllers, ModuleControllersOption> ConfigDependentServices(
            Action<IServiceCollection> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return registration.ConfigureServices(context => configure(context.Services));
        }

        public ModuleRegistration<ModuleControllers, ModuleControllersOption> ConfigMvcBuilder(
            Action<IMvcBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return registration.Configure(options => options.AddMvcBuilderAction(configure));
        }

        public ModuleRegistration<ModuleControllers, ModuleControllersOption> ConfigMvcOption(
            Action<MvcOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return registration.Configure(options => options.AddMvcOptionAction(configure));
        }
    }
}

public class ModuleControllers : MonicaModule<ModuleControllersOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipeline>();
    }

    public override void ConfigureServices(ModuleContext<ModuleControllersOption> context)
    {
        context.Services.AddScoped<ExecutionPipelineMvcFilter>();
    }

    public override void PostConfigureServices(ModuleContext<ModuleControllersOption> context)
    {
        var mvcBuilder = context.Services.AddControllers(options =>
            options.Filters.AddService(typeof(ExecutionPipelineMvcFilter)));

        foreach (var action in Option.MvcBuilderActions)
        {
            action(mvcBuilder);
        }

        foreach (var action in Option.MvcOptionActions)
        {
            context.Services.Configure(action);
        }
    }
}

public class ModuleControllersOption : ModuleOptions<ModuleControllers>
{
    internal List<Action<IMvcBuilder>> MvcBuilderActions { get; } = [];
    internal List<Action<MvcOptions>> MvcOptionActions { get; } = [];

    internal void AddMvcBuilderAction(Action<IMvcBuilder> action)
    {
        MvcBuilderActions.Add(action);
    }

    internal void AddMvcOptionAction(Action<MvcOptions> action)
    {
        MvcOptionActions.Add(action);
    }
}
