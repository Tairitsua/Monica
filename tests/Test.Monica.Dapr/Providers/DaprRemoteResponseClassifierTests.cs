using System.Net;
using System.Text.Json;
using Monica.Core.Results;
using Monica.Dapr.Providers;
using Monica.Rpc.Models;
using Xunit;

namespace Test.Monica.Dapr.Providers;

public sealed class DaprRemoteResponseClassifierTests
{
    [Fact]
    public void Classify_WhenDaprReportsDirectInvocationFailure_ShouldUseStructuredEvidenceOnly()
    {
        using var json = JsonDocument.Parse("""{"errorCode":"ERR_DIRECT_INVOKE","message":"secret provider prose"}""");
        var classifier = new DaprRemoteResponseClassifier();
        Assert.Equal("dapr", classifier.Transport);
        Assert.True(classifier.TryClassify(new RemoteResponseContext(HttpStatusCode.InternalServerError, json.RootElement), out var failure));
        Assert.Equal(ResStatus.ServiceUnavailable, failure!.Status);
        Assert.Equal("dependency.unavailable", failure.Code);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(failure));
    }

    [Theory]
    [InlineData("""{"code":500,"message":"domain failure"}""")]
    [InlineData("""{"errorCode":"UNKNOWN","message":"failed to invoke"}""")]
    [InlineData("""{"errorCode":12}""")]
    public void Classify_WhenErrorIsNotProviderOwned_ShouldDecline(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.False(new DaprRemoteResponseClassifier().TryClassify(
            new RemoteResponseContext(HttpStatusCode.InternalServerError, json.RootElement), out _));
    }
}
