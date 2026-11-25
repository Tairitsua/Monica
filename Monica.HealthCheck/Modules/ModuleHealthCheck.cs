using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.ApplicationRoles;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.HealthCheck;
using Monica.HealthCheck.Facades;
using Monica.HealthCheck.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Provides the registration entry point for Monica's unified health-check module.
/// </summary>
public static class ModuleHealthCheckBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers native ASP.NET Core health checks, the sanitized snapshot facade, and optional probe endpoints.
        /// Probe endpoints are mapped in every web-host environment and are anonymous by design so infrastructure
        /// orchestrators can evaluate the process before application authentication is available.
        /// </summary>
        /// <param name="action">Optional module configuration.</param>
        /// <returns>The host-bound health-check module registration.</returns>
        public ModuleRegistration<ModuleHealthCheck, ModuleHealthCheckOption> AddHealthCheck(
            Action<ModuleHealthCheckOption>? action = null)
        {
            return builder.AddModule<ModuleHealthCheck, ModuleHealthCheckOption>(action);
        }
    }
}

/// <summary>
/// Registers the host health-check runtime and maps liveness and readiness probe endpoints for web hosts.
/// </summary>
public sealed class ModuleHealthCheck : MonicaModule<ModuleHealthCheckOption>, IWebModule
{
    private const string SELF_CHECK_NAME = "monica.self";

    /// <inheritdoc />
    public override void ValidateOptions(ModuleHealthCheckOption options, string? profileName)
    {
        if (options.EnableReadinessEndpoint)
        {
            ValidateEndpointPath(options.ReadinessPath, nameof(options.ReadinessPath));
        }

        if (options.EnableLivenessEndpoint)
        {
            ValidateEndpointPath(options.LivenessPath, nameof(options.LivenessPath));
        }

        if (options.EnableReadinessEndpoint &&
            options.EnableLivenessEndpoint &&
            string.Equals(options.ReadinessPath, options.LivenessPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{nameof(ModuleHealthCheckOption.ReadinessPath)} and " +
                $"{nameof(ModuleHealthCheckOption.LivenessPath)} must be distinct when both probe endpoints are enabled.");
        }
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleHealthCheckOption> context)
    {
        context.Services.AddHealthChecks()
            .AddCheck(
                SELF_CHECK_NAME,
                static () => HealthCheckResult.Healthy("The Monica host process is responsive."),
                tags: [HealthCheckTags.Live, HealthCheckTags.Ready, HealthCheckTags.Monica]);

        context.Services.TryAddSingleton<RoleHealthFacade>(provider => new RoleHealthFacade(
            provider.GetRequiredService<MonicaApplication>(), provider.GetService<IRoleHealthStateProvider>()));
        context.Application.Composition.OnServicesFinalized(services =>
        {
            if (!services.Any(descriptor => descriptor.ServiceType == typeof(IRoleHealthStateProvider))) return;
            RoleHealthCheckRegistration.Attach(context.Application, services);
            services.AddHealthChecks().AddCheck<ApplicationRolesHealthCheck>(ApplicationRolesHealthCheck.Name,
                tags: [HealthCheckTags.Monica, HealthCheckTags.Ready, "roles"]);
        });

        context.Services.TryAddSingleton<HealthCheckSnapshotService>(static provider =>
            new HealthCheckSnapshotService(provider.GetRequiredService<HealthCheckService>()));
        context.Services.TryAddSingleton<HealthCheckFacade>(static provider => new HealthCheckFacade(
            provider.GetRequiredService<HealthCheckSnapshotService>(),
            provider.GetRequiredService<ILogger<HealthCheckFacade>>()));
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleHealthCheckOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            if (Option.EnableReadinessEndpoint)
            {
                endpoints.MapHealthChecks(
                        Option.ReadinessPath,
                        CreateEndpointOptions(HealthCheckTags.Ready))
                    .AllowAnonymous()
                    .WithMonicaEndpoint(MonicaEndpointKind.HealthProbe);
            }

            if (Option.EnableLivenessEndpoint)
            {
                endpoints.MapHealthChecks(
                        Option.LivenessPath,
                        CreateEndpointOptions(HealthCheckTags.Live))
                    .AllowAnonymous()
                    .WithMonicaEndpoint(MonicaEndpointKind.HealthProbe);
            }
        });
    }

    private static HealthCheckOptions CreateEndpointOptions(string requiredTag)
    {
        return new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(
                requiredTag,
                StringComparer.OrdinalIgnoreCase),
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            },
            ResponseWriter = WriteProbeResponseAsync
        };
    }

    private static Task WriteProbeResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(report.Status.ToString(), context.RequestAborted);
    }

    private static void ValidateEndpointPath(string path, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, propertyName);

        if (path.Length == 1 ||
            path[0] != '/' ||
            path.Contains('?', StringComparison.Ordinal) ||
            path.Contains('#', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{propertyName} must be an absolute application path other than '/' and cannot contain a query or fragment.");
        }
    }
}

/// <summary>
/// Configures health probe endpoints for a Monica host.
/// </summary>
public sealed class ModuleHealthCheckOption : ModuleOptions<ModuleHealthCheck>
{
    /// <summary>
    /// Gets or sets whether the readiness endpoint is mapped. Defaults to <see langword="true"/>.
    /// Disable it only when another host-owned endpoint provides the readiness contract.
    /// </summary>
    public bool EnableReadinessEndpoint { get; set; } = true;

    /// <summary>
    /// Gets or sets the distinct absolute path used for readiness probes. Defaults to <c>/health</c>.
    /// Readiness executes checks tagged with <see cref="HealthCheckTags.Ready"/>.
    /// </summary>
    public string ReadinessPath { get; set; } = "/health";

    /// <summary>
    /// Gets or sets whether the liveness endpoint is mapped. Defaults to <see langword="true"/>.
    /// Disable it only when another host-owned endpoint provides the liveness contract.
    /// </summary>
    public bool EnableLivenessEndpoint { get; set; } = true;

    /// <summary>
    /// Gets or sets the distinct absolute path used for liveness probes. Defaults to <c>/alive</c>.
    /// Liveness executes checks tagged with <see cref="HealthCheckTags.Live"/>.
    /// </summary>
    public string LivenessPath { get; set; } = "/alive";
}
