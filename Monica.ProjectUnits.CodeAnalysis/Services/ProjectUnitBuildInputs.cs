using System.Runtime.CompilerServices;
using Microsoft.Build.Evaluation;

namespace Monica.ProjectUnits.CodeAnalysis.Services;

/// <summary>Captures SDK-evaluated imports only while constructing a source observation.</summary>
internal static class ProjectUnitBuildInputs
{
    // Keep this explicit context shared with MSBuildWorkspace. These are the .NET design-time
    // defaults in Roslyn 5.0 ProjectBuildManager; they are not inferred from host build settings.
    public static Dictionary<string, string> CreateGlobalProperties(string? solutionPath = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "True",
            ["NonExistentFile"] = "__NonExistentSubDir__\\__NonExistentFile__",
            ["BuildProjectReferences"] = "False",
            ["BuildingProject"] = "False",
            ["ProvideCommandLineArgs"] = "True",
            ["SkipCompilerExecution"] = "True",
            ["ContinueOnError"] = "ErrorAndContinue",
            ["ShouldUnsetParentConfigurationAndPlatform"] = "False"
        };
        if (solutionPath is not null)
        {
            // Roslyn's solution loader adds SolutionDir, including its trailing separator.
            // Input evaluation and individual-project recovery must use the same context.
            properties["SolutionDir"] = Path.GetDirectoryName(solutionPath) + Path.DirectorySeparatorChar;
        }
        return properties;
    }

    // MSBuildLocator must register before the JIT resolves Microsoft.Build types in this method.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static EvaluatedProjectInputs Evaluate(string projectPath, string? solutionPath = null)
    {
        using var collection = new ProjectCollection(CreateGlobalProperties(solutionPath));
        var project = new Project(projectPath, null, null, collection,
            ProjectLoadSettings.RejectCircularImports | ProjectLoadSettings.IgnoreEmptyImports
            | ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition | ProjectLoadSettings.FailOnUnresolvedSdk);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(projectPath) };
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contexts = new List<IReadOnlyDictionary<string, string>>();
        Capture();
        // Roslyn loads each TargetFramework of a multi-target project. The union is conservative
        // even when the consumer selects only one Roslyn project instance from that file.
        if (string.IsNullOrWhiteSpace(project.GetPropertyValue("TargetFramework")))
        {
            foreach (var target in project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                project.SetGlobalProperty("TargetFramework", target);
                project.ReevaluateIfNecessary();
                Capture();
            }
        }
        return new(paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            references.Order(StringComparer.OrdinalIgnoreCase).ToArray(), contexts);

        void Capture()
        {
            foreach (var import in project.Imports) paths.Add(Path.GetFullPath(import.ImportedProject.FullPath));
            foreach (var item in project.GetItems("ProjectReference"))
                references.Add(Path.GetFullPath(item.EvaluatedInclude, project.DirectoryPath));
            foreach (var itemType in new[] { "Compile", "AdditionalFiles", "Analyzer", "EditorConfigFiles" })
                foreach (var item in project.GetItems(itemType)) paths.Add(Path.GetFullPath(item.EvaluatedInclude, project.DirectoryPath));
            var context = new Dictionary<string, string>(project.GlobalProperties, StringComparer.OrdinalIgnoreCase);
            foreach (var property in new[] { "Configuration", "Platform", "TargetFramework", "TargetFrameworks", "RuntimeIdentifier", "MSBuildToolsPath", "MSBuildSDKsPath" })
                context[property] = project.GetPropertyValue(property);
            contexts.Add(context);
        }
    }
}

internal sealed record EvaluatedProjectInputs(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Contexts);
