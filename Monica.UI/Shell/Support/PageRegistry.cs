using System.Reflection;
using Microsoft.AspNetCore.Components;
using Monica.Core.Localization.Abstractions;
using Monica.UI.Icons;
using Monica.UI.Localization;
using Monica.UI.Shell.Components;
using Monica.UI.Shell.Models;

namespace Monica.UI.Shell.Support;

/// <summary>
/// Stores startup-only page, category, and navigation contributions for one Monica host.
/// </summary>
internal sealed class PageRegistry : INavigationRegistryBuilder, IPageCatalog
{
    private readonly HashSet<Assembly> _assemblies = [];
    private readonly Dictionary<NavigationCategoryId, NavigationCategoryDefinition> _categories = [];
    private readonly Dictionary<string, Type> _components = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Assembly> _excludedAssemblies = [typeof(AppRouter).Assembly];
    private readonly List<NavigationItem> _navItems = [];
    private readonly List<PageDefinition> _pages = [];
    private readonly object _sync = new();
    private Snapshot? _snapshot;

    internal void AddAssembly(Assembly assembly)
    {
        lock (_sync)
        {
            EnsureMutable();
            if (!_excludedAssemblies.Contains(assembly))
            {
                _assemblies.Add(assembly);
            }
        }
    }

    /// <summary>
    /// Initializes a registry with the shared category taxonomy owned by the Monica UI shell.
    /// </summary>
    public PageRegistry()
    {
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Intelligence, "NavBar:Categories:Intelligence", 100, MoIcons.Sparkle,
            "NavBar:CategoryDescriptions:Intelligence");
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Platform, "NavBar:Categories:Platform", 200, MoIcons.Cube,
            "NavBar:CategoryDescriptions:Platform");
        AddBuiltInCategory(BuiltInNavigationCategoryIds.AI, "NavBar:Categories:AI", 10, MoIcons.Sparkle,
            "NavBar:CategoryDescriptions:AI", BuiltInNavigationCategoryIds.Intelligence);
        AddBuiltInCategory(BuiltInNavigationCategoryIds.KnowledgeRetrieval, "NavBar:Categories:KnowledgeRetrieval", 20, MoIcons.BookSearch,
            "NavBar:CategoryDescriptions:KnowledgeRetrieval", BuiltInNavigationCategoryIds.Intelligence);
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Documentation, "NavBar:Categories:Documentation", 500, MoIcons.Book,
            "NavBar:CategoryDescriptions:Documentation");
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Configuration, "NavBar:Categories:Configuration", 20, MoIcons.Sliders,
            "NavBar:CategoryDescriptions:Configuration", BuiltInNavigationCategoryIds.Platform);
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Infrastructure, "NavBar:Categories:Infrastructure", 400, MoIcons.Server,
            "NavBar:CategoryDescriptions:Infrastructure");
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Module, "NavBar:Categories:Module", 10, MoIcons.Cube,
            "NavBar:CategoryDescriptions:Module", BuiltInNavigationCategoryIds.Platform);
        AddBuiltInCategory(BuiltInNavigationCategoryIds.TaskScheduling, "NavBar:Categories:TaskScheduling", 20, MoIcons.CalendarCheck,
            "NavBar:CategoryDescriptions:TaskScheduling", BuiltInNavigationCategoryIds.Monitor);
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Monitor, "NavBar:Categories:Monitor", 300, MoIcons.Pulse,
            "NavBar:CategoryDescriptions:Monitor");
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Debug, "NavBar:Categories:Debug", 600, MoIcons.Bug,
            "NavBar:CategoryDescriptions:Debug");
        AddBuiltInCategory(BuiltInNavigationCategoryIds.Uncategorized, "NavBar:Categories:Uncategorized", 1_000, MoIcons.Folder,
            "NavBar:CategoryDescriptions:Uncategorized");
    }

    /// <inheritdoc />
    public NavigationCategoryId RegisterCategory(
        string categoryId,
        string displayName,
        int order = 0,
        NavigationCategoryId? parentId = null,
        string? icon = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (description is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
        }
        return RegisterCategoryCore(
            new NavigationCategoryDefinition(
                NavigationCategoryId.Create(categoryId),
                UIRegistryText.Literal(displayName),
                order,
                parentId,
                icon,
                description is null ? null : UIRegistryText.Literal(description)));
    }

    /// <inheritdoc />
    public NavigationCategoryId RegisterLocalizedCategory<TResource>(
        string categoryId,
        string displayNameKey,
        int order = 0,
        NavigationCategoryId? parentId = null,
        string? icon = null,
        string? descriptionKey = null)
        where TResource : class, ILocalizationResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNameKey);
        if (descriptionKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptionKey);
        }
        return RegisterCategoryCore(
            new NavigationCategoryDefinition(
                NavigationCategoryId.Create(categoryId),
                UIRegistryText.Localized<TResource>(displayNameKey),
                order,
                parentId,
                icon,
                descriptionKey is null ? null : UIRegistryText.Localized<TResource>(descriptionKey)));
    }

    /// <inheritdoc />
    public void RegisterPage<TPage>(
        string route,
        string displayName,
        string? icon = null,
        NavigationCategoryId? categoryId = null,
        bool addToNav = false,
        int navOrder = 0,
        Type? accessPolicyType = null,
        string? description = null)
        where TPage : ComponentBase
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (description is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
        }

        RegisterPageCore(
            new PageDefinition(
                NormalizeRoute(route),
                typeof(TPage),
                UIRegistryText.Literal(displayName),
                ValidateAccessPolicyType(accessPolicyType),
                description is null ? null : UIRegistryText.Literal(description)),
            icon,
            categoryId,
            addToNav,
            navOrder);
    }

    /// <inheritdoc />
    public void RegisterLocalizedPage<TPage, TResource>(
        string route,
        string displayNameKey,
        string? icon = null,
        NavigationCategoryId? categoryId = null,
        bool addToNav = false,
        int navOrder = 0,
        Type? accessPolicyType = null,
        string? descriptionKey = null)
        where TPage : ComponentBase
        where TResource : class, ILocalizationResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNameKey);
        if (descriptionKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptionKey);
        }

        RegisterPageCore(
            new PageDefinition(
                NormalizeRoute(route),
                typeof(TPage),
                UIRegistryText.Localized<TResource>(displayNameKey),
                ValidateAccessPolicyType(accessPolicyType),
                descriptionKey is null ? null : UIRegistryText.Localized<TResource>(descriptionKey)),
            icon,
            categoryId,
            addToNav,
            navOrder);
    }

    /// <inheritdoc />
    public IReadOnlyList<PageDefinition> GetRegisteredPages() => GetSnapshot().Pages;

    /// <inheritdoc />
    public IReadOnlyList<NavigationItem> GetNavItems() => GetSnapshot().NavigationItems;

    /// <inheritdoc />
    public IReadOnlyList<NavigationCategoryDefinition> GetNavigationCategories() => GetSnapshot().Categories;

    /// <inheritdoc />
    public Assembly[] GetAdditionalAssemblies()
    {
        return GetSnapshot().Assemblies.ToArray();
    }

    internal void Seal()
    {
        lock (_sync)
        {
            if (_snapshot is not null)
            {
                return;
            }

            var missingCategoryIds = _navItems
                .Select(static item => item.CategoryId)
                .Distinct()
                .Where(categoryId => !_categories.ContainsKey(categoryId))
                .OrderBy(static categoryId => categoryId.Value, StringComparer.Ordinal)
                .ToArray();

            if (missingCategoryIds.Length > 0)
            {
                throw new InvalidOperationException(
                    "Navigation entries reference unregistered categories: " +
                    string.Join(", ", missingCategoryIds.Select(static categoryId => $"'{categoryId}'")) + ".");
            }

            ValidateCategoryHierarchy();

            _snapshot = new Snapshot(
                _assemblies.OrderBy(static assembly => assembly.FullName, StringComparer.Ordinal).ToArray(),
                _pages
                    .OrderBy(static page => page.Route, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static page => page.ComponentType.FullName, StringComparer.Ordinal)
                    .ToList()
                    .AsReadOnly(),
                _navItems
                    .OrderBy(static item => item.Order)
                    .ThenBy(static item => item.Href, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly(),
                _categories.Values
                    .OrderBy(static category => category.Order)
                    .ThenBy(static category => category.Id.Value, StringComparer.Ordinal)
                    .ToList()
                    .AsReadOnly());
        }
    }

    private static string NormalizeRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        return route.Trim().Trim('/');
    }

    private static Type? ValidateAccessPolicyType(Type? accessPolicyType)
    {
        if (accessPolicyType is null)
        {
            return null;
        }

        if (!accessPolicyType.IsClass || accessPolicyType.IsAbstract ||
            !typeof(IPageAccessPolicy).IsAssignableFrom(accessPolicyType))
        {
            throw new ArgumentException(
                $"Page access policy type '{accessPolicyType.FullName}' must be a concrete {nameof(IPageAccessPolicy)} implementation.",
                nameof(accessPolicyType));
        }

        return accessPolicyType;
    }

    private void AddBuiltInCategory(
        NavigationCategoryId id,
        string displayNameKey,
        int order,
        string icon,
        string descriptionKey,
        NavigationCategoryId? parentId = null)
    {
        _categories.Add(
            id,
            new NavigationCategoryDefinition(
                id,
                UIRegistryText.Localized<SharedResource>(displayNameKey),
                order,
                parentId,
                icon: icon,
                description: UIRegistryText.Localized<SharedResource>(descriptionKey)));
    }

    private NavigationCategoryId RegisterCategoryCore(NavigationCategoryDefinition category)
    {
        lock (_sync)
        {
            EnsureMutable();

            if (!_categories.TryGetValue(category.Id, out var existing))
            {
                _categories.Add(category.Id, category);
                return category.Id;
            }

            if (existing.HasSameRegistrationAs(category))
            {
                return existing.Id;
            }

            throw new InvalidOperationException(
                $"Navigation category '{existing.Id}' is already registered with a different label, resource, order, parent, icon, or description.");
        }
    }

    private void ValidateCategoryHierarchy()
    {
        var missingParentIds = _categories.Values
            .Where(static category => category.ParentId is not null)
            .Select(static category => category.ParentId!)
            .Distinct()
            .Where(parentId => !_categories.ContainsKey(parentId))
            .OrderBy(static parentId => parentId.Value, StringComparer.Ordinal)
            .ToArray();

        if (missingParentIds.Length > 0)
        {
            throw new InvalidOperationException(
                "Navigation categories reference unregistered parents: " +
                string.Join(", ", missingParentIds.Select(static parentId => $"'{parentId}'")) + ".");
        }

        var completed = new HashSet<NavigationCategoryId>();
        foreach (var category in _categories.Values.OrderBy(static category => category.Id.Value, StringComparer.Ordinal))
        {
            var ancestry = new HashSet<NavigationCategoryId>();
            var current = category;
            while (!completed.Contains(current.Id))
            {
                if (!ancestry.Add(current.Id))
                {
                    throw new InvalidOperationException(
                        $"Navigation category hierarchy contains a cycle at '{current.Id}'.");
                }

                if (current.ParentId is null)
                {
                    break;
                }

                current = _categories[current.ParentId];
            }

            completed.UnionWith(ancestry);
        }
    }

    private void RegisterPageCore(
        PageDefinition page,
        string? icon,
        NavigationCategoryId? categoryId,
        bool addToNav,
        int navOrder)
    {
        lock (_sync)
        {
            EnsureMutable();

            if (_components.TryGetValue(page.Route, out var existingComponentType))
            {
                throw new InvalidOperationException(
                    $"UI route '/{page.Route}' is already registered by component " +
                    $"'{existingComponentType.FullName}' and cannot also map to '{page.ComponentType.FullName}'.");
            }

            _pages.Add(page);
            _components[page.Route] = page.ComponentType;

            if (addToNav)
            {
                _navItems.Add(new NavigationItem(
                    page,
                    icon,
                    categoryId ?? BuiltInNavigationCategoryIds.Uncategorized,
                    navOrder));
            }

            if (!_excludedAssemblies.Contains(page.ComponentType.Assembly))
            {
                _assemblies.Add(page.ComponentType.Assembly);
            }
        }
    }

    private Snapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot ?? throw new InvalidOperationException(
                "The page catalog is not available until the UI shell completes startup registration.");
        }
    }

    private void EnsureMutable()
    {
        if (_snapshot is not null)
        {
            throw new InvalidOperationException(
                "The page registry has already been sealed. Register pages and categories during application startup.");
        }
    }

    private sealed record Snapshot(
        IReadOnlyList<Assembly> Assemblies,
        IReadOnlyList<PageDefinition> Pages,
        IReadOnlyList<NavigationItem> NavigationItems,
        IReadOnlyList<NavigationCategoryDefinition> Categories);
}
