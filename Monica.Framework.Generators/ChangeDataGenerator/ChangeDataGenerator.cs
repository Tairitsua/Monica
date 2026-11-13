using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Monica.Framework.Generators.ChangeDataGenerator;

/// <summary>
/// Change-item data source generator.
/// Automatically generates the change-item data class and Apply method for each tracked entity.
/// </summary>
[Generator]
public class ChangeDataGenerator : IIncrementalGenerator
{
    private const string GenerateChangeItemDataAttributeName = "Monica.Framework.ChangeTracking.Annotations.GenerateChangeDataAttribute";
    private const string ChangeTrackedEntityInterfaceName = "Monica.Framework.ChangeTracking.Abstractions.IChangeTrackedEntity";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        //Debugger.Launch();
        // No need to generate properties files anymore, use interface detection

        var candidateClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => ctx.Node as ClassDeclarationSyntax)
            .Where(static classSyntax => classSyntax is not null)
            .Select(static (classSyntax, _) => classSyntax!);

        // Find classes that implement the change-tracked entity interface and check these classes first.
        var tracingDataEntities = candidateClasses
            .Combine(context.CompilationProvider)
            .Select(static (pair, _) => GetOptimizedEntityInfo(pair.Right, pair.Left))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        var generationInputs = context.CompilationProvider.Combine(tracingDataEntities.Collect());

        // Generate code for all detected entities (with deduplication for partial classes)
        context.RegisterSourceOutput(generationInputs, (ctx, input) =>
        {
            var (compilation, entities) = input;

            // Deduplication: Use HashSet to ensure each entity is processed only once
            var uniqueEntities = new HashSet<EntityGenerationInfo>(entities);
            var analyzer = new EntityAnalyzer(compilation, ctx.CancellationToken);
            
            foreach (var entity in uniqueEntities)
            {
                try
                {
                    GenerateChangeItemDataForEntity(ctx, entity, analyzer);
                }
                catch (Exception ex)
                {
                    // Generate diagnostic information instead of throwing exceptions
                    var diagnostic = Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "MOGEN001", 
                            "ChangeItemData generation failed", 
                            $"Failed to generate ChangeItemData for {entity.EntitySymbol.Name}: {ex.Message}", 
                            "Monica.Generators", 
                            DiagnosticSeverity.Warning, 
                            isEnabledByDefault: true),
                        Location.None);
                    
                    ctx.ReportDiagnostic(diagnostic);
                }
            }
        });
    }

    /// <summary>
    /// Optimized entity information acquisition method: first check the change-tracked entity interface, and then check the generation attribute.
    /// </summary>
    private static EntityGenerationInfo? GetOptimizedEntityInfo(
        Compilation compilation,
        ClassDeclarationSyntax classSyntax)
    {
        var semanticModel = compilation.GetSemanticModel(classSyntax.SyntaxTree);
        if (semanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol entitySymbol)
            return null;

        var changeTrackedEntitySymbol = compilation.GetTypeByMetadataName(ChangeTrackedEntityInterfaceName);
        if (changeTrackedEntitySymbol == null)
            return null;

        // First check whether the change-tracked entity interface is implemented.
        if (!ImplementsInterface(entitySymbol, changeTrackedEntitySymbol))
            return null;

        var generateChangeItemDataAttributeSymbol = compilation.GetTypeByMetadataName(GenerateChangeItemDataAttributeName);

        // Then check if there is a generation attribute and, if so, use its settings.
        var generateAttribute = generateChangeItemDataAttributeSymbol == null
            ? null
            : entitySymbol.GetAttributes()
                .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(
                    a.AttributeClass,
                    generateChangeItemDataAttributeSymbol));

        if (generateAttribute != null)
        {
            // If there are attributes, get the attribute parameters
            var customNamespace = GetAttributeArgumentValue<string>(generateAttribute, "Namespace");
            var customClassName = GetAttributeArgumentValue<string>(generateAttribute, "ClassName");
            var includeDebugInfo = GetAttributeArgumentValue<bool>(generateAttribute, "IncludeDebugInfo");

            return new EntityGenerationInfo(
                entitySymbol,
                classSyntax,
                customNamespace,
                customClassName,
                includeDebugInfo
            );
        }

        // If there are no attributes, use the default settings
        return new EntityGenerationInfo(
            entitySymbol,
            classSyntax,
            null, // 默认命名空间
            null, // 默认类名
            false // 默认不包含调试信息
        );
    }


    /// <summary>
    /// Generate change-item data code for entities.
    /// </summary>
    private static void GenerateChangeItemDataForEntity(
        SourceProductionContext context,
        EntityGenerationInfo entityInfo,
        EntityAnalyzer analyzer)
    {
        var entitySymbol = entityInfo.EntitySymbol;
        var analysisResult = analyzer.AnalyzeEntity(entitySymbol);
        
        if (analysisResult == null)
        {
            // Report analysis failed
            var diagnostic = Diagnostic.Create(
                new DiagnosticDescriptor(
                    "MOGEN002", 
                    "Entity analysis failed", 
                    $"Failed to analyze entity {entitySymbol.Name}", 
                    "Monica.Generators", 
                    DiagnosticSeverity.Warning, 
                    isEnabledByDefault: true),
                Location.None);
            
            context.ReportDiagnostic(diagnostic);
            return;
        }

        // flat attribute
        var flattener = new PropertyFlattener();
        var flattenedResult = flattener.FlattenProperties(analysisResult);

        // Generate code
        var codeBuilder = new CodeBuilder();
        var generatedCode = codeBuilder.BuildChangeItemDataClass(
            analysisResult,
            flattenedResult,
            entityInfo.CustomNamespace,
            entityInfo.CustomClassName,
            entityInfo.IncludeDebugInfo);

        // Add generated source files
        var fileName = $"{entityInfo.CustomClassName ?? $"{entitySymbol.Name}ChangeDataGen"}.g.cs";
        context.AddSource(fileName, generatedCode);
    }

    /// <summary>
    /// Check whether the type implements the specified interface.
    /// </summary>
    private static bool ImplementsInterface(INamedTypeSymbol type, INamedTypeSymbol interfaceSymbol)
    {
        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceSymbol));
    }


    /// <summary>
    /// Get attribute parameter value
    /// </summary>
    private static T GetAttributeArgumentValue<T>(AttributeData? attribute, string parameterName)
    {
        if (attribute == null)
            return default(T)!;

        // Find named parameters
        var namedArg = attribute.NamedArguments
            .FirstOrDefault(kvp => kvp.Key == parameterName);

        if (namedArg.Value.IsNull || namedArg.Value.Value == null)
            return default(T)!;

        return (T)namedArg.Value.Value;
    }

}

/// <summary>
/// Entity generation information
/// </summary>
internal class EntityGenerationInfo(
    INamedTypeSymbol entitySymbol,
    ClassDeclarationSyntax classSyntax,
    string? customNamespace = null,
    string? customClassName = null,
    bool includeDebugInfo = false)
    : IEquatable<EntityGenerationInfo>
{
    public INamedTypeSymbol EntitySymbol { get; } = entitySymbol;
    public ClassDeclarationSyntax ClassSyntax { get; } = classSyntax;
    public string? CustomNamespace { get; } = customNamespace;
    public string? CustomClassName { get; } = customClassName;
    public bool IncludeDebugInfo { get; } = includeDebugInfo;

    public bool Equals(EntityGenerationInfo? other)
    {
        if (other == null) return false;
        return SymbolEqualityComparer.Default.Equals(EntitySymbol, other.EntitySymbol);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as EntityGenerationInfo);
    }

    public override int GetHashCode()
    {
        return SymbolEqualityComparer.Default.GetHashCode(EntitySymbol);
    }
}
