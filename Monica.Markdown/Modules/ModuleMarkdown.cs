using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Markdown.Abstractions;
using Monica.Markdown.Facades;
using Monica.Markdown.Models;
using Monica.Markdown.Providers.FileSystem;
using Monica.Markdown.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleMarkdownBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Markdown document management module.
        /// </summary>
        public ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> AddMarkdown(
            Action<ModuleMarkdownOption>? action = null)
        {
            return builder.AddModule<ModuleMarkdown, ModuleMarkdownOption>(action);
        }
    }
}

public class ModuleMarkdown : MonicaModule<ModuleMarkdownOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocalization>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleMarkdownOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<IMarkdownDocumentTitleResolver,
            FileNameMarkdownDocumentTitleResolver>();
        services.TryAddSingleton<IMarkdownDocumentProvider,
            FileSystemMarkdownDocumentProvider>();
        services.TryAddSingleton<IMarkdownDocumentSearcher, MarkdownDocumentSearchService>();
        services.AddSingleton<IMarkdownDocumentCatalog, MarkdownDocumentCatalogService>();
        services.AddSingleton<MarkdownFacade>();
    }
}

public static class ModuleMarkdownRegistrationExtensions
{
    /// <summary>
    /// Registers a markdown document group for scanning.
    /// </summary>
    /// <param name="module">The Markdown registration being configured.</param>
    /// <param name="key">Unique identifier for this group.</param>
    /// <param name="title">Display title for this group.</param>
    /// <param name="basePath">Folder path containing markdown files.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="excludedFolders">Additional folders to exclude for this group only.</param>
    public static ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> AddDocumentGroup(this ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> module,
        string key,
        string title,
        string basePath,
        string? description = null,
        string[]? excludedFolders = null)
    {
        module.Configure(options =>
        {
            options.DocumentGroupRegistrations.Add(new MarkdownDocumentGroupRegistration
            {
                Key = key,
                Title = title,
                BasePath = basePath,
                Description = description,
                ExcludedFolders = excludedFolders
            });
        });

        return module;
    }

    /// <summary>
    /// Configures global folder exclusions that apply to all document groups.
    /// </summary>
    /// <param name="module">The Markdown registration being configured.</param>
    /// <param name="folderNames">Array of folder names to exclude (case-insensitive).</param>
    public static ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> WithExcludedFolders(this ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> module, params string[] folderNames)
    {
        module.Configure(options =>
        {
            options.ExcludedFolders = folderNames;
        });

        return module;
    }

    /// <summary>
    /// Adds additional folders to the global exclusion list without replacing defaults.
    /// </summary>
    /// <param name="module">The Markdown registration being configured.</param>
    /// <param name="folderNames">Additional folder names to exclude.</param>
    public static ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> AddExcludedFolders(this ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> module, params string[] folderNames)
    {
        module.Configure(options =>
        {
            var existing = options.ExcludedFolders ?? Array.Empty<string>();
            options.ExcludedFolders = existing.Concat(folderNames).Distinct(
                StringComparer.OrdinalIgnoreCase).ToArray();
        });

        return module;
    }

    /// <summary>
    /// Enables multilingual markdown document discovery driven by the
    /// configured localization cultures. When enabled, top-level folders whose
    /// names match supported culture keys such as <c>zh-CN</c> or <c>en-US</c>
    /// become language roots for the built-in viewer.
    /// </summary>
    public static ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> EnableMultilingualDocuments(this ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> module)
    {
        module.Configure(options =>
        {
            options.EnableMultilingualDocuments = true;
        });

        return module;
    }

    /// <summary>
    /// Clears all global folder exclusions (including defaults).
    /// </summary>
    public static ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> ClearExcludedFolders(this ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> module)
    {
        module.Configure(options =>
        {
            options.ExcludedFolders = Array.Empty<string>();
        });

        return module;
    }

    /// <summary>
    /// Replaces the default document title provider with a custom implementation.
    /// </summary>
    /// <typeparam name="TProvider">Custom title provider type.</typeparam>
    public static ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> UseDocumentTitleProvider<TProvider>(this ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> module)
        where TProvider : class, IMarkdownDocumentTitleResolver
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.RemoveAll<IMarkdownDocumentTitleResolver>();
            ctx.Services.AddSingleton<IMarkdownDocumentTitleResolver, TProvider>();
        }, order: 0);

        return module;
    }

    /// <summary>
    /// Replaces the default document provider with a custom implementation.
    /// </summary>
    /// <typeparam name="TProvider">Custom document provider type.</typeparam>
    public static ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> UseDocumentProvider<TProvider>(this ModuleRegistration<ModuleMarkdown, ModuleMarkdownOption> module)
        where TProvider : class, IMarkdownDocumentProvider
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.RemoveAll<IMarkdownDocumentProvider>();
            ctx.Services.AddSingleton<IMarkdownDocumentProvider, TProvider>();
        }, order: 0);

        return module;
    }

}

public class ModuleMarkdownOption : ModuleOptions<ModuleMarkdown>
{
    /// <summary>
    /// Registered document group descriptors populated by module registration extensions.
    /// </summary>
    public List<MarkdownDocumentGroupRegistration> DocumentGroupRegistrations { get; set; } = [];

    /// <summary>
    /// File extensions recognized as markdown files.
    /// </summary>
    public string[] MarkdownFileExtensions { get; set; } = [".md", ".markdown"];

    /// <summary>
    /// Whether to parse YAML front matter from markdown files.
    /// This must remain enabled for document titles, sidebar labels,
    /// sidebar positions, tags, dates, and folder metadata files to take effect.
    /// </summary>
    public bool ParseFrontMatter { get; set; } = true;

    /// <summary>
    /// Markdown file names reserved for folder-level navigation metadata.
    /// Matching is case-insensitive. These files are excluded from the document
    /// list and are read only for front matter such as <c>title</c>,
    /// <c>sidebar_label</c>, <c>sidebar_position</c>, <c>name</c>, and
    /// <c>position</c>.
    /// </summary>
    public string[] FolderMetadataFileNames { get; set; } = ["_category_.md"];

    /// <summary>
    /// Selects the matching strategy used by markdown document search.
    /// This applies to both host integrations and the built-in UI dialog.
    /// </summary>
    public MarkdownSearchAlgorithm DocumentSearchAlgorithm { get; set; } =
        MarkdownSearchAlgorithm.KeywordFuzzy;

    /// <summary>
    /// Enables multilingual document discovery based on the configured
    /// localization culture keys. When this is enabled and the scanner detects
    /// top-level culture folders, the markdown viewer treats those folders as
    /// hidden language roots and expects every markdown file to live under a
    /// supported culture root.
    /// </summary>
    public bool EnableMultilingualDocuments { get; internal set; }

    /// <summary>
    /// Minimum normalized query length required before a search executes.
    /// Increase this to reduce low-signal broad matches.
    /// </summary>
    public int DocumentSearchMinQueryLength { get; set; } = 2;

    /// <summary>
    /// Maximum number of document results returned by each search request.
    /// Higher values improve recall but increase ranking and payload cost.
    /// </summary>
    public int DocumentSearchMaxResults { get; set; } = 50;

    /// <summary>
    /// Maximum number of preview characters returned for each search result.
    /// Longer previews provide more context but produce larger response payloads.
    /// </summary>
    public int DocumentSearchPreviewLength { get; set; } = 180;

    /// <summary>
    /// Global folder names to exclude from scanning across all document groups.
    /// Matching is case-insensitive and applies to directory names at any level.
    /// </summary>
    public string[] ExcludedFolders { get; set; } =
    [
        ".git",
        ".vs",
        ".vscode",
        ".idea",
        "node_modules",
        "bin",
        "obj",
        ".nuget",
        "packages",
        ".attachments",
        ".obsidian"
    ];

}
