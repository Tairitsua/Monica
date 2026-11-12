using System.Globalization;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Annotations;
using Monica.ModelQuery.Exceptions;
using Monica.ModelQuery.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Test.Monica.ModelQuery.Abstractions;
using Xunit;

namespace Test.Monica.ModelQuery.Providers;

public sealed class ModelQueryContractTests(ModelQueryTestApplicationFactory factory)
    : IClassFixture<ModelQueryTestApplicationFactory>
{
    [Theory]
    [InlineData("AB", new[] { "ABtail", "headAB", "xABy" })]
    [InlineData("AB%", new[] { "ABtail" })]
    [InlineData("%AB", new[] { "headAB" })]
    [InlineData("%AB%", new[] { "ABtail", "headAB", "xABy" })]
    [InlineData("A_B", new[] { "xA1By" })]
    public async Task ApplySearch_WhenPatternContainsWildcards_ShouldPreserveLikeSemantics(string pattern, string[] expected)
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        SearchModel[] source = [new() { Name = "ABtail" }, new() { Name = "headAB" },
            new() { Name = "xABy" }, new() { Name = "xA1By" }];
        Assert.Equal(expected, query.ApplySearch(source, pattern, [nameof(SearchModel.Name)]).Select(item => item.Name));
    }

    [Fact]
    public async Task ApplySearch_WhenTextContainsFilterSyntax_ShouldTreatItAsData()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        const string text = "\" or Name = \"other";
        SearchModel[] source = [new() { Name = "prefix \" or Name = \"other suffix" }, new() { Name = "other" }];
        Assert.Same(source[0], Assert.Single(query.ApplySearch(source, text, [nameof(SearchModel.Name)])));
    }

    [Fact]
    public async Task ApplyFilter_WhenSelectorUsesExpLike_ShouldKeepItsExpressionSemantics()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        SearchModel[] source = [new() { Name = "alpha beta" }, new() { Name = "alpha" }, new() { Name = "beta" }];
        Assert.Same(source[0], Assert.Single(query.ApplyFilter(source, item => item.Name, EFieldConditions.ExpLike, "alpha&beta")));
        Assert.Same(source[0], Assert.Single(query.ApplyFilter(source, "Name explike \"alpha&beta\"")));
    }

    [Fact]
    public async Task ApplySearch_WhenFieldIsExcludedFromDefault_ShouldStillAllowExplicitSearch()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        SearchModel[] source = [new() { Name = "ordinary", Secret = "hidden match" }];
        Assert.Empty(query.ApplySearch(source, "hidden"));
        Assert.Single(query.ApplySearch(source, "hidden", [nameof(SearchModel.Secret)]));
        Assert.True(query.GetFields().Single(field => field.ReflectionName == nameof(SearchModel.Secret)).SearchInfo.IsIgnored);
    }

    [Fact]
    public async Task ApplySearch_WhenFieldsAreBlankOrUnsupported_ShouldRejectExplicitFieldsAndSkipUnsupportedDefaults()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<MixedSearchModel>>();
        MixedSearchModel[] source = [new() { Name = "target", Code = 'A', Score = 1.5f }];
        Assert.Single(query.ApplySearch(source, "target"));
        Assert.True(query.GetFields().Single(field => field.ReflectionName == nameof(MixedSearchModel.Code)).SearchInfo.IsNotSupported);
        Assert.True(query.GetFields().Single(field => field.ReflectionName == nameof(MixedSearchModel.Score)).SearchInfo.IsNotSupported);
        Assert.Throws<ModelQueryNormalizationException>(() => query.ApplySearch(source, "target", [" "]));
        var unsupported = Assert.Throws<ModelQueryNormalizationException>(() => query.ApplySearch(source, "1", [nameof(MixedSearchModel.Score)]));
        Assert.Contains(nameof(MixedSearchModel.Score), unsupported.DisplayMessage);
    }

    [Fact]
    public async Task ParseFilter_WhenReusedAcrossSourceKinds_ShouldExposeImmutableReferencedFields()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        var parsed = query.ParseFilter("Name = \"A\" && (Count >= \"2\" or Count = \"1\")");
        SearchModel[] source = [new() { Name = "A", Count = 2 }, new() { Name = "B", Count = 3 }];

        Assert.Equal(new[] { nameof(SearchModel.Name), nameof(SearchModel.Count) },
            parsed.ReferencedFields.Select(field => field.ReflectionName));
        Assert.Same(source[0], Assert.Single(query.ApplyFilter(source, parsed)));
        Assert.Same(source[0], Assert.Single(query.ApplyFilter(source.AsQueryable(), parsed)));
        Assert.True(query.CompileFilter(parsed)(source[0]));
        Assert.False(query.CompileFilter(parsed)(source[1]));
        Assert.Throws<NotSupportedException>(() => ((IList<QueryField>)parsed.ReferencedFields).Clear());
        Assert.Throws<ModelQueryNormalizationException>(() => query.ParseFilter("[Name == \"A\"]"));
        Assert.Throws<ModelQueryNormalizationException>(() => query.ParseFilter("Name = \"A\" or true"));
    }

    [Theory]
    [InlineData("AndAlso", 1)]
    [InlineData("OrElse", 3)]
    [InlineData("&", 1)]
    [InlineData("|", 3)]
    public async Task ApplyFilter_WhenLegacyBooleanOperatorsAreUsed_ShouldPreserveTheirMeaning(string operation, int expected)
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        SearchModel[] source = [new() { Name = "A", Count = 2 }, new() { Name = "B", Count = 2 },
            new() { Name = "A", Count = 1 }, new() { Name = "B", Count = 1 }];
        var filter = $"Name = \"A\" {operation} Count = \"2\"";
        Assert.Equal(expected, query.ApplyFilter(source, filter).Count());
        Assert.Equal(expected, query.ApplyFilter(source.AsQueryable(), filter).Count());
    }

    [Fact]
    public async Task ParseFilter_WhenCultureChanges_ShouldKeepTheValueParsedOriginally()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<PriceModel>>();
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var parsed = query.ParseFilter("Amount = \"1,5\"");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            PriceModel[] source = [new() { Amount = 1.5m }, new() { Amount = 15m }];
            Assert.Same(source[0], Assert.Single(query.ApplyFilter(source, parsed)));
            Assert.Same(source[0], Assert.Single(query.ApplyFilter(source.AsQueryable(), parsed)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task ParseFilter_WhenUsedAcrossHosts_ShouldEnforceTheOriginatingSnapshot()
    {
        await using var firstHost = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var secondHost = await new StrictModelQueryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var original = firstHost.Services.GetRequiredService<IModelQuery<HostBoundModel>>();
        var anotherServiceInFirstHost = firstHost.Services.GetRequiredService<IModelQuery<HostBoundModel>>();
        var otherHost = secondHost.Services.GetRequiredService<IModelQuery<HostBoundModel>>();
        var parsed = original.ParseFilter("Name = \"A\"");
        HostBoundModel[] source = [new() { Name = "A" }, new() { Name = "B" }];

        Assert.NotSame(original, anotherServiceInFirstHost);
        Assert.DoesNotContain(otherHost.GetFields(), field => field.ReflectionName == nameof(HostBoundModel.Name));
        Assert.Same(source[0], Assert.Single(anotherServiceInFirstHost.ApplyFilter(source, parsed)));
        Assert.Same(source[0], Assert.Single(anotherServiceInFirstHost.ApplyFilter(source.AsQueryable(), parsed)));
        var error = Assert.Throws<ModelQueryNormalizationException>(() => otherHost.ApplyFilter(source, parsed));
        Assert.Contains("another host", error.DisplayMessage);
        Assert.Throws<ModelQueryNormalizationException>(() => otherHost.CompileFilter(parsed));
        Assert.Throws<ModelQueryNormalizationException>(() => otherHost.ApplyFilter(source.AsQueryable(), parsed));
    }

    [Fact]
    public async Task ParseFilter_WhenAnExecutionListIsChanged_ShouldNotChangeLaterExecutions()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        var parsed = query.ParseFilter("Count in \"1,2\"");
        SearchModel[] source = [new() { Count = 1 }, new() { Count = 2 }, new() { Count = 3 }];
        var first = query.ApplyFilter(source.AsQueryable(), parsed);
        var visitor = new ListConstantVisitor();
        visitor.Visit(first.Expression);
        Assert.Single(visitor.Lists).Clear();
        Assert.Empty(first);
        Assert.Equal(2, query.ApplyFilter(source, parsed).Count());
        Assert.Equal(2, query.ApplyFilter(source.AsQueryable(), parsed).Count());
    }

    [Fact]
    public async Task Metadata_WhenReturnedFromTheHost_ShouldNotAllowCachedStateMutation()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        var field = query.GetFields().Single(item => item.ReflectionName == nameof(SearchModel.Name));
        Assert.Throws<NotSupportedException>(() => ((IList<QueryField>)query.GetFields()).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)field.QueryNames).Clear());
        Assert.False(typeof(QueryField).GetProperty(nameof(QueryField.Title))!.CanWrite);
        Assert.False(typeof(QueryFieldTypeInfo).GetProperty(nameof(QueryFieldTypeInfo.BasicType))!.CanWrite);
        Assert.False(typeof(QueryFieldSearchInfo).GetProperty(nameof(QueryFieldSearchInfo.IsIgnored))!.CanWrite);
        Assert.NotNull(query.MatchFields([nameof(SearchModel.Name)]).MatchedFields.Single());
        var snapshot = host.Services.GetRequiredService<IModelQuerySnapshotFactory>().PreloadSnapshot<NestedModel>();
        var nested = snapshot.Fields.Single(item => item.ReflectionName == nameof(NestedChild.Value));
        Assert.Throws<NotSupportedException>(() => ((IList<QueryField>)snapshot.Fields).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<(string PropertyName, bool IsCollection)>)nested.NavigationProperties!).Clear());
        Assert.False(typeof(QueryModel).GetProperty(nameof(QueryModel.Name))!.CanWrite);
    }

    [Fact]
    public async Task ApplyFilterAndSelectFields_WhenSourcesAreDeferred_ShouldNotEnumerateDuringComposition()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        var enumerations = 0;
        IEnumerable<SearchModel> Source()
        {
            enumerations++;
            yield return new SearchModel { Name = "A" };
            yield return new SearchModel { Name = "B" };
        }

        var memory = query.ApplyFilter(Source(), "Name = \"A\"");
        var projected = query.SelectFields(Source(), FieldSelection.Include(nameof(SearchModel.Name)));
        var queryable = query.ApplyFilter(Source().AsQueryable(), "Name = \"A\"");
        Assert.Equal(0, enumerations);
        Assert.Single(memory);
        Assert.Equal(2, projected.Count());
        Assert.Single(queryable);
        Assert.Equal(3, enumerations);
    }

    [Fact]
    public async Task ResolveFields_WhenSelectionIsInvalid_ShouldRejectUnknownOrEmptyResults()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<SearchModel>>();
        Assert.Throws<ArgumentException>(() => FieldSelection.Include(" "));
        Assert.Throws<ModelQueryNormalizationException>(() => query.ResolveFields(FieldSelection.Include()));
        var unknown = Assert.Throws<ModelQueryNormalizationException>(() => query.ResolveFields(FieldSelection.Include("missing")));
        Assert.Contains("missing", unknown.DisplayMessage);
        Assert.Throws<ModelQueryNormalizationException>(() => query.ResolveFields(FieldSelection.Exclude(query.GetFields().Select(field => field.QueryNames[0]).ToArray())));

        var names = new[] { nameof(SearchModel.Name) };
        var selection = FieldSelection.Include(names);
        names[0] = "missing";
        Assert.Equal(nameof(SearchModel.Name), Assert.Single(query.ResolveFields(selection)).ReflectionName);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)selection.Names).Clear());
    }

    [Fact]
    public async Task SelectFields_WhenPathsHaveTheSameOutputName_ShouldRejectBothPathsClearly()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<DuplicatePathsModel>>();
        var selection = FieldSelection.Include("Left.Name", "Right.Name");
        DuplicatePathsModel[] source = [new()];
        var error = Assert.Throws<ModelQueryNormalizationException>(() => query.SelectFields(source.AsQueryable(), selection));
        Assert.Contains("Left.Name", error.DisplayMessage);
        Assert.Contains("Right.Name", error.DisplayMessage);
        Assert.Throws<ModelQueryNormalizationException>(() => query.SelectFields((IEnumerable<DuplicatePathsModel>)source, selection));
    }

    [Fact]
    public void GetFieldSelection_WhenArraysAreProvided_ShouldValidateAndCopyThem()
    {
        var request = new SelectionRequest();
        Assert.Null(request.GetFieldSelection());
        request.IncludeFields = [];
        request.ExcludeFields = [];
        Assert.Null(request.GetFieldSelection());
        request.IncludeFields = [nameof(SearchModel.Name)];
        var selection = request.GetFieldSelection();
        request.IncludeFields[0] = "changed";
        Assert.Equal(nameof(SearchModel.Name), Assert.Single(selection!.Names));
        request.ExcludeFields = [nameof(SearchModel.Secret)];
        Assert.Throws<ModelQueryNormalizationException>(() => request.GetFieldSelection());
        request.IncludeFields = [];
        request.ExcludeFields = [" "];
        Assert.Throws<ModelQueryNormalizationException>(() => request.GetFieldSelection());
    }

    public sealed class SearchModel
    {
        public string Name { get; init; } = "";
        [QueryField(ExcludeFromDefaultSearch = true)] public string Secret { get; init; } = "";
        public int Count { get; init; }
    }

    public sealed class PriceModel
    {
        public decimal Amount { get; init; }
    }

    public sealed class HostBoundModel
    {
        [QueryField] public int Id { get; init; }
        public string Name { get; init; } = "";
    }

    private sealed class StrictModelQueryFactory : MonicaTestApplicationFactory<ModuleModelQuery>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) =>
            builder.AddModelQuery(options => options.RequireFieldAnnotation = true);
    }

    public sealed class MixedSearchModel
    {
        public string Name { get; init; } = "";
        public char Code { get; init; }
        public float Score { get; init; }
    }

    public sealed class DuplicatePathsModel
    {
        public LeftPath Left { get; init; } = new();
        public RightPath Right { get; init; } = new();
    }

    public sealed class LeftPath
    {
        public string Name { get; init; } = "";
    }

    public sealed class RightPath
    {
        public string Name { get; init; } = "";
    }

    public sealed class NestedModel
    {
        public NestedChild Child { get; init; } = new();
    }

    public sealed class NestedChild
    {
        public string Value { get; init; } = "";
    }

    private sealed class ListConstantVisitor : ExpressionVisitor
    {
        public List<IList<int>> Lists { get; } = [];

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is IList<int> list) Lists.Add(list);
            return base.VisitConstant(node);
        }
    }

    private sealed class SelectionRequest : IFieldSelectionRequest
    {
        public string[]? IncludeFields { get; set; }
        public string[]? ExcludeFields { get; set; }
    }
}
