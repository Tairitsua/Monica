using System.Collections.Frozen;

namespace Monica.ApplicationModel.Models;

/// <summary>A startup snapshot of logical application placement in one deployment scope.</summary>
/// <remarks>Replicas share an AppId and its roles. Role identifiers are exact, ordinal identifiers without inheritance.</remarks>
public sealed class ApplicationDeployment
{
    private ApplicationDeployment(string appId, FrozenDictionary<string, ApplicationDeploymentEntry> deployments,
        FrozenDictionary<string, string> owners)
    {
        AppId = appId;
        Deployments = deployments;
        RoleApplications = owners;
    }

    /// <summary>Gets the logical identity of this application, independent of its process instance.</summary>
    public string AppId { get; }

    /// <summary>Gets the selected application's canonical display name.</summary>
    public string AppName => Deployments[AppId].AppName;

    /// <summary>Gets every explicitly declared application, including applications with no active roles.</summary>
    public IReadOnlyDictionary<string, ApplicationDeploymentEntry> Deployments { get; }

    /// <summary>Gets the unique logical owner of every configured role.</summary>
    public IReadOnlyDictionary<string, string> RoleApplications { get; }

    /// <summary>Whether this application's placement contains the exact role identifier.</summary>
    public bool IsRoleActive(string roleId) => RoleApplications.TryGetValue(roleId, out var owner) && owner == AppId;

    /// <summary>Copies and validates configuration before either imperative role composition or model registration.</summary>
    /// <exception cref="InvalidOperationException">The current application is missing or a role has multiple owners.</exception>
    public static ApplicationDeployment Create(string appId, IReadOnlyDictionary<string, ApplicationPlacement> deployments)
    {
        RequireIdentifier(appId, "AppId");
        ArgumentNullException.ThrowIfNull(deployments);
        if (!deployments.Keys.Contains(appId, StringComparer.Ordinal))
            throw new InvalidOperationException($"Current AppId '{appId}' is absent from the application deployment map.");
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var applications = new Dictionary<string, ApplicationDeploymentEntry>(StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roleIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (owner, placement) in deployments)
        {
            RequireIdentifier(owner, "AppId");
            if (!identifiers.Add(owner))
                throw new InvalidOperationException($"AppId '{owner}' is ambiguous under case-insensitive configuration lookup.");
            if (placement?.Roles is null)
                throw new InvalidOperationException($"Application '{owner}' must declare its roles (an empty array is allowed).");
            var appName = placement.AppName ?? owner;
            RequireIdentifier(appName, "AppName");
            foreach (var role in placement.Roles)
            {
                RequireIdentifier(role, "Role");
                if (!owners.TryAdd(role, owner))
                    throw new InvalidOperationException($"Role '{role}' is assigned more than once: '{owners[role]}' and '{owner}'.");
                if (!roleIdentifiers.Add(role))
                    throw new InvalidOperationException($"Role '{role}' is ambiguous under case-insensitive configuration lookup.");
            }
            applications.Add(owner, new ApplicationDeploymentEntry(appName, Array.AsReadOnly(placement.Roles.ToArray())));
        }
        return new ApplicationDeployment(appId, applications.ToFrozenDictionary(StringComparer.Ordinal),
            owners.ToFrozenDictionary(StringComparer.Ordinal));
    }

    internal static void RequireIdentifier(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new InvalidOperationException($"{kind} must be a non-empty identifier without surrounding whitespace.");
    }
}
