using System.Globalization;
using Microsoft.Extensions.Configuration;
using Monica.ApplicationModel.Models;
using Monica.Core.Configuration;

namespace Monica.ApplicationModel.Services;

/// <summary>Reads one authoritative deployment value without merging entries or indexed role arrays.</summary>
internal static class ApplicationDeploymentReader
{
    private const string SECTION_PATH = "Application:Deployments";

    internal static (ApplicationDeployment Deployment, string Source) Read(string appId,
        IReadOnlyDictionary<string, ApplicationPlacement>? configured, ConfigurationSnapshot configuration)
    {
        var selected = configuration.SelectCompleteSection(SECTION_PATH);
        if (configured is not null && selected is not null)
            throw new InvalidOperationException($"'{SECTION_PATH}' cannot be supplied both through configuration ({Describe(selected.Origin)}) and ModuleApplicationModelOption.Deployments.");
        if (configured is null && selected is null)
            throw new InvalidOperationException($"Supply a complete '{SECTION_PATH}' map or configure ModuleApplicationModelOption.Deployments.");

        var source = configured is not null ? "ModuleApplicationModelOption.Deployments"
            : $"'{SECTION_PATH}' from {Describe(selected!.Origin)}";
        try
        {
            if (configured is not null) return (ApplicationDeployment.Create(appId, configured), source);
            var provider = selected!;
            var root = provider.GetSection(SECTION_PATH);
            RequireShape(root, ConfigurationValueShape.Object);
            var deployments = new Dictionary<string, ApplicationPlacement>(StringComparer.Ordinal);
            foreach (var application in root.GetChildren())
            {
                RequireShape(application, ConfigurationValueShape.Object);
                foreach (var property in application.GetChildren())
                    if (!property.Key.Equals(nameof(ApplicationPlacement.AppName), StringComparison.OrdinalIgnoreCase)
                        && !property.Key.Equals(nameof(ApplicationPlacement.Roles), StringComparison.OrdinalIgnoreCase))
                        throw Invalid(property.Path, "is not a recognized application placement field");

                var name = application.GetSection(nameof(ApplicationPlacement.AppName));
                if (provider.Contains(name.Path)) RequireShape(name, ConfigurationValueShape.Scalar);
                var roles = application.GetSection(nameof(ApplicationPlacement.Roles));
                RequireShape(roles, ConfigurationValueShape.List);
                var roleIds = new List<string>();
                foreach (var role in roles.GetChildren())
                {
                    if (role.Key != roleIds.Count.ToString(CultureInfo.InvariantCulture))
                        throw Invalid(roles.Path, "must contain contiguous zero-based role indexes");
                    RequireShape(role, ConfigurationValueShape.Scalar);
                    if (string.IsNullOrWhiteSpace(role.Value)) throw Invalid(role.Path, "must contain a nonempty role identifier");
                    roleIds.Add(role.Value);
                }
                deployments.Add(application.Key, new ApplicationPlacement { AppName = name.Value, Roles = roleIds.ToArray() });
            }
            return (ApplicationDeployment.Create(appId, deployments), source);
        }
        catch (InvalidOperationException error)
        {
            throw ValidationFailure(source, error);
        }

        void RequireShape(IConfigurationSection section, ConfigurationValueShape expected)
        {
            if (!selected!.TryGetShape(section.Path, out var shape)
                || shape != expected && !(shape == ConfigurationValueShape.Container
                    && expected is ConfigurationValueShape.Object or ConfigurationValueShape.List)
                || expected == ConfigurationValueShape.Scalar && section.GetChildren().Any())
                throw Invalid(section.Path, $"must be an explicit {expected.ToString().ToLowerInvariant()} value");
        }

        InvalidOperationException Invalid(string path, string reason) =>
            new($"'{path}' {reason}.");
    }

    internal static InvalidOperationException ValidationFailure(string source, InvalidOperationException error) =>
        new($"Application model validation failed using deployment input {source}.", error);

    private static string Describe(ConfigurationProviderOrigin origin) =>
        $"provider '{origin.ProviderType.Name}' at '{origin.ProviderPath}'"
        + (origin.SourcePath is { } path ? $" (source '{path}')" : string.Empty);
}
