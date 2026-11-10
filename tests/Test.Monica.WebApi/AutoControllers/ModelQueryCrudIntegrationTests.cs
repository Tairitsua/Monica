using System.Net;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;
using Monica.ModelQuery.Annotations;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Monica.WebApi.AutoControllers.Abstractions;
using Monica.WebApi.AutoControllers.Models;
using Monica.WebApi.AutoControllers.Services;
using Xunit;

namespace Test.Monica.WebApi.AutoControllers;

public sealed class ModelQueryCrudIntegrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("?DisablePage=true")]
    [InlineData("?DisablePage=true&IncludeFields=code")]
    public async Task GetList_WhenHttpRequestIsAborted_ShouldCancelThePendingDatabaseRead(string query)
    {
        var token = TestContext.Current.CancellationToken;
        var observer = new BlockReadUntilCancelled();
        await using var host = await new ModelQueryCrudFactory(observer).CreateAsync(cancellationToken: token);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        observer.Enabled = true;
        var response = client.GetAsync("/model-query-crud/records" + query, cancellation.Token);
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

    private sealed class ModelQueryCrudFactory(DbCommandInterceptor? interceptor = null) : MonicaTestApplicationFactory<ModelQueryCrudIntegrationTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddDependencyInjection();
            builder.AddResultEnvelope();
            builder.AddAutoControllers();
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

public sealed class ModelQueryCrudDto : EntityDto<int>;

public sealed class ModelQueryCrudContext(DbContextOptions<ModelQueryCrudContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<ModelQueryCrudContext>(options, services)
{
    public DbSet<ModelQueryCrudRow> Rows => Set<ModelQueryCrudRow>();
}

public sealed class ModelQueryCrudRepository(ModelQueryCrudContext context)
    : EfRepository<ModelQueryCrudContext, ModelQueryCrudRow, int>(context);

[Route("model-query-crud")]
public sealed class ModelQueryFixtureCrudService(ModelQueryCrudRepository repository)
    : CrudApplicationService<ModelQueryCrudRow, ModelQueryCrudDto, int, CrudPageRequestDto, ModelQueryCrudRepository>(repository),
        ICrudDisableDelete
{
    [HttpGet("records")]
    public override Task<PagedResult<dynamic>> GetListAsync([FromQuery] CrudPageRequestDto input) => base.GetListAsync(input);

    [HttpPost("records/query")]
    public Task<PagedResult<dynamic>> QueryRecordsAsync([FromBody] CrudPageRequestDto input) => base.GetListAsync(input);
}
