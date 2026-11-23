using Monica.Tool.Extensions;
using Monica.WebApi.AutoControllers.Abstractions;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>
/// Describes a generated CRUD controller whose CLR name does not start with the configured service name prefix.
/// </summary>
internal sealed record CrudControllerPrefixMismatch(
    string ControllerName,
    string ControllerType,
    string RequiredPrefix)
{
    internal const string SOURCE = "AutoControllers.CrudControllerPrefix";

    /// <summary>
    /// Creates a diagnostic only for participating CRUD services whose name violates a non-empty prefix.
    /// </summary>
    internal static CrudControllerPrefixMismatch? Create(Type type, string? requiredPrefix)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.IsImplementInterface<ICrudApplicationService>()
               && !string.IsNullOrEmpty(requiredPrefix)
               && !type.Name.StartsWith(requiredPrefix, StringComparison.Ordinal)
            ? new CrudControllerPrefixMismatch(
                type.Name,
                type.GetCleanFullName(),
                requiredPrefix)
            : null;
    }
}
