using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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
    [Fact]
    public async Task GetList_WhenFilteringFuzzyAndSelectingFields_ShouldUseThePublishedRequestContract()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await new ModelQueryCrudFactory().CreateAsync(cancellationToken: token);
        await host.SeedAsync<ModelQueryCrudContext, int>(async (db, cancellationToken) =>
        {
            db.Rows.AddRange(
                new ModelQueryCrudRow { Id = 1, Number = "AX100", Region = "north", Carrier = "Bluebird" },
                new ModelQueryCrudRow { Id = 2, Number = "AX200", Region = "north", Carrier = "Redbird" },
                new ModelQueryCrudRow { Id = 3, Number = "AX300", Region = "south", Carrier = "Bluebird" });
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }, token);

        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
        using var selected = await client.GetAsync(
            "/model-query-crud/records?Filter=Region%20%3D%20%22north%22&Fuzzy=AX1&FuzzyColumns=code&SelectColumns=code%2CCarrier",
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
            "/model-query-crud/records?Filter=Region%20%3D%20%22north%22&SelectExceptColumns=Region",
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
    }

    private sealed class ModelQueryCrudFactory : MonicaTestApplicationFactory<ModelQueryCrudIntegrationTests>
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
            services.UseTestDatabase<ModelQueryCrudContext>();
            services.AddScoped<ModelQueryCrudRepository>();
        }
    }
}

public sealed class ModelQueryCrudRow : Entity<int>
{
    [QueryField(QueryNames = new[] { "code" })]
    public string Number { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Carrier { get; set; } = string.Empty;
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
}
