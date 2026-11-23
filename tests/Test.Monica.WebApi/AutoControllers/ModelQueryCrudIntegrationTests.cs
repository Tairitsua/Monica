using System.Net;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Monica.Core.Execution;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Annotations;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Monica.WebApi.Annotations;
using Monica.WebApi.AutoControllers.Abstractions;
using Monica.WebApi.AutoControllers.Models;
using Monica.WebApi.AutoControllers.Services;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace Test.Monica.WebApi.AutoControllers;

public sealed class ModelQueryCrudIntegrationTests
{
    [Fact]
    public async Task GetList_WhenUuidCursorIsContinued_ShouldReadEveryRowOnceWithTheRelationalProvider()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: token);
        Guid[] ids =
        [
            Guid.Parse("019a8c82-1d00-7000-8000-000000002329"),
            Guid.Parse("67ef9c86-f704-4d65-b3bf-f89d602ee1a9"),
            Guid.Parse("f74192cb-dbcf-4884-921a-b4a2c69982be")
        ];
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, ct) =>
        {
            db.UuidRows.AddRange(ids.Select(id => new UuidCursorRow { Id = id }));
            return await db.SaveChangesAsync(ct);
        }, token);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();

        using var first = await client.GetAsync("/uuid-cursor/records?MaxResultCount=2", token);
        first.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync(token));
        var firstPage = firstJson.RootElement.GetProperty("data");
        Assert.Equal(3, firstPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { ids[2], ids[1] }, firstPage.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetGuid()));
        Assert.Equal(ids[1].ToString("D"), firstPage.GetProperty("cursor").GetString());

        using var second = await client.GetAsync("/uuid-cursor/records?MaxResultCount=2&Cursor=" + ids[1].ToString("D"), token);
        second.EnsureSuccessStatusCode();
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync(token));
        Assert.Equal(ids[0], Assert.Single(secondJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("/model-query-crud/records")]
    [InlineData("/model-query-crud/records?DisablePage=true")]
    [InlineData("/model-query-crud/records?DisablePage=true&IncludeFields=code")]
    [InlineData("/api/catalog-fixture/1")]
    public async Task CrudRead_WhenHttpRequestIsAborted_ShouldCancelThePendingDatabaseRead(string route)
    {
        var token = TestContext.Current.CancellationToken;
        var observer = new BlockReadUntilCancelled();
        await using var host = await new ModelQueryCrudFactory(observer).CreateAsync(cancellationToken: token);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        observer.Enabled = true;
        var response = client.GetAsync(route, cancellation.Token);
        try
        {
            var first = await Task.WhenAny(observer.Entered.Task, response).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (first == response)
            {
                using var unexpected = await response;
                Assert.Fail($"The request returned before reaching the database: {unexpected.StatusCode}, {await unexpected.Content.ReadAsStringAsync(token)}");
            }
            var databaseToken = await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(databaseToken.CanBeCanceled);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
            await observer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(databaseToken.IsCancellationRequested);
        }
        finally
        {
            observer.Release.TrySetResult();
            await cancellation.CancelAsync();
        }
    }

    private sealed class BlockReadUntilCancelled : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Enabled) return result;
            Entered.TrySetResult(cancellationToken);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Exited.TrySetResult();
            }
            return result;
        }
    }

    [Fact]
    public async Task GetList_WhenFilteringSearchingAndSelectingFields_ShouldUseThePublishedRequestContract()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: token);
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, cancellationToken) =>
        {
            db.Rows.AddRange(
                new ModelQueryCrudRow { Id = 1, Number = "AX100", Region = "north", Carrier = "Bluebird" },
                new ModelQueryCrudRow { Id = 2, Number = "AX200", Region = "north", Carrier = "Redbird" },
                new ModelQueryCrudRow { Id = 3, Number = "AX300", Region = "south", Carrier = "O'HAIR" },
                new ModelQueryCrudRow { Id = 4, Number = "AX400", Region = "north", Carrier = "Bluebird", IsDeleted = true });
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }, token);

        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        using var selected = await client.GetAsync(
            "/model-query-crud/records?Filter=Region%20%3D%20%22north%22&SearchText=AX1&SearchFields=code&SearchFields=Carrier&IncludeFields=code&IncludeFields=Carrier",
            token);
        var selectedBody = await selected.Content.ReadAsStringAsync(token);
        Assert.True(selected.StatusCode == HttpStatusCode.OK, selectedBody);
        using var selectedJson = JsonDocument.Parse(selectedBody);
        var selectedItems = selectedJson.RootElement.GetProperty("data").GetProperty("items");
        var row = Assert.Single(selectedItems.EnumerateArray());
        Assert.Equal("AX100", row.GetProperty("number").GetString());
        Assert.Equal("Bluebird", row.GetProperty("carrier").GetString());
        Assert.False(row.TryGetProperty("region", out _));

        using var except = await client.GetAsync(
            "/model-query-crud/records?Filter=Region%20%3D%20%22north%22&ExcludeFields=Region",
            token);
        var exceptBody = await except.Content.ReadAsStringAsync(token);
        Assert.True(except.StatusCode == HttpStatusCode.OK, exceptBody);
        using var exceptJson = JsonDocument.Parse(exceptBody);
        var exceptItems = exceptJson.RootElement.GetProperty("data").GetProperty("items");
        Assert.Equal(2, exceptItems.GetArrayLength());
        foreach (var item in exceptItems.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("number", out _));
            Assert.True(item.TryGetProperty("carrier", out _));
            Assert.False(item.TryGetProperty("region", out _));
        }

        using var posted = await client.PostAsJsonAsync("/model-query-crud/records/query", new
        {
            filter = "Region = \"north\"",
            searchText = "AX_00",
            searchFields = new[] { "code", "Carrier" },
            excludeFields = new[] { "Region", "Carrier" }
        }, token);
        var postedBody = await posted.Content.ReadAsStringAsync(token);
        Assert.True(posted.StatusCode == HttpStatusCode.OK, postedBody);
        using var postedJson = JsonDocument.Parse(postedBody);
        var postedItems = postedJson.RootElement.GetProperty("data").GetProperty("items");
        Assert.Equal(2, postedItems.GetArrayLength());
        foreach (var item in postedItems.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("number", out _));
            Assert.False(item.TryGetProperty("region", out _));
            Assert.False(item.TryGetProperty("carrier", out _));
        }

        using var wildcard = await client.GetAsync(
            "/model-query-crud/records?SearchText=A%25_00&SearchFields=code&IncludeFields=code", token);
        var wildcardBody = await wildcard.Content.ReadAsStringAsync(token);
        Assert.True(wildcard.StatusCode == HttpStatusCode.OK, wildcardBody);
        using var wildcardJson = JsonDocument.Parse(wildcardBody);
        Assert.Equal(3, wildcardJson.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());

        using var quoted = await client.GetAsync(
            "/model-query-crud/records?SearchText=O%27HAIR&SearchFields=Carrier&IncludeFields=code", token);
        var quotedBody = await quoted.Content.ReadAsStringAsync(token);
        Assert.True(quoted.StatusCode == HttpStatusCode.OK, quotedBody);
        using var quotedJson = JsonDocument.Parse(quotedBody);
        var quotedRow = Assert.Single(quotedJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("AX300", quotedRow.GetProperty("number").GetString());

        using var deleted = await client.GetAsync(
            "/model-query-crud/records?Filter=removed%20%3D%20%22true%22&IncludeFields=code", token);
        var deletedBody = await deleted.Content.ReadAsStringAsync(token);
        Assert.True(deleted.StatusCode == HttpStatusCode.OK, deletedBody);
        using var deletedJson = JsonDocument.Parse(deletedBody);
        var deletedRow = Assert.Single(deletedJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("AX400", deletedRow.GetProperty("number").GetString());

        using var unknown = await client.GetAsync(
            "/model-query-crud/records?Filter=Region%20%3D%20%22absent%22&IncludeFields=missing", token);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("missing", await unknown.Content.ReadAsStringAsync(token));

        using var blankSearch = await client.PostAsJsonAsync("/model-query-crud/records/query", new
        {
            searchText = "AX",
            searchFields = new[] { "" }
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, blankSearch.StatusCode);

        using var conflicting = await client.PostAsJsonAsync("/model-query-crud/records/query", new
        {
            includeFields = new[] { "code" },
            excludeFields = new[] { "Carrier" }
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, conflicting.StatusCode);
    }

    [Theory]
    [InlineData("Id desc", 4, 3, 2, 1)]
    [InlineData("Id asc", 1, 2, 3, 4)]
    [InlineData("Region desc, Number asc", 1, 3, 2, 4)]
    public async Task Cursor_ShouldContinueTheEffectiveOrder(string sorting, params int[] expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: ct);
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, token) =>
        {
            db.Rows.AddRange(
                new ModelQueryCrudRow { Id = 1, Region = "B", Number = "M" },
                new ModelQueryCrudRow { Id = 2, Region = "A", Number = "Z" },
                new ModelQueryCrudRow { Id = 3, Region = "B", Number = "M" },
                new ModelQueryCrudRow { Id = 4, Region = "A", Number = "Z" });
            return await db.SaveChangesAsync(token);
        }, ct);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        var url = "/model-query-crud/records?MaxResultCount=2&Sorting=" + Uri.EscapeDataString(sorting);
        var cursor = "";
        var ids = new List<int>();
        for (var page = 0; page < 3; page++)
        {
            using var response = await client.GetAsync(url + cursor, ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var data = json.RootElement.GetProperty("data");
            ids.AddRange(data.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
            if (page < 2) cursor = "&Cursor=" + data.GetProperty("cursor").GetString();
        }
        Assert.Equal(expected, ids);
    }

    [Fact]
    public async Task DistinctPages_ShouldSortTheSelectedTupleBeforeApplyingTheOffset()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: ct);
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, token) =>
        {
            db.Rows.AddRange(
                new ModelQueryCrudRow { Id = 1, Number = "B" },
                new ModelQueryCrudRow { Id = 2, Number = "A" },
                new ModelQueryCrudRow { Id = 3, Number = "C" },
                new ModelQueryCrudRow { Id = 4, Number = "B" });
            return await db.SaveChangesAsync(token);
        }, ct);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        const string url = "/model-query-crud/records?IncludeFields=code&Features=Distinct&MaxResultCount=1&Sorting=";
        foreach (var (page, number) in new[] { (1, "C"), (2, "B"), (3, "A") })
        {
            using var response = await client.GetAsync($"{url}code%20desc&Page={page}", ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(3, data.GetProperty("totalCount").GetInt32());
            Assert.Equal(number, Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("number").GetString());
        }
        using var invalid = await client.GetAsync(url + "Region", ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task GetList_WhenSortingTiedRows_ShouldPreservePagingAndEnrichOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: ct);
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, token) =>
        {
            db.Rows.AddRange(
                new ModelQueryCrudRow { Id = 10, Number = "A", Region = "north", Carrier = "shared" },
                new ModelQueryCrudRow { Id = 20, Number = "B", Region = "north", Carrier = "shared" },
                new ModelQueryCrudRow { Id = 30, Number = "C", Region = "north", Carrier = "other" },
                new ModelQueryCrudRow { Id = 40, Number = "deny", Region = "south", Carrier = "other" });
            return await db.SaveChangesAsync(token);
        }, ct);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();

        using var accepted = await client.GetAsync("/model-query-crud/records?Sorting=Region&Page=2&MaxResultCount=1", ct);
        accepted.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(ct));
        var page = json.RootElement.GetProperty("data");
        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("enriched:B", row.GetProperty("number").GetString());
        Assert.Equal(2, row.GetProperty("num").GetInt32());
        Assert.Equal(4, page.GetProperty("totalCount").GetInt32());
        Assert.Equal("20", page.GetProperty("cursor").GetString());
        Assert.Equal(2, page.GetProperty("currentPage").GetInt32());
        Assert.Equal(1, page.GetProperty("pageSize").GetInt32());

        using var distinct = await client.GetAsync("/model-query-crud/records?IncludeFields=Carrier&Features=Distinct&MaxResultCount=1", ct);
        distinct.EnsureSuccessStatusCode();
        using var distinctJson = JsonDocument.Parse(await distinct.Content.ReadAsStringAsync(ct));
        var selectedPage = distinctJson.RootElement.GetProperty("data");
        Assert.Equal(2, selectedPage.GetProperty("totalCount").GetInt32());
        Assert.Single(Assert.Single(selectedPage.GetProperty("items").EnumerateArray()).EnumerateObject());
        Assert.True(!selectedPage.TryGetProperty("cursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null);

        using var denied = await client.GetAsync("/model-query-crud/records?SearchText=deny&SearchFields=code", ct);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        using var deniedJson = JsonDocument.Parse(await denied.Content.ReadAsStringAsync(ct));
        Assert.Equal("Enrichment refused.", deniedJson.RootElement.GetProperty("message").GetString());
        Assert.Equal("number", deniedJson.RootElement.GetProperty("metadata").GetProperty("field").GetString());
    }

    [Fact]
    public async Task ListStream_WhenSelectingProperties_ShouldValidateAndLimitRowsWithoutDeduplicating()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: ct);
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, token) =>
        {
            db.Rows.AddRange(
                new ModelQueryCrudRow { Id = 1, Number = "A", Carrier = "other" },
                new ModelQueryCrudRow { Id = 2, Number = "B", Carrier = "shared" },
                new ModelQueryCrudRow { Id = 3, Number = "C", Carrier = "shared" });
            return await db.SaveChangesAsync(token);
        }, ct);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        const string route = "/model-query-crud/model-query-fixture/list-stream";

        using var full = await client.PostAsJsonAsync(route, new { MaxResultCount = 2, Page = 2, DisablePage = true }, ct);
        full.EnsureSuccessStatusCode();
        using var fullJson = JsonDocument.Parse(await full.Content.ReadAsStringAsync(ct));
        Assert.Equal(new[] { "C", "B" }, fullJson.RootElement.EnumerateArray().Select(item => item.GetProperty("number").GetString()));
        Assert.Equal(new[] { 3, 2 }, fullJson.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));

        using var selected = await client.PostAsJsonAsync(route, new
        {
            IncludeFields = new[] { "Carrier" }, Features = "Distinct", MaxResultCount = 2, Page = 2
        }, ct);
        selected.EnsureSuccessStatusCode();
        using var selectedJson = JsonDocument.Parse(await selected.Content.ReadAsStringAsync(ct));
        Assert.Equal(new[] { "shared", "shared" }, selectedJson.RootElement.EnumerateArray()
            .Select(item => Assert.Single(item.EnumerateObject()).Value.GetString()));

        using var invalid = await client.PostAsJsonAsync(route, new { IncludeFields = new[] { "unknown" } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("unknown", await invalid.Content.ReadAsStringAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListStream_WhenRequestIsAborted_ShouldCancelDatabaseEnumeration(bool selectFields)
    {
        var ct = TestContext.Current.CancellationToken;
        var observer = new BlockReadUntilCancelled();
        await using var host = await new ModelQueryCrudFactory(observer).CreateAsync(cancellationToken: ct);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        observer.Enabled = true;
        var request = client.PostAsJsonAsync("/model-query-crud/model-query-fixture/list-stream",
            new { IncludeFields = selectFields ? new[] { "code" } : Array.Empty<string>() }, cancellation.Token);
        try
        {
            var databaseToken = await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await observer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.True(databaseToken.IsCancellationRequested);
        }
        finally
        {
            observer.Release.TrySetResult();
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public async Task AutoControllers_WhenPublishingTypedCrud_ShouldRetainRoutesAndDescribeCollectionShapes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory(payloadName: "payload").CreateAsync(cancellationToken: ct);
        var actions = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>();
        var catalog = actions.Where(action => action.ControllerTypeInfo.AsType() == typeof(CrudServiceCatalogFixture)).ToArray();
        var routes = catalog.Select(action => (
            action.MethodInfo.Name,
            Assert.Single(Assert.Single(action.ActionConstraints!.OfType<HttpMethodActionConstraint>()).HttpMethods),
            action.AttributeRouteInfo!.Template!)).OrderBy(route => route.Name).ToArray();
        Assert.Equal(new[]
        {
            ("BulkDeleteAsync", "POST", "api/catalog-fixture/bulk-delete"),
            ("CreateAsync", "POST", "api/catalog-fixture"),
            ("DeleteAsync", "DELETE", "api/catalog-fixture/{id}"),
            ("GetAlternateAsync", "GET", "api/catalog-fixture/alternate"),
            ("GetAsync", "GET", "api/catalog-fixture/{id}"),
            ("GetListAsync", "GET", "api/catalog-fixture"),
            ("ListAsync", "POST", "api/catalog-fixture/list"),
            ("ListStreamAsync", "POST", "api/catalog-fixture/list-stream"),
            ("UpdateAsync", "PUT", "api/catalog-fixture/{id}")
        }, routes);
        Assert.Equal(4, actions.Count(action => action.ControllerTypeInfo.AsType() == typeof(CrudServiceUuidCursorFixture)));

        var descriptions = host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(group => group.Items);
        var stream = Assert.Single(descriptions, description => description.RelativePath == "api/catalog-fixture/list-stream");
        var streamInput = Assert.Single(stream.ParameterDescriptions);
        Assert.Equal(typeof(CrudPageRequestDto), streamInput.Type);
        Assert.Equal(BindingSource.Body, streamInput.Source);
        Assert.Equal(typeof(IAsyncEnumerable<object>), Assert.Single(stream.SupportedResponseTypes, type => type.StatusCode == 200).Type);
        var page = Assert.Single(descriptions, description => description.RelativePath == "api/catalog-fixture" && description.HttpMethod == "GET");
        Assert.Equal(typeof(Result<Page<object>>), Assert.Single(page.SupportedResponseTypes, type => type.StatusCode == 200).Type);

        var document = await host.Services.GetRequiredService<IAsyncSwaggerProvider>().GetSwaggerAsync("v1");
        var streamSchema = document.Paths["/api/catalog-fixture/list-stream"].Operations![HttpMethod.Post]
            .Responses!["200"].Content!["application/json"].Schema!;
        AssertRowAlternatives(streamSchema.Items!, typeof(ModelQueryCrudDto));
        var pageSchema = document.Paths["/api/catalog-fixture"].Operations![HttpMethod.Get]
            .Responses!["200"].Content!["application/json"].Schema!;
        AssertRowAlternatives(pageSchema.Properties!["payload"].Properties!["items"].Items!, typeof(ModelQueryCrudDto));
        var envelope = Assert.IsType<OpenApiSchemaReference>(Assert.Single(pageSchema.AllOf!));
        var baseEnvelope = document.Components!.Schemas![envelope.Reference.Id!];
        Assert.Contains("status", baseEnvelope.Properties!.Keys);
        var originalPage = Assert.IsType<OpenApiSchemaReference>(baseEnvelope.Properties["payload"]);
        Assert.Contains("totalCount", document.Components.Schemas[originalPage.Reference.Id!].Properties!.Keys);
        var alternateSchema = document.Paths["/api/catalog-fixture/alternate"].Operations![HttpMethod.Get]
            .Responses!["200"].Content!["application/json"].Schema!;
        AssertRowAlternatives(alternateSchema.Properties!["payload"].Properties!["items"].Items!, typeof(CatalogExportRow));

        static void AssertRowAlternatives(IOpenApiSchema schema, Type rowType)
        {
            Assert.Collection(schema.AnyOf!, row =>
            {
                var reference = Assert.IsType<OpenApiSchemaReference>(row);
                Assert.EndsWith(rowType.Name, reference.Reference.Id);
            }, selected => Assert.Equal(JsonSchemaType.Object, selected.Type));
        }
    }

    [Fact]
    public async Task ReadPage_WhenProjectingAlternateRows_ShouldKeepHeaderSelectionAndPaging()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: ct);
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, token) =>
        {
            db.Rows.AddRange(new ModelQueryCrudRow { Id = 10, Number = "A" }, new ModelQueryCrudRow { Id = 20, Number = "B" });
            return await db.SaveChangesAsync(token);
        }, ct);
        await using var scope = host.CreateScope(ct);
        var input = new CrudPageRequestDto { IncludeFields = ["code"], MaxResultCount = 1, Page = 2 };
        var page = await scope.Resolve<CrudServiceCatalogFixture>().ReadExportPageAsync(input, ct);

        Assert.Equal("A", Assert.Single(page.Items).Number);
        Assert.Equal("code", Assert.Single(input.IncludeFields));
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.CurrentPage);
        Assert.Equal(1, page.PageSize);
        Assert.Null(page.Cursor);

        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        using var full = await client.GetAsync("/api/catalog-fixture/alternate?MaxResultCount=1", ct);
        full.EnsureSuccessStatusCode();
        using var fullJson = JsonDocument.Parse(await full.Content.ReadAsStringAsync(ct));
        var fullItem = Assert.Single(fullJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("number", Assert.Single(fullItem.EnumerateObject()).Name);
        Assert.Equal("B", fullItem.GetProperty("number").GetString());

        using var selected = await client.GetAsync("/api/catalog-fixture/alternate?IncludeFields=Region&MaxResultCount=1", ct);
        selected.EnsureSuccessStatusCode();
        using var selectedJson = JsonDocument.Parse(await selected.Content.ReadAsStringAsync(ct));
        var selectedItem = Assert.Single(selectedJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("region", Assert.Single(selectedItem.EnumerateObject()).Name);
    }

    [Fact]
    public async Task CrudWrites_WhenUpdatingDeletingAndRollingBack_ShouldUseTheOwningTransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: ct);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();

        using var failedCreate = await client.PostAsJsonAsync("/api/catalog-fixture", new CatalogCreateInput("rollback", "north", "carrier", true), ct);
        Assert.Equal(HttpStatusCode.InternalServerError, failedCreate.StatusCode);
        await host.VerifyAsync<ModelQueryCrudContext>(async (db, token) => Assert.Empty(await db.Rows.ToListAsync(token)), ct);

        using var created = await client.PostAsJsonAsync("/api/catalog-fixture", new CatalogCreateInput("A", "north", "carrier"), ct);
        created.EnsureSuccessStatusCode();
        var id = 0;
        await host.VerifyAsync<ModelQueryCrudContext>(async (db, token) => id = (await db.Rows.SingleAsync(token)).Id, ct);
        Assert.True(id > 0);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
        Assert.Equal($"Created successfully: {id}", createdJson.RootElement.GetProperty("message").GetString());

        using var changed = await client.PutAsJsonAsync($"/api/catalog-fixture/{id}", new CatalogUpdateInput("B"), ct);
        changed.EnsureSuccessStatusCode();
        using var fetched = await client.GetAsync($"/api/catalog-fixture/{id}", ct);
        fetched.EnsureSuccessStatusCode();
        using var fetchedJson = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync(ct));
        var detail = fetchedJson.RootElement.GetProperty("data");
        Assert.Equal("B", detail.GetProperty("number").GetString());
        Assert.Equal("north", detail.GetProperty("region").GetString());
        Assert.Equal("carrier", detail.GetProperty("carrier").GetString());

        using var bulkDeleted = await client.PostAsJsonAsync("/api/catalog-fixture/bulk-delete", new { Ids = new[] { id } }, ct);
        bulkDeleted.EnsureSuccessStatusCode();
        using var deletedMissing = await client.DeleteAsync($"/api/catalog-fixture/{id}", ct);
        deletedMissing.EnsureSuccessStatusCode();
        using var deletedJson = JsonDocument.Parse(await deletedMissing.Content.ReadAsStringAsync(ct));
        Assert.Equal($"Deleted successfully: {id}", deletedJson.RootElement.GetProperty("message").GetString());
        using var hidden = await client.GetAsync($"/api/catalog-fixture/{id}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);
        using var hiddenJson = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync(ct));
        Assert.Equal("The requested data was not found.", hiddenJson.RootElement.GetProperty("message").GetString());
        await host.VerifyAsync<ModelQueryCrudContext>(async (db, token) =>
            Assert.True((await db.Rows.IgnoreQueryFilters().SingleAsync(token)).IsDeleted), ct);
    }

    private sealed class ModelQueryCrudFactory(DbCommandInterceptor? interceptor = null, string? payloadName = null)
        : MonicaTestApplicationFactory<ModelQueryCrudIntegrationTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddDependencyInjection();
            builder.AddResultEnvelope(options => options.FieldNames.Data = payloadName);
            builder.AddAutoControllers(configureCrud: options => options.RoutePath = "api");
            builder.AddSwagger();
            builder.AddRepository().AddRepositoryDbContext<ModelQueryCrudContext>(
                (_, options) => options.UseSqlite("Data Source=unused"));
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.UseTestDatabase<ModelQueryCrudContext>((_, options) =>
            {
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.AddScoped<ModelQueryCrudRepository>();
            services.AddScoped<UuidCursorRepository>();
        }
    }
}

public sealed class ModelQueryCrudRow : Entity<int>, IHasSoftDelete
{
    [QueryField(QueryNames = new[] { "code" })]
    public string Number { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Carrier { get; set; } = string.Empty;
    [QueryField(QueryNames = new[] { "removed" })]
    public bool IsDeleted { get; set; }
}

public sealed class ModelQueryCrudDto : EntityDto<int>, IHasDtoSequenceNumber
{
    public string Number { get; set; } = string.Empty;
    public int? Num { get; set; }
}

public sealed record ModelQueryCrudDetail(string Number, string Region, string Carrier);

public sealed class ModelQueryCrudContext(DbContextOptions<ModelQueryCrudContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<ModelQueryCrudContext>(options, services)
{
    public DbSet<ModelQueryCrudRow> Rows => Set<ModelQueryCrudRow>();
    public DbSet<UuidCursorRow> UuidRows => Set<UuidCursorRow>();
}

public sealed class ModelQueryCrudRepository(ModelQueryCrudContext context)
    : EfRepository<ModelQueryCrudContext, ModelQueryCrudRow, int>(context);

[Route("model-query-crud")]
public sealed class CrudServiceModelQueryFixture(ModelQueryCrudRepository repository)
    : CrudService<ModelQueryCrudRow, int, ModelQueryCrudDetail, ModelQueryCrudDto, CrudPageRequestDto>(repository)
{
    protected override Task<ModelQueryCrudDetail> ToDetailAsync(ModelQueryCrudRow entity, CancellationToken ct)
        => Task.FromResult(new ModelQueryCrudDetail(entity.Number, entity.Region, entity.Carrier));

    protected override IAsyncEnumerable<ModelQueryCrudDto> ReadRowsAsync(IQueryable<ModelQueryCrudRow> window, CancellationToken ct)
        => window.Select(row => new ModelQueryCrudDto { Id = row.Id, Number = row.Number }).AsAsyncEnumerable();

    protected override Task<Result<IReadOnlyList<ModelQueryCrudDto>>> EnrichPageAsync(CrudPageRequestDto input,
        IReadOnlyList<ModelQueryCrudDto> rows, CancellationToken ct)
    {
        if (rows.Any(row => row.Number == "deny"))
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<ModelQueryCrudDto>>("Enrichment refused.", ResultStatus.Conflict)
                .SetMetadata("field", "number"));
        }

        IReadOnlyList<ModelQueryCrudDto> enriched = rows.Select(row => new ModelQueryCrudDto
        {
            Id = row.Id, Number = $"enriched:{row.Number}", Num = row.Num
        }).ToArray();
        return Task.FromResult(Result.Success(enriched));
    }

    [HttpGet("records")]
    public override Task<Result<Page<dynamic>>> GetListAsync([FromQuery] CrudPageRequestDto input) => base.GetListAsync(input);

    [HttpPost("records/query")]
    public Task<Result<Page<dynamic>>> QueryRecordsAsync([FromBody] CrudPageRequestDto input) => base.GetListAsync(input);
}

public sealed class UuidCursorRow : Entity<Guid>;
public sealed class UuidCursorDto : EntityDto<Guid>;
public sealed class UuidCursorRepository(ModelQueryCrudContext context)
    : EfRepository<ModelQueryCrudContext, UuidCursorRow, Guid>(context);

[Route("uuid-cursor")]
public sealed class CrudServiceUuidCursorFixture(UuidCursorRepository repository)
    : CrudService<UuidCursorRow, Guid, UuidCursorDto, UuidCursorDto, CrudPageRequestDto>(repository)
{
    protected override Task<UuidCursorDto> ToDetailAsync(UuidCursorRow entity, CancellationToken ct)
        => Task.FromResult(new UuidCursorDto { Id = entity.Id });

    protected override IAsyncEnumerable<UuidCursorDto> ReadRowsAsync(IQueryable<UuidCursorRow> window, CancellationToken ct)
        => window.Select(row => new UuidCursorDto { Id = row.Id }).AsAsyncEnumerable();

    [HttpGet("records")]
    public override Task<Result<Page<dynamic>>> GetListAsync([FromQuery] CrudPageRequestDto input) => base.GetListAsync(input);
}

public sealed record CatalogCreateInput(string Number, string Region, string Carrier, bool RollBack = false);
public sealed record CatalogUpdateInput(string Number);
public sealed record CatalogExportRow(string Number);

public sealed class CrudServiceCatalogFixture(ModelQueryCrudRepository repository)
    : CrudService<ModelQueryCrudRow, int, ModelQueryCrudDetail, ModelQueryCrudDto, CrudPageRequestDto>(repository)
{
    protected override Task<ModelQueryCrudDetail> ToDetailAsync(ModelQueryCrudRow entity, CancellationToken ct)
        => Task.FromResult(new ModelQueryCrudDetail(entity.Number, entity.Region, entity.Carrier));

    protected override IAsyncEnumerable<ModelQueryCrudDto> ReadRowsAsync(IQueryable<ModelQueryCrudRow> window, CancellationToken ct)
        => window.Select(row => new ModelQueryCrudDto { Id = row.Id, Number = row.Number }).AsAsyncEnumerable();

    public async Task<Result> CreateAsync(CatalogCreateInput input)
    {
        var result = await InsertEntityAsync(new ModelQueryCrudRow
        {
            Number = input.Number, Region = input.Region, Carrier = input.Carrier
        }, QueryCancellationToken);
        if (input.RollBack) throw new InvalidOperationException("Fail after generating the key.");
        return result;
    }

    public Task<Result> UpdateAsync(int id, CatalogUpdateInput input)
        => UpdateEntityAsync(id, entity => entity.Number = input.Number, QueryCancellationToken);

    public Task<Result> DeleteAsync(int id) => DeleteEntityAsync(id, QueryCancellationToken);

    public Task<Result> BulkDeleteAsync(CrudBulkDeleteRequestDto<int> input) => DeleteEntitiesAsync(input.Ids, QueryCancellationToken);

    [CrudCollectionResponse(typeof(CatalogExportRow))]
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public async Task<Result<Page<dynamic>>> GetAlternateAsync(CrudPageRequestDto input)
    {
        var ct = QueryCancellationToken;
        var selection = input.GetFieldSelection();
        var filtered = await CreateFilteredQueryAsync(input);
        if (selection is not null)
        {
            return Result.Success(await ReadFieldPageAsync(input, filtered, selection, ct));
        }

        var page = await ReadPageAsync(input, filtered, ProjectExport, ct);
        return Result.Success(new Page<dynamic>(page.TotalCount, page.Items, page.CurrentPage, page.PageSize, page.Cursor));
    }

    [NonAction]
    public async Task<Page<CatalogExportRow>> ReadExportPageAsync(CrudPageRequestDto input, CancellationToken ct)
        => await ReadPageAsync(input, await CreateFilteredQueryAsync(input), ProjectExport, ct);

    private static IQueryable<CatalogExportRow> ProjectExport(IQueryable<ModelQueryCrudRow> query)
        => query.Select(row => new CatalogExportRow(row.Number));
}
