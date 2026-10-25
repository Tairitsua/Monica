using System.Globalization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.Core.Localization.Abstractions;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Xunit;

namespace Test.Monica.UI.Shell.Support;

public sealed class PageRegistryTests
{
    [Fact]
    public void RegisterLocalizedPage_WhenModuleOwnsResources_ShouldResolveThroughCurrentHostCatalog()
    {
        var registry = new PageRegistry();
        var categoryId = registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "Tairitsua.Monica.GachaPool",
            "Navigation:Category",
            order: 450,
            descriptionKey: "Navigation:CategoryDescription");
        registry.RegisterLocalizedPage<TestPage, ThirdPartyResource>(
            "/gacha-pool",
            "Navigation:Title",
            categoryId: categoryId,
            addToNav: true,
            descriptionKey: "Navigation:PageDescription");
        registry.Seal();

        var page = registry.GetRegisteredPages().Single();
        var navigationItem = registry.GetNavItems().Single();
        var category = registry.GetNavigationCategories().Single(item => item.Id == categoryId);
        var firstHostCatalog = new HostLocalizationCatalog("first-host");
        var secondHostCatalog = new HostLocalizationCatalog("second-host");

        page.DisplayName.ResourceType.Should().Be(typeof(ThirdPartyResource));
        page.ResolveDisplayName(firstHostCatalog)
            .Should().Be("first-host:ThirdPartyResource:Navigation:Title");
        category.ResolveDisplayName(firstHostCatalog)
            .Should().Be("first-host:ThirdPartyResource:Navigation:Category");
        category.ResolveDescription(firstHostCatalog)
            .Should().Be("first-host:ThirdPartyResource:Navigation:CategoryDescription");
        category.ResolveDescription(secondHostCatalog)
            .Should().Be("second-host:ThirdPartyResource:Navigation:CategoryDescription");
        page.Description!.ResourceType.Should().Be(typeof(ThirdPartyResource));
        page.ResolveDescription(firstHostCatalog)
            .Should().Be("first-host:ThirdPartyResource:Navigation:PageDescription");
        navigationItem.Page.ResolveDescription(secondHostCatalog)
            .Should().Be("second-host:ThirdPartyResource:Navigation:PageDescription");
        navigationItem.CategoryId.Should().Be(categoryId);
        navigationItem.ResolveText(firstHostCatalog)
            .Should().Be("first-host:ThirdPartyResource:Navigation:Title");
        navigationItem.ResolveText(secondHostCatalog)
            .Should().Be("second-host:ThirdPartyResource:Navigation:Title");
        firstHostCatalog.RequestedResourceTypes.Should().OnlyContain(type => type == typeof(ThirdPartyResource));
        secondHostCatalog.RequestedResourceTypes.Should().OnlyContain(type => type == typeof(ThirdPartyResource));
    }

    [Fact]
    public void RegisterPage_WhenDescriptionsAreLiteral_ShouldResolveWithoutRequestingResources()
    {
        var registry = new PageRegistry();
        var categoryId = registry.RegisterCategory(
            "package.diagnostics", "Diagnostics", description: "Inspect package runtime behavior.");
        registry.RegisterPage<TestPage>(
            "package/runtime", "Runtime", categoryId: categoryId, addToNav: true,
            description: "Trace operations and failures.");
        registry.Seal();
        var catalog = new HostLocalizationCatalog("host");

        var group = NavigationTreeBuilder.Build(registry.GetNavigationCategories(), registry.GetNavItems(), catalog)
            .Should().ContainSingle().Subject;

        group.Description.Should().Be("Inspect package runtime behavior.");
        group.Items.Single().Page.ResolveDescription(catalog).Should().Be("Trace operations and failures.");
        catalog.RequestedResourceTypes.Should().BeEmpty();
    }

    [Fact]
    public void RegisterPage_WhenDescriptionsAreOmitted_ShouldPublishNoDescription()
    {
        var registry = new PageRegistry();
        var categoryId = registry.RegisterCategory("package.extra", "Extra");
        registry.RegisterPage<TestPage>("package/extra", "Extra", categoryId: categoryId, addToNav: true);
        registry.Seal();
        var catalog = new HostLocalizationCatalog("host");

        var group = NavigationTreeBuilder.Build(registry.GetNavigationCategories(), registry.GetNavItems(), catalog)
            .Should().ContainSingle().Subject;

        group.Description.Should().BeNull();
        group.Items.Single().Page.Description.Should().BeNull();
        group.Items.Single().Page.ResolveDescription(catalog).Should().BeNull();
        registry.GetNavigationCategories().Single(category => category.Id == categoryId)
            .ResolveDescription(catalog).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Register_WhenOptionalDescriptionIsBlank_ShouldRejectStartupContribution(string blank)
    {
        var registry = new PageRegistry();
        var literalCategory = () => registry.RegisterCategory("package.literal", "Literal", description: blank);
        var localizedCategory = () => registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "package.localized", "Navigation:Category", descriptionKey: blank);
        var literalPage = () => registry.RegisterPage<TestPage>("literal", "Literal", description: blank);
        var localizedPage = () => registry.RegisterLocalizedPage<TestPage, ThirdPartyResource>(
            "localized", "Navigation:Title", descriptionKey: blank);

        literalCategory.Should().Throw<ArgumentException>().WithParameterName("description");
        localizedCategory.Should().Throw<ArgumentException>().WithParameterName("descriptionKey");
        literalPage.Should().Throw<ArgumentException>().WithParameterName("description");
        localizedPage.Should().Throw<ArgumentException>().WithParameterName("descriptionKey");
    }

    [Fact]
    public void RegisterLocalizedPage_WithAccessPolicy_ShouldPublishOnePolicyIdentityForPageAndNavigation()
    {
        var registry = new PageRegistry();
        registry.RegisterLocalizedPage<TestPage, ThirdPartyResource>(
            "protected",
            "Navigation:Title",
            addToNav: true,
            accessPolicyType: typeof(TestAccessPolicy));
        registry.Seal();

        var page = registry.GetRegisteredPages().Single();
        var navigation = registry.GetNavItems().Single();

        page.AccessPolicyType.Should().Be(typeof(TestAccessPolicy));
        navigation.Page.Should().BeSameAs(page);
    }

    [Fact]
    public void RegisterPage_WhenAccessPolicyTypeIsInvalid_ShouldRejectStartupRegistration()
    {
        var registry = new PageRegistry();

        var act = () => registry.RegisterPage<TestPage>(
            "invalid-policy",
            "Invalid policy",
            accessPolicyType: typeof(TestPage));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("accessPolicyType");
    }

    [Fact]
    public void RegisterLocalizedCategory_WhenDefinitionMatches_ShouldBeIdempotentAcrossCasing()
    {
        var registry = new PageRegistry();

        var first = registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "Tairitsua.Monica.GachaPool",
            "Navigation:Category",
            order: 450);
        var second = registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "tairitsua.monica.gachapool",
            "Navigation:Category",
            order: 450);
        registry.Seal();

        second.Should().Be(first);
        registry.GetNavigationCategories().Count(item => item.Id == first).Should().Be(1);
    }

    [Fact]
    public void RegisterLocalizedCategory_WhenDefinitionConflicts_ShouldRejectCollision()
    {
        var registry = new PageRegistry();
        registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "Tairitsua.Monica.GachaPool",
            "Navigation:Category",
            order: 450);

        var act = () => registry.RegisterLocalizedCategory<SecondResource>(
            "tairitsua.monica.gachapool",
            "Navigation:DifferentCategory",
            order: 451);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*tairitsua.monica.gachapool*different label, resource, order, parent, icon, or description*");
    }

    [Fact]
    public void RegisterCategory_WhenDescriptionChanges_ShouldRejectConflictingMetadata()
    {
        var registry = new PageRegistry();
        var categoryId = registry.RegisterCategory("package.literal", "Literal", description: "First purpose.");
        registry.RegisterCategory("PACKAGE.LITERAL", "Literal", description: "First purpose.").Should().Be(categoryId);
        registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "package.localized", "Navigation:Category", descriptionKey: "Navigation:FirstDescription");

        var literalConflict = () => registry.RegisterCategory(
            "package.literal", "Literal", description: "Second purpose.");
        var localizedConflict = () => registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "package.localized", "Navigation:Category", descriptionKey: "Navigation:SecondDescription");

        literalConflict.Should().Throw<InvalidOperationException>().WithMessage("*description*");
        localizedConflict.Should().Throw<InvalidOperationException>().WithMessage("*description*");
    }

    [Fact]
    public void GetNavItems_WhenPageReferencesUnknownCategory_ShouldFailBeforeRouting()
    {
        var registry = new PageRegistry();
        registry.RegisterLocalizedPage<TestPage, ThirdPartyResource>(
            "gacha-pool",
            "Navigation:Title",
            categoryId: NavigationCategoryId.Create("Tairitsua.Monica.GachaPool"),
            addToNav: true);

        var act = registry.Seal;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unregistered categories*tairitsua.monica.gachapool*");
    }

    [Fact]
    public void RegisterCategory_WhenParentIsRegisteredLater_ShouldPublishParentAndIcon()
    {
        var registry = new PageRegistry();
        var parentId = NavigationCategoryId.Create("package.parent");
        var childId = registry.RegisterCategory("package.child", "Child", parentId: parentId, icon: "child-icon");
        registry.RegisterCategory(parentId.Value, "Parent");
        registry.Seal();

        var child = registry.GetNavigationCategories().Single(category => category.Id == childId);
        child.ParentId.Should().Be(parentId);
        child.Icon.Should().Be("child-icon");
    }

    [Fact]
    public void Seal_WhenCategoryReferencesMissingParent_ShouldRejectHierarchy()
    {
        var registry = new PageRegistry();
        registry.RegisterCategory("package.child", "Child", parentId: NavigationCategoryId.Create("package.missing"));

        var act = registry.Seal;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unregistered parents*package.missing*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Seal_WhenCategoryHierarchyContainsCycle_ShouldRejectHierarchy(bool selfReference)
    {
        var registry = new PageRegistry();
        var firstId = NavigationCategoryId.Create("package.first");
        var secondId = NavigationCategoryId.Create("package.second");
        registry.RegisterCategory(firstId.Value, "First", parentId: selfReference ? firstId : secondId);
        if (!selfReference)
        {
            registry.RegisterCategory(secondId.Value, "Second", parentId: firstId);
        }

        var act = registry.Seal;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*hierarchy contains a cycle*package.first*");
    }

    [Fact]
    public void RegisterCategory_WhenParentOrIconConflicts_ShouldRejectCollision()
    {
        var registry = new PageRegistry();
        registry.RegisterCategory("package.child", "Child", icon: "first-icon");

        var changedIcon = () => registry.RegisterCategory("package.child", "Child", icon: "second-icon");
        var changedParent = () => registry.RegisterCategory(
            "package.child", "Child", parentId: BuiltInNavigationCategoryIds.Module, icon: "first-icon");

        changedIcon.Should().Throw<InvalidOperationException>();
        changedParent.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Build_WhenOnlyNestedDestinationIsAuthorized_ShouldRetainAncestorsAndPruneEmptyBranches()
    {
        var registry = new PageRegistry();
        var rootId = registry.RegisterCategory(
            "package.root", "Root", order: 20, icon: "root-icon", description: "Explore package capabilities.");
        var branchId = registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "package.branch", "Navigation:Category", parentId: rootId,
            descriptionKey: "Navigation:CategoryDescription");
        var leafCategoryId = registry.RegisterCategory("package.leaf", "Leaf", parentId: branchId);
        var deniedCategoryId = registry.RegisterLocalizedCategory<SecondResource>(
            "package.denied", "Navigation:Denied", parentId: rootId,
            descriptionKey: "Navigation:DeniedDescription");
        registry.RegisterPage<TestPage>("permitted/zeta", "Zeta", categoryId: leafCategoryId, addToNav: true, navOrder: 20);
        registry.RegisterPage<TestPage>("permitted/alpha", "Alpha", categoryId: leafCategoryId, addToNav: true, navOrder: 10);
        registry.RegisterPage<TestPage>("denied", "Denied", categoryId: deniedCategoryId, addToNav: true);
        registry.Seal();
        var catalog = new HostLocalizationCatalog("host");
        var authorizedItems = registry.GetNavItems().Where(item => item.Href.StartsWith("permitted/", StringComparison.Ordinal)).ToArray();

        var tree = NavigationTreeBuilder.Build(registry.GetNavigationCategories(), authorizedItems, catalog);

        var root = tree.Should().ContainSingle().Subject;
        root.Id.Should().Be(rootId);
        root.Icon.Should().Be("root-icon");
        root.Description.Should().Be("Explore package capabilities.");
        root.Items.Should().BeEmpty();
        var branch = root.Children.Should().ContainSingle().Subject;
        branch.Id.Should().Be(branchId);
        branch.DisplayName.Should().Be("host:ThirdPartyResource:Navigation:Category");
        branch.Description.Should().Be("host:ThirdPartyResource:Navigation:CategoryDescription");
        var leaf = branch.Children.Should().ContainSingle().Subject;
        leaf.Id.Should().Be(leafCategoryId);
        NavigationTreeBuilder.Flatten(tree).Select(static item => item.Href)
            .Should().Equal("permitted/alpha", "permitted/zeta");
        catalog.RequestedResourceTypes.Should().NotContain(typeof(SecondResource));
    }

    [Fact]
    public void RegisterPage_WhenRegistryIsSealed_ShouldRejectLateMutation()
    {
        var registry = new PageRegistry();
        registry.RegisterPage<TestPage>("first", "First page");
        registry.Seal();

        var act = () => registry.RegisterPage<SecondTestPage>("second", "Second page");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*sealed*application startup*");
    }

    [Fact]
    public void GetNavigationCategories_ShouldUseExplicitCultureIndependentOrder()
    {
        var registry = new PageRegistry();
        registry.RegisterLocalizedCategory<ThirdPartyResource>(
            "Tairitsua.Monica.GachaPool",
            "Navigation:Category",
            order: 450);
        registry.Seal();

        var ids = registry.GetNavigationCategories()
            .Where(static category => category.ParentId is null)
            .Select(static category => category.Id)
            .ToArray();

        ids.Should().ContainInOrder(
            BuiltInNavigationCategoryIds.Intelligence,
            BuiltInNavigationCategoryIds.Platform,
            BuiltInNavigationCategoryIds.Monitor,
            BuiltInNavigationCategoryIds.Infrastructure,
            NavigationCategoryId.Create("Tairitsua.Monica.GachaPool"),
            BuiltInNavigationCategoryIds.Documentation,
            BuiltInNavigationCategoryIds.Debug,
            BuiltInNavigationCategoryIds.Uncategorized);
    }

    [Fact]
    public void BuiltInCategories_ShouldNestCapabilitiesUnderStableRootIdentities()
    {
        var registry = new PageRegistry();
        registry.Seal();
        var categories = registry.GetNavigationCategories().ToDictionary(static category => category.Id);

        categories[BuiltInNavigationCategoryIds.AI].ParentId.Should().Be(BuiltInNavigationCategoryIds.Intelligence);
        categories[BuiltInNavigationCategoryIds.KnowledgeRetrieval].ParentId.Should().Be(BuiltInNavigationCategoryIds.Intelligence);
        categories[BuiltInNavigationCategoryIds.Module].ParentId.Should().Be(BuiltInNavigationCategoryIds.Platform);
        categories[BuiltInNavigationCategoryIds.Configuration].ParentId.Should().Be(BuiltInNavigationCategoryIds.Platform);
        categories[BuiltInNavigationCategoryIds.TaskScheduling].ParentId.Should().Be(BuiltInNavigationCategoryIds.Monitor);
        categories[BuiltInNavigationCategoryIds.Module].Order.Should()
            .BeLessThan(categories[BuiltInNavigationCategoryIds.Configuration].Order);
    }

    [Fact]
    public void ResolveText_WhenResourceDoesNotContainKey_ShouldUseRegisteredFallback()
    {
        var registry = new PageRegistry();
        registry.RegisterLocalizedPage<TestPage, ThirdPartyResource>(
            "gacha-pool",
            "Navigation:Title",
            addToNav: true,
            descriptionKey: "Navigation:PageDescription");
        registry.Seal();
        var catalog = new HostLocalizationCatalog("host", resourceNotFound: true);
        var navigationItem = registry.GetNavItems().Single();

        navigationItem.ResolveText(catalog).Should().Be("Navigation:Title");
        navigationItem.Page.ResolveDescription(catalog).Should().Be("Navigation:PageDescription");
        navigationItem.CategoryId.Should().Be(BuiltInNavigationCategoryIds.Uncategorized);
    }

    [Fact]
    public void RegisterPage_WhenAnotherComponentOwnsTheRoute_ShouldRejectCollision()
    {
        var registry = new PageRegistry();
        registry.RegisterPage<TestPage>("/shared", "First page");

        var act = () => registry.RegisterPage<SecondTestPage>("SHARED", "Second page");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*/SHARED*TestPage*SecondTestPage*");
    }

    [Fact]
    public void ContributorContract_ShouldBeWriteOnlyAndCannotSealComposition()
    {
        var registry = new PageRegistry();
        INavigationRegistryBuilder contributor = registry;
        IPageCatalog catalog = registry;

        var readBeforeSeal = catalog.GetNavItems;

        readBeforeSeal.Should().Throw<InvalidOperationException>()
            .WithMessage("*not available*startup registration*");
        contributor.RegisterPage<TestPage>("first", "First page");
        registry.Seal();
        catalog.GetRegisteredPages().Should().ContainSingle();
        typeof(INavigationRegistryBuilder).GetMethods()
            .Should().OnlyContain(method => method.Name.StartsWith("Register", StringComparison.Ordinal));
    }

    [Fact]
    public void GetNavItems_WhenOrdersMatch_ShouldUseRouteAsDeterministicTieBreaker()
    {
        var registry = new PageRegistry();
        registry.RegisterPage<SecondTestPage>("zeta", "Zeta", addToNav: true, navOrder: 30);
        registry.RegisterPage<TestPage>("alpha", "Alpha", addToNav: true, navOrder: 30);
        registry.Seal();

        registry.GetNavItems().Select(static item => item.Href)
            .Should().ContainInOrder("alpha", "zeta");
    }

    [Fact]
    public void NavigationCategoryId_Create_ShouldCanonicalizeCasingAndHaveNoInvalidDefault()
    {
        var id = NavigationCategoryId.Create("Tairitsua.Monica.GachaPool");

        id.Value.Should().Be("tairitsua.monica.gachapool");
        id.Should().Be(NavigationCategoryId.Create("TAIRITSUA.MONICA.GACHAPOOL"));
        typeof(NavigationCategoryId).IsValueType.Should().BeFalse();
    }

    [Fact]
    public void CatalogModels_ShouldExposeImmutableState()
    {
        typeof(PageDefinition).GetProperties()
            .Should().OnlyContain(property => property.SetMethod == null);
        typeof(NavigationItem).GetProperties()
            .Should().OnlyContain(property => property.SetMethod == null);
    }

    private sealed class TestPage : ComponentBase;

    private sealed class SecondTestPage : ComponentBase;

    private sealed class ThirdPartyResource : ILocalizationResource;

    private sealed class SecondResource : ILocalizationResource;

    private sealed class TestAccessPolicy : IPageAccessPolicy
    {
        public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class HostLocalizationCatalog(string hostName, bool resourceNotFound = false)
        : ILocalizationCatalog
    {
        public List<Type> RequestedResourceTypes { get; } = [];

        public IStringLocalizer For<TResource>() where TResource : class, ILocalizationResource
        {
            return For(typeof(TResource));
        }

        public IStringLocalizer For(Type resourceType)
        {
            RequestedResourceTypes.Add(resourceType);
            return new HostStringLocalizer(hostName, resourceType, resourceNotFound);
        }

        public string Get<TResource>(string key) where TResource : class, ILocalizationResource
        {
            return For<TResource>()[key].Value;
        }

        public string Get<TResource>(string key, params object[] arguments)
            where TResource : class, ILocalizationResource
        {
            return For<TResource>()[key, arguments].Value;
        }
    }

    private sealed class HostStringLocalizer(string hostName, Type resourceType, bool resourceNotFound)
        : IStringLocalizer
    {
        public LocalizedString this[string name] => new(
            name,
            $"{hostName}:{resourceType.Name}:{name}",
            resourceNotFound);

        public LocalizedString this[string name, params object[] arguments] => new(
            name,
            $"{hostName}:{resourceType.Name}:{string.Format(CultureInfo.InvariantCulture, name, arguments)}",
            resourceNotFound);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        {
            return [];
        }

        public IStringLocalizer WithCulture(CultureInfo culture)
        {
            return this;
        }
    }
}
