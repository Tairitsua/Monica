using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Monica.Core.Results;
using Monica.Core.Results.Services;
using Xunit;

namespace Test.Monica.Core.Results;

public sealed class ResultErrorTests
{
    [Theory]
    [InlineData(ResultStatus.Ok)]
    [InlineData(ResultStatus.Created)]
    public void Success_WhenDataIsNull_ShouldRemainSuccessWithoutPromisingPayload(ResultStatus status)
    {
        var result = new Result<string?> { Status = status };
        Assert.True(result.IsSuccess());
        Assert.Null(result.Data);
        Assert.False(result.IsFailure());
    }

    [Theory]
    [InlineData(ResultStatus.BadRequest)]
    [InlineData(ResultStatus.Unauthorized)]
    [InlineData(ResultStatus.Conflict)]
    [InlineData(ResultStatus.TooManyRequests)]
    [InlineData(ResultStatus.BadGateway)]
    [InlineData(ResultStatus.ServiceUnavailable)]
    [InlineData(ResultStatus.GatewayTimeout)]
    public void Projection_WhenStatusIsSupported_ShouldMapOneToOneToItsHttpStatus(ResultStatus status)
    {
        Assert.Equal((int)status, ResultHttpProjection.GetStatusCode(new Result { Status = status }));
    }

    [Fact]
    public void Projection_WhenStatusIsOutsideTheWhitelist_ShouldRejectItAsAContractDefect()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResultHttpProjection.GetStatusCode(new Result { Status = (ResultStatus)460 }));
    }

    [Fact]
    public void Error_WhenTraceIsMissing_ShouldRejectInvalidConstruction()
    {
        Assert.Throws<ArgumentException>(() => new ResultError("test", ""));
        var context = new DefaultHttpContext { TraceIdentifier = "request-123" };
        Assert.Equal(ResultTraceId.Capture(context), ResultTraceId.Capture(context));
        Assert.False(string.IsNullOrWhiteSpace(ResultTraceId.Capture()));
    }

    [Fact]
    public void Presentation_WhenMetadataContainsDiagnostics_ShouldRetainOnlyPublicMembersAndTypedError()
    {
        var error = new ResultError("domain.conflict", "trace-123");
        var result = Result.Failure("Domain message").SetError(error)
            .SetMetadata("request", "secret").SetMetadata("exception2", "secret")
            .SetMetadata("chain_error", "secret").SetMetadata("detail", "secret")
            .SetMetadata("availableActions", new[] { "cancel" });
        result.PrepareForPresentation(new JsonSerializerOptions(), new DefaultResultErrorMessageProvider(), "new-trace");
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret", json);
        Assert.Contains("availableActions", json);
        Assert.True(result.TryGetError(new JsonSerializerOptions(), out var actual));
        Assert.Same(error, actual);
    }

    [Fact]
    public void Presentation_WhenTypedJsonContainsUndeclaredDiagnostics_ShouldDiscardThem()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var document = JsonDocument.Parse("""{"code":"domain.failed","traceId":"origin","stackTrace":"secret"}""");
        var result = Result.Failure("Safe failure").SetMetadata("error", document.RootElement.Clone());
        result.PrepareForPresentation(json, new DefaultResultErrorMessageProvider(), "local");
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result, json));
        Assert.True(result.TryGetError(json, out var error));
        Assert.Equal("origin", error.TraceId);
    }

    [Fact]
    public void Presentation_WhenLegacyErrorProducerRemains_ShouldRejectMixedContract()
    {
        var result = Result.Failure("failure").SetMetadata("error", new { stackTrace = "secret" });
        Assert.Throws<InvalidOperationException>(() => result.PrepareForPresentation(
            new JsonSerializerOptions(), new DefaultResultErrorMessageProvider(), "trace"));
    }
}
