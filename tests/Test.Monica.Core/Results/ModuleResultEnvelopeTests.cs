using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Results;

public sealed class ModuleResultEnvelopeTests
{
    [Fact]
    public void UseResultFieldNames_ShouldApplyToEveryHostSerializer()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddResultEnvelope().UseResultFieldNames(static names =>
            {
                names.Message = "text";
                names.Status = "code";
                names.Data = "payload";
                names.Metadata = "context";
            });
        });

        using var host = builder.Build();
        var canonicalOptions = host.Services
            .GetRequiredService<IJsonSerializerOptionsProvider>()
            .SerializerOptions;
        var httpOptions = host.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;
        var mvcOptions = host.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;

        foreach (var serializerOptions in new[] { canonicalOptions, httpOptions, mvcOptions })
        {
            serializerOptions.IsReadOnly.Should().BeTrue();
            var original = Result.Success("ready").WithMessage("loaded").SetMetadata("tenant", "north");
            var json = JsonSerializer.Serialize(original, serializerOptions);
            using var document = JsonDocument.Parse(json);

            document.RootElement.GetProperty("code").GetInt32().Should().Be((int)ResultStatus.Ok);
            document.RootElement.GetProperty("text").GetString().Should().Be("loaded");
            document.RootElement.GetProperty("payload").GetString().Should().Be("ready");
            document.RootElement.GetProperty("context").GetProperty("tenant").GetString().Should().Be("north");
            document.RootElement.TryGetProperty("status", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("message", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("data", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("metadata", out _).Should().BeFalse();

            var deserialized = JsonSerializer.Deserialize<Result<string>>(json, serializerOptions);
            deserialized.Should().NotBeNull();
            deserialized!.Status.Should().Be(ResultStatus.Ok);
            deserialized.Message.Should().Be("loaded");
            deserialized.Data.Should().Be("ready");
            deserialized.Metadata.Should().NotBeNull();
        }

    }
}
