using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution.Mvc;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainTracingHttpMetadataTests
{
    [Theory]
    [InlineData(true, "mediated")]
    [InlineData(false, "mediated")]
    [InlineData(true, "direct")]
    [InlineData(false, "direct")]
    public async Task Get_WhenAsyncMvcActionQueriesDatabase_ShouldApplyHostDiagnosticPolicy(
        bool exposeDiagnostics, string action)
    {
        await using var application = await new ChainTracingHttpTestApplicationFactory(exposeDiagnostics)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

        using var response = await client.GetAsync(
            $"/chain-tracing/{action}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        result.GetProperty("code").GetInt32().Should().Be(200);
        result.GetProperty("data").GetInt32().Should().Be(42);
        var metadata = result.GetProperty("metadata");
        metadata.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        if (exposeDiagnostics)
        {
            var chain = metadata.GetProperty("chain");
            chain.GetProperty("duration").GetString().Should().NotBeNullOrWhiteSpace();
            Nodes(chain).Should().Contain(node =>
                node.GetProperty("operation").GetString()!.Contains("SELECT 42", StringComparison.Ordinal));
            chain.GetProperty("durationMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
            var database = Nodes(chain).Where(node => node.TryGetProperty("database", out _)).ToArray();
            database.Select(node => node.GetProperty("database").GetProperty("phase").GetString())
                .Should().Equal("connection.open", "command.execute", "reader.consume");
            database.Should().OnlyContain(node => node.GetProperty("database").GetProperty("pendingCount").GetInt32() == 0);
            database[0].GetProperty("database").GetProperty("poolWaitMs").ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            metadata.EnumerateObject().Select(property => property.Name).Should().Equal("traceId");
        }
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            foreach (var descendant in Nodes(child))
                yield return descendant;
        }
    }

    private sealed class ChainTracingHttpTestApplicationFactory(bool exposeDiagnostics)
        : MonicaTestApplicationFactory<ModuleChainTracing>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddJsonSerialization(options =>
            {
                options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
                options.EnableGlobalEnumToString = true;
                options.EnableEnumFormatValue = true;
                options.EnumTypeToIgnore = [typeof(ResStatus)];
            });
            builder.AddResultEnvelope(options => options.ExposeDiagnosticDetails = exposeDiagnostics)
                .UseResultFieldNames(names => names.Status = "code");
            builder.AddControllers().ConfigMvcBuilder(mvc =>
                mvc.AddApplicationPart(typeof(ChainTracingHttpProbeController).Assembly));
            builder.AddChainTracing()
                .UseControllerTracing()
                .AttachControllerTraceMetadata()
                .UseEntityFrameworkCoreTracing()
                .ConfigureEndpoints(context => context.ApplicationBuilder.UseEndpoints(endpoints =>
                    endpoints.MapControllers()));
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddDbContext<ChainTracingHttpProbeDbContext>(options => options
                .UseSqlite("Data Source=:memory:"));
        }
    }
}

[ApiController]
[Route("chain-tracing")]
public sealed class ChainTracingHttpProbeController(ChainTracingHttpProbeDbContext database) : ControllerBase
{
    [HttpGet("mediated")]
    [MediatedController]
    public Task<ObjectResult> Mediated(CancellationToken cancellationToken) => ReadAsync(cancellationToken);

    [HttpGet("direct")]
    public Task<ObjectResult> Direct(CancellationToken cancellationToken) => ReadAsync(cancellationToken);

    private async Task<ObjectResult> ReadAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var value = await database.Database.SqlQueryRaw<int>("SELECT 42 AS Value").SingleAsync(cancellationToken);
        return Res.Ok<int>(value).GetResponse(this);
    }
}

public sealed class ChainTracingHttpProbeDbContext(DbContextOptions<ChainTracingHttpProbeDbContext> options)
    : DbContext(options);
