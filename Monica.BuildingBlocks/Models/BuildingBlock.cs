using System.Reflection;
using Microsoft.Extensions.Logging;
using Monica.Core.Execution;
using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.BuildingBlocks.Annotations;
using Monica.BuildingBlocks.Services.Support;
using Monica.Tool.Extensions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes a discovered application type and its relationships within one Monica host.
/// This reflection-backed catalog model is not a base class for application types.
/// </summary>
public abstract class BuildingBlock
{
    private protected BuildingBlock(
        BusinessTypeShape shape,
        BuildingBlockKind kind,
        BuildingBlockCatalog catalog,
        params ExecutionPoint[] executionPoints)
    {
        Shape = shape;
        ClrType = shape.Type;
        Kind = kind;
        Title = ClrType.Name;
        ExecutionPoints = [.. executionPoints.Select(static point => point.Value)];
        Catalog = catalog;
    }

    private protected BuildingBlockCatalog Catalog { get; }

    private protected BusinessTypeShape Shape { get; }

    private protected ILogger Logger => Catalog.Logger;

    /// <summary>
    /// Gets the naming rule configured for this building-block kind, falling back to the building block's default convention.
    /// </summary>
    internal BuildingBlockNamingRule? ConventionOption =>
        Catalog.NamingOptions.Rules.TryGetValue(Kind, out var option)
            ? option
            : DefaultConventionOption();

    /// <summary>
    /// Gets the stable building-block key, which is the represented CLR type's full name.
    /// </summary>
    public string Key => ClrType.FullName!;

    /// <summary>
    /// Gets or sets the building-block display name.
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// Gets the effective building-block description using metadata, configuration annotation, then XML documentation
    /// precedence.
    /// </summary>
    public string? Description => MetadataDescription ?? ConfigurationDescription ?? XmlDocumentationDescription;

    /// <summary>
    /// Gets the description declared by explicit building-block metadata.
    /// </summary>
    public string? MetadataDescription { get; private set; }

    /// <summary>
    /// Gets the description declared by a configuration annotation when this building block represents configuration.
    /// </summary>
    public string? ConfigurationDescription { get; private set; }

    /// <summary>
    /// Gets the description loaded from XML documentation.
    /// </summary>
    public string? XmlDocumentationDescription { get; private set; }

    /// <summary>
    /// Gets the valid, normalized title declared by <see cref="BuildingBlockMetadataAttribute"/>.
    /// </summary>
    public string? MetadataTitle { get; private set; }

    /// <summary>
    /// Gets whether the represented type explicitly declares building-block metadata.
    /// </summary>
    public bool HasExplicitMetadata { get; private set; }

    /// <summary>
    /// Gets the normalized building-block owner.
    /// </summary>
    public string? Owner { get; private set; }

    /// <summary>
    /// Gets normalized building-block classification tags.
    /// </summary>
    public IReadOnlyList<string> Tags { get; private set; } = [];

    /// <summary>
    /// Gets normalized, case-insensitively deduplicated requirement identifiers.
    /// </summary>
    public IReadOnlyList<string> RequirementIds { get; private set; } = [];

    /// <summary>
    /// Gets the represented CLR type.
    /// </summary>
    public Type ClrType { get; }

    /// <summary>
    /// Gets the architectural category assigned to the building block.
    /// </summary>
    public BuildingBlockKind Kind { get; protected set; }

    /// <summary>
    /// Gets the stable execution points supported by Monica adapters for this building-block kind. A listed point indicates that
    /// matching work can enter that boundary; it does not guarantee that every instance emits it. Unsupported,
    /// collaborator, and data-only building blocks expose an empty list.
    /// </summary>
    public IReadOnlyList<string> ExecutionPoints { get; }

    /// <summary>
    /// Gets the building blocks on which this building block depends.
    /// </summary>
    public HashSet<BuildingBlock> Dependencies { get; protected set; } = [];

    /// <summary>
    /// Gets architecture alerts raised while analyzing the building block.
    /// </summary>
    public List<BuildingBlockAlert> Alerts { get; protected set; } = [];

    /// <summary>
    /// Gets public methods discovered for the building block.
    /// </summary>
    public List<BuildingBlockMethod> Methods { get; protected set; } = [];

    /// <summary>
    /// Gets the parameter types declared by the building block's primary public constructor.
    /// </summary>
    public List<Type> ConstructorParameterTypes { get; protected set; } = [];

    /// <summary>
    /// Connects this building block to other building blocks after discovery has completed.
    /// </summary>
    public virtual void DoingConnect()
    {
        if (ShouldAnalyzeConstructorDependencies)
        {
            DetectConstructorUnitDependencies();
        }
    }

    /// <summary>
    /// Gets whether constructor dependencies should participate in building-block analysis.
    /// </summary>
    protected virtual bool ShouldAnalyzeConstructorDependencies => false;

    /// <summary>
    /// Enriches the building block with normalized metadata, requirements, documentation, and constructor metadata.
    /// </summary>
    public virtual void EnrichMetadata()
    {
        InitializeClassInfo();

        if (Shape.GetAttributes<BuildingBlockMetadataAttribute>(inherit: false).SingleOrDefault() is { } metadata)
        {
            HasExplicitMetadata = true;
            MetadataTitle = Normalize(metadata.Title);
            if (MetadataTitle is null)
            {
                AddMetadataWarning(
                    "BuildingBlock.Metadata.Title.Empty",
                    "Building-block metadata title must not be empty.");
            }
            else
            {
                Title = MetadataTitle;
            }

            Owner = Normalize(metadata.Owner);
            MetadataDescription = Normalize(metadata.Description);
            Tags = NormalizeTags(metadata.Tags ?? []);
        }

        RequirementIds = NormalizeRequirements();
    }

    /// <summary>
    /// Records a relationship between this building block and another building block.
    /// </summary>
    /// <param name="unit">The related building block.</param>
    /// <param name="isDependent">
    /// <see langword="true"/> when this building block depends on <paramref name="unit"/>; otherwise the relationship is
    /// informational and is handled by a specialized building block model.
    /// </param>
    public virtual void DeclareRelevance(BuildingBlock unit, bool isDependent = false)
    {
        if (isDependent)
        {
            Dependencies.Add(unit);
        }
    }

    /// <summary>
    /// Gets dependencies assignable to a specific building-block model type.
    /// </summary>
    /// <typeparam name="TBuildingBlock">The dependency model type to select.</typeparam>
    /// <returns>A snapshot of matching dependencies.</returns>
    public virtual IReadOnlyList<TBuildingBlock> FetchDependency<TBuildingBlock>() where TBuildingBlock : BuildingBlock
    {
        return [.. Dependencies.OfType<TBuildingBlock>()];
    }

    /// <summary>
    /// Initializes public method metadata for the represented type.
    /// </summary>
    protected void InitializeMethods()
    {
        Methods = Catalog.Documentation.GetPublicMethods(ClrType);
    }

    /// <summary>
    /// Initializes public method metadata while excluding methods declared by <typeparamref name="TBaseType"/>.
    /// </summary>
    /// <typeparam name="TBaseType">The base type whose declared methods should be excluded.</typeparam>
    protected void InitializeMethods<TBaseType>()
    {
        Methods = Catalog.Documentation.GetPublicMethods(ClrType, typeof(TBaseType));
    }

    /// <summary>
    /// Preserves the description declared by the Monica configuration annotation.
    /// </summary>
    internal void SetConfigurationDescription(string? description)
    {
        ConfigurationDescription = Normalize(description);
    }

    /// <summary>
    /// Applies optional XML documentation after structural discovery is complete.
    /// </summary>
    internal void ApplyDocumentation(BuildingBlockDocumentationResolver documentation)
    {
        XmlDocumentationDescription = Normalize(documentation.ExtractTypeDescription(ClrType));
        foreach (var method in Methods)
        {
            method.Description = Normalize(documentation.ExtractMethodDescription(method.MethodInfo));
        }
    }

    /// <summary>
    /// Gets the default naming convention for this building block model.
    /// </summary>
    /// <returns>The default convention, or <see langword="null"/> when no default applies.</returns>
    protected virtual BuildingBlockNamingRule? DefaultConventionOption()
    {
        return null;
    }

    /// <summary>
    /// Validates the configured naming convention.
    /// </summary>
    /// <returns><see langword="true"/> when the name satisfies the convention or convention checking is disabled.</returns>
    protected virtual bool VerifyNameConvention()
    {
        if (!Catalog.NamingOptions.Enabled || ConventionOption is not { } option)
        {
            return true;
        }

        var success = true;
        if (option.Suffix is { } postfix)
        {
            success &= ClrType.Name.EndsWith(postfix, StringComparison.Ordinal);
        }

        if (success && option.Prefix is { } prefix)
        {
            success &= ClrType.Name.StartsWith(prefix, StringComparison.Ordinal);
        }

        return success;
    }

    /// <summary>
    /// Applies the configured naming-convention failure mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when strict convention validation fails.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an unsupported convention mode is configured.</exception>
    protected void CheckMode()
    {
        if (VerifyNameConvention() || ConventionOption is not { } option)
        {
            return;
        }

        var alertMessage = $"{ClrType.GetCleanFullName()} must satisfy the naming convention: {option}";

        switch (option.Mode ?? Catalog.NamingOptions.Mode)
        {
            case BuildingBlockNamingMode.Strict:
                Alerts.Add(new BuildingBlockAlert
                {
                    Level = EAlertLevel.Error,
                    Message = alertMessage,
                    Source = "NamingConvention"
                });
                throw new InvalidOperationException(alertMessage);
            case BuildingBlockNamingMode.Warning:
                Alerts.Add(new BuildingBlockAlert
                {
                    Level = EAlertLevel.Warning,
                    Message = alertMessage,
                    Source = "NamingConvention"
                });
                Logger.LogError("{NamingConventionAlert}", alertMessage);
                break;
            case BuildingBlockNamingMode.Disable:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"BuildingBlock[{Kind}] - {Title}({Key})";
    }

    private void InitializeClassInfo()
    {
        var mainConstructor = Shape.Constructors
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .FirstOrDefault();
        if (mainConstructor is not null)
        {
            ConstructorParameterTypes = [.. mainConstructor.GetParameters().Select(parameter => parameter.ParameterType)];
        }
    }

    private IReadOnlyList<string> NormalizeTags(IEnumerable<string?> tags)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            if (Normalize(tag) is not { } value)
            {
                AddMetadataWarning(
                    "BuildingBlock.Metadata.Tag.Empty",
                    "Building-block metadata tags must not contain empty values.");
                continue;
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private IReadOnlyList<string> NormalizeRequirements()
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in Shape.GetAttributes<BuildingBlockRequirementAttribute>(inherit: false))
        {
            if (Normalize(requirement.RequirementId) is not { } requirementId)
            {
                AddMetadataWarning(
                    "BuildingBlock.Requirement.Id.Empty",
                    "Building-block requirement identifiers must not be empty.");
                continue;
            }

            if (seen.Add(requirementId))
            {
                normalized.Add(requirementId);
            }
        }

        return normalized;
    }

    private void AddMetadataWarning(string source, string message)
    {
        Alerts.Add(new BuildingBlockAlert
        {
            Level = EAlertLevel.Warning,
            Message = message,
            Source = source
        });
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void DetectConstructorUnitDependencies()
    {
        foreach (var constructor in ClrType.GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                if (Catalog.ResolveConstructorDependency(parameter.ParameterType, this) is not { } dependentUnit)
                {
                    continue;
                }

                DeclareRelevance(dependentUnit, true);
                dependentUnit.DeclareRelevance(this);
                Logger.LogDebug(
                    "Building block {BuildingBlockKey} depends on {DependencyUnitKey} through its constructor.",
                    Key,
                    dependentUnit.Key);
            }
        }
    }
}
