using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.ApplicationRoles;

namespace Monica.HealthCheck.Services;

/// <summary>Records the typed builder's implementation without executing an arbitrary native check factory.</summary>
internal sealed class TypedHealthCheckFactory(Type type)
{
    public Type ImplementationType => type;
    public IHealthCheck Create(IServiceProvider services) => (IHealthCheck)ActivatorUtilities.GetServiceOrCreateInstance(services, type);
}

internal static class RoleHealthCheckRegistration
{
    internal static void Attach(MonicaApplication application, IServiceCollection services)
    {
        // AddCheck (including native delegate registrations) contributes a named options instance per registration.
        // Read the exact descriptor's provenance before replacing it, while preserving its position and named option behavior.
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.IsKeyedService || descriptor.ServiceType != typeof(IConfigureOptions<HealthCheckServiceOptions>)
                || descriptor.ImplementationInstance is not ConfigureNamedOptions<HealthCheckServiceOptions> contributor) continue;

            var owner = application.Composition.GetServiceRegistrationOwner(descriptor);
            services[i] = ServiceDescriptor.Singleton<IConfigureOptions<HealthCheckServiceOptions>>(provider =>
                new ConfigureNamedOptions<HealthCheckServiceOptions>(contributor.Name, options =>
                {
                    var before = options.Registrations.ToHashSet();
                    contributor.Action?.Invoke(options);
                    if (provider.GetService<IRoleHealthStateProvider>() is not { } roles) return;
                    foreach (var check in options.Registrations.Where(item => !before.Contains(item)
                                 && item.Tags.Contains(HealthCheckTags.Ready, StringComparer.OrdinalIgnoreCase)).ToArray())
                    {
                        var implementation = (check.Factory.Target as TypedHealthCheckFactory)?.ImplementationType ?? typeof(IHealthCheck);
                        if (roles.GetRoleId(implementation, owner) is not { } roleId) continue;
                        var snapshot = roles.GetSnapshot();
                        var role = snapshot.Roles.First(entry => entry.RoleId == roleId);
                        options.Registrations.Remove(check);
                        if (!role.IsActiveOnCurrentHost) continue;
                        var readyTags = check.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (readyTags.Remove(HealthCheckTags.Live))
                        {
                            // Live probes retain their original implementation and failure semantics.
                            var liveTags = check.Tags.Where(tag => !tag.Equals(HealthCheckTags.Ready, StringComparison.OrdinalIgnoreCase));
                            options.Registrations.Add(new HealthCheckRegistration(check.Name + ".live", check.Factory,
                                check.FailureStatus, liveTags, check.Timeout) { Delay = check.Delay, Period = check.Period });
                        }
                        var cap = snapshot.CapOptionalRoleHealthChecksAtDegraded && role.EffectiveCriticality == RoleCriticality.Optional
                                  && snapshot.Roles.Count(entry => entry.IsActiveOnCurrentHost) > 1;
                        options.Registrations.Add(new HealthCheckRegistration(check.Name, scope => new RoleScopedHealthCheck(scope,
                            roles, scope.GetService<IRoleProbeStateSink>(), roleId, check, cap), check.FailureStatus, readyTags)
                            { Delay = check.Delay, Period = check.Period });
                    }
                }));
        }
    }
}
