using Monica.ProjectUnits.Models;

namespace Monica.ProjectUnits.Abstractions;

/// <summary>
/// Provides read-only access to the project units discovered for one Monica host.
/// </summary>
/// <remarks>
/// The catalog is scoped to the owning host. Consumers must resolve it from that host's service provider and must not
/// retain it across independently built Monica applications.
/// </remarks>
public interface IProjectUnitCatalog
{
    /// <summary>
    /// Gets the enum types discovered in the host's business assemblies, keyed by short type name.
    /// </summary>
    IReadOnlyDictionary<string, Type> EnumTypes { get; }

    /// <summary>
    /// Gets all project units discovered for the host.
    /// </summary>
    /// <returns>A snapshot of the current catalog.</returns>
    IReadOnlyList<ProjectUnit> GetAllUnits();

    /// <summary>
    /// Gets discovered project units assignable to <typeparamref name="TProjectUnit"/>.
    /// </summary>
    /// <typeparam name="TProjectUnit">The project-unit model type to select.</typeparam>
    /// <returns>A snapshot containing the matching project units.</returns>
    IReadOnlyList<TProjectUnit> GetUnits<TProjectUnit>() where TProjectUnit : ProjectUnit;

    /// <summary>
    /// Finds a project unit by its CLR full name.
    /// </summary>
    /// <param name="typeFullName">The full name of the represented CLR type.</param>
    /// <returns>The matching unit, or <see langword="null"/> when no unit was discovered.</returns>
    ProjectUnit? FindByFullName(string? typeFullName);

    /// <summary>
    /// Finds a project unit by its short CLR type name.
    /// </summary>
    /// <param name="typeName">The short name of the represented CLR type.</param>
    /// <returns>The matching unit, or <see langword="null"/> when the name is absent or ambiguous across units.</returns>
    ProjectUnit? FindByName(string? typeName);

    /// <summary>
    /// Finds a typed project unit by its CLR full name.
    /// </summary>
    /// <typeparam name="TProjectUnit">The required project-unit model type.</typeparam>
    /// <param name="typeFullName">The full name of the represented CLR type.</param>
    /// <returns>The matching typed unit, or <see langword="null"/> when no compatible unit was discovered.</returns>
    TProjectUnit? FindByFullName<TProjectUnit>(string? typeFullName) where TProjectUnit : ProjectUnit;

    /// <summary>
    /// Finds a typed project unit by its short CLR type name.
    /// </summary>
    /// <typeparam name="TProjectUnit">The required project-unit model type.</typeparam>
    /// <param name="typeName">The short name of the represented CLR type.</param>
    /// <returns>
    /// The matching typed unit, or <see langword="null"/> when the name is absent, ambiguous, or incompatible.
    /// </returns>
    TProjectUnit? FindByName<TProjectUnit>(string? typeName) where TProjectUnit : ProjectUnit;

}
