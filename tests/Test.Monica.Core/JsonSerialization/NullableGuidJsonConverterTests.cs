using System.Text.Json;
using Monica.Core.JsonSerialization.Converters;
using Xunit;

namespace Test.Monica.Core.JsonSerialization;

public sealed class NullableGuidJsonConverterTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new NullableGuidJsonConverter() }
    };

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    public void Read_WhenUuidFormatIsSupported_ShouldPreserveIdentity(string format)
    {
        var id = Guid.Parse("16a36cc5-9855-45f5-8f70-bb9f32771873");
        var input = JsonSerializer.Serialize(id.ToString(format));
        Assert.Equal(id, JsonSerializer.Deserialize<Guid?>(input, _options));
        Assert.Equal($"\"{id:D}\"", JsonSerializer.Serialize<Guid?>(id, _options));
    }

    [Fact]
    public void Read_WhenNullOrEmptyUuid_ShouldKeepDistinctValues()
    {
        Assert.Null(JsonSerializer.Deserialize<Guid?>("null", _options));
        Assert.Equal(Guid.Empty, JsonSerializer.Deserialize<Guid?>("\"00000000-0000-0000-0000-000000000000\"", _options));
    }

    [Theory]
    [InlineData("\"not-a-uuid\"")]
    [InlineData("\"\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Read_WhenMalformed_ShouldReportJsonError(string input)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Guid?>(input, _options));
    }
}
