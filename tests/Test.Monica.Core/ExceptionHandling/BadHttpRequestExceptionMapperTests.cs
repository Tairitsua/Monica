using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Monica.Core.ExceptionHandling.Services;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Core.Results;
using Monica.Core.Results.Services;
using Xunit;

namespace Test.Monica.Core.ExceptionHandling;

public sealed class BadHttpRequestExceptionMapperTests
{
    [Fact]
    public void TryMap_JsonBindingFailure_ReturnsSafeBadRequest()
    {
        var mapper = CreateMapper();
        var exception = new BadHttpRequestException(
            "Failed to read the request body.",
            new JsonException("Required properties including 'expectedValueVersion' were missing."));

        var mapped = mapper.TryMap(null, exception, CancellationToken.None, out var response);

        mapped.Should().BeTrue();
        response.Should().NotBeNull();
        response!.Status.Should().Be(ResultStatus.BadRequest);
        response.Message.Should().NotContain("expectedValueVersion");
    }

    [Fact]
    public void TryMap_NonBindingException_DoesNotHandleIt()
    {
        var mapper = CreateMapper();

        var mapped = mapper.TryMap(null, new InvalidOperationException("failure"), CancellationToken.None, out var response);

        mapped.Should().BeFalse();
        response.Should().BeNull();
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, ResultStatus.PayloadTooLarge)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, ResultStatus.UnsupportedMediaType)]
    public void TryMap_NonBadRequestStatus_PreservesHttpStatus(int statusCode, ResultStatus expectedStatus)
    {
        var mapper = CreateMapper();
        var exception = new BadHttpRequestException("The request cannot be processed.", statusCode);

        var mapped = mapper.TryMap(null, exception, CancellationToken.None, out var response);

        mapped.Should().BeTrue();
        response.Should().NotBeNull();
        response!.Status.Should().Be(expectedStatus);
        response.ToHttpStatusCode().Should().Be((HttpStatusCode)statusCode);
    }

    private static BadHttpRequestExceptionMapper CreateMapper() => new(new RequestRejectionFactory(
        new JsonSerializerOptionsProvider(new JsonSerializerOptions(JsonSerializerDefaults.Web), DateTimeWireFormat.Iso8601WallClock),
        new DefaultResultErrorMessageProvider()));
}
