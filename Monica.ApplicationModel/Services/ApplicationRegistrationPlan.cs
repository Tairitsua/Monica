using System.Reflection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.ApplicationModel.Services;

/// <summary>Validates local registration declarations before any user registration callback executes.</summary>
internal sealed record ApplicationRegistrationPlan(string OwnerId, Type RegistrationType, bool IsActive)
{
    internal static IReadOnlyList<ApplicationRegistrationPlan> Create(IApplicationManifest manifest,
        IApplicationModel model, ITypeFinder finder)
    {
        var assemblies = finder.GetAssemblies().ToDictionary(static assembly => assembly.GetName().Name!, StringComparer.Ordinal);
        var plans = new List<ApplicationRegistrationPlan>();
        foreach (var name in manifest.LocalAssemblies)
        {
            var assembly = assemblies[name];
            var domain = assembly.GetCustomAttribute<ApplicationDomainAttribute>();
            var role = assembly.GetCustomAttribute<ApplicationRoleAttribute>();
            var registration = domain?.Registration ?? role?.Registration;
            if (registration is null) continue;
            var ownerId = domain?.DomainId ?? role!.RoleId;
            if (!typeof(ApplicationRegistration).IsAssignableFrom(registration) || registration.IsAbstract
                || registration.ContainsGenericParameters || registration.Assembly != assembly
                || registration.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"Registration '{registration}' for '{ownerId}' must be a concrete ApplicationRegistration in its declaring assembly with a public parameterless constructor.");
            if (domain is { BackgroundRole: null }
                && registration.GetMethod(nameof(ApplicationRegistration.ConfigureActive), [typeof(ApplicationRegistrationContext)])!.DeclaringType != typeof(ApplicationRegistration))
                throw new InvalidOperationException($"Domain '{ownerId}' must declare BackgroundRole to own its ConfigureActive registration.");
            var activeRole = domain?.BackgroundRole ?? role?.RoleId;
            plans.Add(new ApplicationRegistrationPlan(ownerId, registration,
                activeRole is not null && model.IsRoleActive(activeRole)));
        }
        return plans.OrderBy(static plan => plan.OwnerId, StringComparer.Ordinal)
            .ThenBy(static plan => plan.RegistrationType.Assembly.FullName, StringComparer.Ordinal).ToArray();
    }
}
