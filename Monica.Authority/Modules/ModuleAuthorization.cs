using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Authority.Authorization.Abstractions;
using Monica.Authority.Authorization.Exceptions;
using Monica.Authority.Authorization.Services;
using Monica.Authority.Authorization.Services.Behaviors;
using Monica.Authority.Authorization.Services.Support;
using Monica.Authority.Localization;
using Monica.Core;
using Monica.Core.Execution;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleAuthorizationBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Registers standard policy-based authorization without a permission-bit model.</summary>
        public ModuleRegistration<ModuleAuthorization, ModuleAuthorizationOption> AddAuthorization(
            Action<ModuleAuthorizationOption>? configure = null)
            => builder.AddModule<ModuleAuthorization, ModuleAuthorizationOption>(configure);

        /// <summary>
        /// Registers authorization with the host's primary permission-bit definition.
        /// </summary>
        public ModuleRegistration<ModuleAuthorization, ModuleAuthorizationOption> AddAuthorization<TEnum>(
            string claimTypeDefinition)
            where TEnum : struct, Enum
        {
            return builder.AddModule<ModuleAuthorization, ModuleAuthorizationOption>()
                .AddDefaultPermissionBit<TEnum>(claimTypeDefinition);
        }
    }

    extension(ModuleRegistration<ModuleAuthorization, ModuleAuthorizationOption> registration)
    {
        internal ModuleRegistration<ModuleAuthorization, ModuleAuthorizationOption> AddDefaultPermissionBit<TEnum>(
            string claimTypeDefinition)
            where TEnum : struct, Enum
        {
            return registration
                .AddPermissionBit<TEnum>(claimTypeDefinition)
                .ConfigureServices(context =>
                {
                    context.Services.AddSingleton<IPermissionChecker, PermissionChecker<TEnum>>();
                    context.Services.AddSingleton<IAuthorizationHandler, PolicyEnumPermissionRequirementHandler>();
                });
        }

        /// <summary>
        /// Adds another permission-bit definition to authorization checks.
        /// </summary>
        public ModuleRegistration<ModuleAuthorization, ModuleAuthorizationOption> AddPermissionBit<TEnum>(
            string claimTypeDefinition)
            where TEnum : struct, Enum
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimTypeDefinition);
            return registration.ConfigureServices(context =>
            {
                var checker = new PermissionBitChecker<TEnum>(claimTypeDefinition);
                context.Services.AddSingleton<IPermissionBitChecker<TEnum>>(_ => checker);
            });
        }

        /// <summary>
        /// Replaces authorization with permissive implementations for explicitly trusted hosts.
        /// </summary>
        public ModuleRegistration<ModuleAuthorization, ModuleAuthorizationOption> ConfigAsAlwaysAllow()
        {
            return registration.ConfigureServices(context =>
            {
                context.Services.Replace(ServiceDescriptor.Singleton<IAuthorizationService, AlwaysAllowAuthorizationService>());
                context.Services.Replace(ServiceDescriptor.Singleton<IAuthorityAuthorizationService, AlwaysAllowAuthorizationService>());
                context.Services.Replace(ServiceDescriptor
                    .Singleton<IExecutionAuthorizationService, AlwaysAllowExecutionAuthorizationService>());
                context.Services.Replace(ServiceDescriptor.Singleton<IPermissionChecker, AlwaysAllowPermissionChecker>());
            }, ModuleRegistrationOrder.Late);
        }
    }
}

/// <summary>
/// Composes authorization middleware, services, localization, exception mapping, and execution behavior.
/// </summary>
public class ModuleAuthorization : MonicaModule<ModuleAuthorizationOption>, IWebHostRequiredModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static localization => localization.AddResource<AuthorityResource>());
        module.Require<ModuleExceptionHandling, ModuleExceptionHandlingOption>(
            options => options.AddExceptionMapper<AuthorizationExceptionMapper>());
        module.Require<ModuleAuthentication, ModuleAuthenticationOption>();
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(options =>
            options.AddBehavior(
                typeof(ExecutionAuthorizationBehavior<,>),
                ExecutionBehaviorOrder.Authorization,
                static descriptor => descriptor.IsBusinessOperation
                                     && ExecutionAuthorizationMetadata.RequiresAuthorization(descriptor)));
    }

    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleAuthorizationOption> context)
    {
        context.ApplicationBuilder.UseAuthorization();
    }

    protected override ModuleWebStage GetApplicationBuilderStage() => ModuleWebStage.AfterRouting;

    public override void ConfigureServices(ModuleContext<ModuleAuthorizationOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<AuthorityMessageLocalizer>();
        services.AddAuthorization();
        services.AddTransient<DefaultAuthorizationPolicyProvider>();
        services.AddSingleton<IAuthorizationService, AuthorityAuthorizationService>();
        services.AddSingleton<IAuthorityAuthorizationService, AuthorityAuthorizationService>();
        services.AddSingleton<IExecutionAuthorizationService, ExecutionAuthorizationService>();
        services.AddTransient<IAuthorityAuthorizationPolicyProvider, PolicyEnumAuthorizationProvider>();
    }
}

/// <summary>
/// Provides the host-owned authorization configuration object.
/// </summary>
public class ModuleAuthorizationOption : ModuleOptions<ModuleAuthorization>;
