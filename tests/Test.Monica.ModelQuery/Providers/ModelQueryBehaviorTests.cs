using System.ComponentModel.DataAnnotations.Schema;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
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

public sealed class ModelQueryBehaviorTests(ModelQueryTestApplicationFactory factory)
    : IClassFixture<ModelQueryTestApplicationFactory>
{
    [Fact]
    public async Task Snapshot_WhenNamesAreExplicit_ShouldUseOnlyThoseNamesIgnoringCase()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = host.Services.GetRequiredService<IModelQuerySnapshot<NamedModel>>();

        var field = snapshot.GetField("NUMBER");
        Assert.NotNull(field);
        Assert.Same(field, snapshot.GetField("OrderNumber"));
        Assert.Null(snapshot.GetField(nameof(NamedModel.Code)));
        Assert.Equal(nameof(NamedModel.Code), field.ReflectionName);
        Assert.Contains("number", snapshot.GetAllQueryNames());
        Assert.Equal(nameof(NamedModel), host.Services.GetRequiredService<IModelQuerySnapshotFactory>()
            .PreloadSnapshot<NamedModel>().Model.Name);
    }

    [Fact]
    public async Task Query_WhenFieldHasExplicitNames_ShouldFilterSelectAndSearchByNameAndSelector()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<NamedModel>>();
        NamedModel[] source = [new() { Code = "AX100", Region = "north" }, new() { Code = "BY200", Region = "south" }];

        Assert.Equal("AX100", Assert.Single(query.ApplyFilter(source, "number = \"AX100\"")).Code);
        Assert.Equal("AX100", Assert.Single(query.ApplyFilter(source, x => x.Code, EFieldConditions.Equal, "AX100")).Code);
        Assert.Equal("AX100", Assert.Single(query.ApplyFilter(source.AsQueryable(), x => x.Code, EFieldConditions.Equal, "AX100")).Code);
        Assert.Equal("AX100", Assert.Single(query.ApplySearch(source, "AX1", ["orderNumber"])).Code);
        Assert.Equal("AX100", Assert.Single(query.ApplySearch(source, "AX1")).Code);
        Assert.Equal(nameof(NamedModel.Code), Assert.Single(query.ResolveFields(FieldSelection.Include("number"))).ReflectionName);
        Assert.Equal(nameof(NamedModel.Region), Assert.Single(query.ResolveFields(FieldSelection.Exclude("number", "orderNumber"))).ReflectionName);

        var matches = query.MatchFields(["number", "missing"]);
        Assert.Equal(nameof(NamedModel.Code), Assert.Single(matches.MatchedFields).ReflectionName);
        Assert.Equal("missing", Assert.Single(matches.UnmatchedNames));
        Assert.Throws<ModelQueryNormalizationException>(() => query.ResolveFields(FieldSelection.Exclude("missing")));
        var projected = Assert.Single(query.SelectFields(source.AsQueryable(), FieldSelection.Exclude("number")).Cast<object>().Take(1));
        Assert.Null(projected.GetType().GetProperty(nameof(NamedModel.Code)));
        Assert.NotNull(projected.GetType().GetProperty(nameof(NamedModel.Region)));
    }

    [Fact]
    public async Task Snapshot_WhenQueryNamesConflictOrCannotBeParsed_ShouldFailClearly()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var snapshots = host.Services.GetRequiredService<IModelQuerySnapshotFactory>();
        Assert.Throws<ModelQuerySnapshotException>(() => snapshots.PreloadSnapshot<DuplicateNamesModel>());
        Assert.Throws<ModelQuerySnapshotException>(() => snapshots.PreloadSnapshot<BlankNameModel>());
        Assert.Throws<ModelQuerySnapshotException>(() => snapshots.PreloadSnapshot<WhitespaceNameModel>());
        Assert.Throws<ModelQuerySnapshotException>(() => snapshots.PreloadSnapshot<ParenthesisNameModel>());
    }

    [Fact]
    public async Task FieldNames_WhenContainingFormerSelectionSeparators_ShouldWorkAsIndividualNames()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var commaQuery = host.Services.GetRequiredService<IModelQuery<SeparatorNameModel>>();
        var pipeQuery = host.Services.GetRequiredService<IModelQuery<CustomSeparatorNameModel>>();
        Assert.Single(commaQuery.ResolveFields(FieldSelection.Include("order,number")));
        Assert.Single(pipeQuery.ResolveFields(FieldSelection.Include("order|number")));
        Assert.Single(commaQuery.ApplyFilter(new[] { new SeparatorNameModel { Value = "A" } }, "order,number = \"A\""));
        Assert.Single(pipeQuery.ApplyFilter(new[] { new CustomSeparatorNameModel { Value = "A" } }, "order|number = \"A\""));
    }

    [Fact]
    public async Task SelectFields_WhenCollectionNavigationIsExcluded_ShouldProjectRemainingFields()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var query = host.Services.GetRequiredService<IModelQuery<CollectionModel>>();
        CollectionModel[] source = [new() { Name = "one" }];
        var selected = Assert.Single(query.SelectFields(source.AsQueryable(), FieldSelection.Exclude("Children.Label")).Cast<object>());
        Assert.Equal("one", selected.GetType().GetProperty(nameof(CollectionModel.Name))?.GetValue(selected));
    }

    [Fact]
    public async Task Snapshot_WhenPropertiesHaveIgnoreAnnotations_ShouldAllowExplicitOptInAndConfiguredInclusion()
    {
        await using var defaults = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var configured = await new IncludeIgnoredFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var defaultSnapshot = defaults.Services.GetRequiredService<IModelQuerySnapshot<IgnoredModel>>();
        var configuredSnapshot = configured.Services.GetRequiredService<IModelQuerySnapshot<IgnoredModel>>();
        Assert.Null(defaultSnapshot.GetField(nameof(IgnoredModel.JsonOmitted)));
        Assert.Null(defaultSnapshot.GetField(nameof(IgnoredModel.NotMappedOmitted)));
        Assert.NotNull(defaultSnapshot.GetField(nameof(IgnoredModel.JsonIncluded)));
        Assert.NotNull(defaultSnapshot.GetField(nameof(IgnoredModel.NotMappedIncluded)));
        Assert.NotNull(configuredSnapshot.GetField(nameof(IgnoredModel.JsonOmitted)));
        Assert.NotNull(configuredSnapshot.GetField(nameof(IgnoredModel.NotMappedOmitted)));
    }

    [Fact]
    public async Task Snapshot_WhenFieldAnnotationsAreRequired_ShouldRespectInclusionAndExclusion()
    {
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = host.Services.GetRequiredService<IModelQuerySnapshot<AnnotatedModel>>();

        Assert.NotNull(snapshot.GetField(nameof(AnnotatedModel.Included)));
        Assert.NotNull(snapshot.GetField(nameof(AnnotatedModel.JsonIncluded)));
        Assert.NotNull(snapshot.GetField(nameof(AnnotatedModel.NotMappedIncluded)));
        Assert.Null(snapshot.GetField(nameof(AnnotatedModel.Excluded)));
        Assert.Null(snapshot.GetField(nameof(AnnotatedModel.Unmarked)));
        Assert.NotNull(snapshot.GetField("Child.Included"));
        Assert.Null(snapshot.GetField("Child.Unmarked"));
    }

    [Fact]
    public async Task Snapshot_WhenModelOverridesGlobalAnnotationPolicy_ShouldRespectExplicitFalseAndInheritance()
    {
        await using var host = await new StrictFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var inherited = host.Services.GetRequiredService<IModelQuerySnapshot<InheritedModel>>();
        var overridden = host.Services.GetRequiredService<IModelQuerySnapshot<OverrideModel>>();
        Assert.Null(inherited.GetField(nameof(InheritedModel.Unmarked)));
        Assert.NotNull(inherited.GetField(nameof(InheritedModel.Marked)));
        Assert.NotNull(overridden.GetField(nameof(OverrideModel.Unmarked)));
    }

    [Fact]
    public async Task Snapshot_WhenPrefixesCollide_ShouldQualifyNamesAndHonorFieldOverrides()
    {
        await using var host = await new PrefixFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = host.Services.GetRequiredService<IModelQuerySnapshot<PrefixModel>>();
        Assert.NotNull(snapshot.GetField("name"));
        Assert.NotNull(snapshot.GetField("Child.Name"));
        Assert.NotNull(snapshot.GetField("Child.Id"));
        Assert.NotNull(snapshot.GetField("Child.Street"));
        Assert.NotNull(snapshot.GetField("Code"));
    }

    [Fact]
    public async Task Status_WhenEnabled_ShouldExposeRenamedRouteAndFilteredModelMetadata()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await new StatusFactory().CreateAsync(cancellationToken: token);
        host.Services.GetRequiredService<IModelQuerySnapshotFactory>().PreloadSnapshot<NamedModel>();
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        using var response = await client.GetAsync("/model-query/status?modelName=namedmodel", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(nameof(NamedModel), json.RootElement.GetProperty("models")[0].GetString());
        var snapshot = json.RootElement.GetProperty("snapshots")[0];
        Assert.True(snapshot.TryGetProperty("model", out _));
        Assert.True(snapshot.TryGetProperty("fields", out _));
        using var legacy = await client.GetAsync("/auto-model/status", token);
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    [QueryModel]
    public sealed class NamedModel
    {
        [QueryField(QueryNames = new[] { "number", "orderNumber" })]
        public string Code { get; init; } = "";
        public string Region { get; init; } = "";
    }

    public sealed class DuplicateNamesModel
    {
        [QueryField(QueryNames = new[] { "number" })] public string First { get; init; } = "";
        [QueryField(QueryNames = new[] { "NUMBER" })] public string Second { get; init; } = "";
    }

    public sealed class BlankNameModel
    {
        [QueryField(QueryNames = new[] { " " })] public string Value { get; init; } = "";
    }

    public sealed class WhitespaceNameModel
    {
        [QueryField(QueryNames = new[] { "order number" })] public string Value { get; init; } = "";
    }

    public sealed class SeparatorNameModel
    {
        [QueryField(QueryNames = new[] { "order,number" })] public string Value { get; init; } = "";
    }

    public sealed class CustomSeparatorNameModel
    {
        [QueryField(QueryNames = new[] { "order|number" })] public string Value { get; init; } = "";
    }

    public sealed class ParenthesisNameModel
    {
        [QueryField(QueryNames = new[] { "order(number)" })] public string Value { get; init; } = "";
    }

    public sealed class CollectionModel
    {
        public string Name { get; init; } = "";
        public List<CollectionChild> Children { get; init; } = [];
    }

    public sealed class CollectionChild
    {
        public string Label { get; init; } = "";
    }

    public sealed class IgnoredModel
    {
        [JsonIgnore] public string JsonOmitted { get; init; } = "";
        [NotMapped] public string NotMappedOmitted { get; init; } = "";
        [QueryField, JsonIgnore] public string JsonIncluded { get; init; } = "";
        [QueryField, NotMapped] public string NotMappedIncluded { get; init; } = "";
    }

    [QueryModel(RequireFieldAnnotation = true)]
    public sealed class AnnotatedModel
    {
        [QueryField] public string Included { get; init; } = "";
        [QueryField, JsonIgnore] public string JsonIncluded { get; init; } = "";
        [QueryField, NotMapped] public string NotMappedIncluded { get; init; } = "";
        [QueryField(Exclude = true)] public string Excluded { get; init; } = "";
        public string Unmarked { get; init; } = "";
        [QueryField] public AnnotatedChild Child { get; init; } = new();
    }

    public sealed class AnnotatedChild
    {
        [QueryField] public string Included { get; init; } = "";
        public string Unmarked { get; init; } = "";
    }

    [QueryModel]
    public sealed class InheritedModel
    {
        [QueryField] public string Marked { get; init; } = "";
        public string Unmarked { get; init; } = "";
    }

    [QueryModel(RequireFieldAnnotation = false)]
    public sealed class OverrideModel
    {
        public string Unmarked { get; init; } = "";
    }

    [QueryModel(OmitNavigationPrefix = true)]
    public sealed class PrefixModel
    {
        public string NAME { get; init; } = "";
        public PrefixChild Child { get; init; } = new();
    }

    public sealed class PrefixChild
    {
        public string Name { get; init; } = "";
        public int Id { get; init; }
        [QueryField(OmitNavigationPrefix = false)] public string Street { get; init; } = "";
        public string Code { get; init; } = "";
    }

    private sealed class StrictFactory : MonicaTestApplicationFactory<ModuleModelQuery>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) =>
            builder.AddModelQuery(options => options.RequireFieldAnnotation = true);
    }

    private sealed class PrefixFactory : MonicaTestApplicationFactory<ModuleModelQuery>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) =>
            builder.AddModelQuery(options => options.AutoQualifyConflictingNames = true);
    }

    private sealed class StatusFactory : MonicaTestApplicationFactory<ModuleModelQuery>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) =>
            builder.AddModelQuery(options => options.EnableMinimalApi = true);
    }

    private sealed class IncludeIgnoredFactory : MonicaTestApplicationFactory<ModuleModelQuery>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) =>
            builder.AddModelQuery(options =>
            {
                options.IncludeJsonIgnoredProperties = true;
                options.IncludeNotMappedProperties = true;
            });
    }

}
