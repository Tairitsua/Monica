using Monica.BuildingBlocks.Models;

namespace Monica.BuildingBlocks.Abstractions;

/// <summary>
/// Provides read-only access to the building blocks discovered for one Monica host.
/// </summary>
/// <remarks>
/// The catalog is scoped to the owning host. Consumers must resolve it from that host's service provider and must not
/// retain it across independently built Monica applications.
/// </remarks>
public interface IBuildingBlockCatalog
{
    /// <summary>
    /// Gets the enum types discovered in the host's business assemblies, keyed by short type name.
    /// </summary>
    IReadOnlyDictionary<string, Type> EnumTypes { get; }

    /// <summary>
    /// Gets all building blocks discovered for the host.
    /// </summary>
    /// <returns>A snapshot of the current catalog.</returns>
    IReadOnlyList<BuildingBlock> GetAll();

    /// <summary>
    /// Gets discovered building blocks assignable to <typeparamref name="TBuildingBlock"/>.
    /// </summary>
    /// <typeparam name="TBuildingBlock">The building-block model type to select.</typeparam>
    /// <returns>A snapshot containing the matching building blocks.</returns>
    IReadOnlyList<TBuildingBlock> GetAll<TBuildingBlock>() where TBuildingBlock : BuildingBlock;

    /// <summary>
    /// Finds a building block by its CLR full name.
    /// </summary>
    /// <param name="typeFullName">The full name of the represented CLR type.</param>
    /// <returns>The matching building block, or <see langword="null"/> when no building block was discovered.</returns>
    BuildingBlock? FindByFullName(string? typeFullName);

    /// <summary>
    /// Finds a building block by its short CLR type name.
    /// </summary>
    /// <param name="typeName">The short name of the represented CLR type.</param>
    /// <returns>The matching building block, or <see langword="null"/> when the name is absent or ambiguous across building blocks.</returns>
    BuildingBlock? FindByName(string? typeName);

    /// <summary>
    /// Finds a typed building block by its CLR full name.
    /// </summary>
    /// <typeparam name="TBuildingBlock">The required building-block model type.</typeparam>
    /// <param name="typeFullName">The full name of the represented CLR type.</param>
    /// <returns>The matching typed building block, or <see langword="null"/> when no compatible building block was discovered.</returns>
    TBuildingBlock? FindByFullName<TBuildingBlock>(string? typeFullName) where TBuildingBlock : BuildingBlock;

    /// <summary>
    /// Finds a typed building block by its short CLR type name.
    /// </summary>
    /// <typeparam name="TBuildingBlock">The required building-block model type.</typeparam>
    /// <param name="typeName">The short name of the represented CLR type.</param>
    /// <returns>
    /// The matching typed building block, or <see langword="null"/> when the name is absent, ambiguous, or incompatible.
    /// </returns>
    TBuildingBlock? FindByName<TBuildingBlock>(string? typeName) where TBuildingBlock : BuildingBlock;

}
