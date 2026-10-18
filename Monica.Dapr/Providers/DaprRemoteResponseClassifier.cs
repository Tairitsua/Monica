using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Monica.Core.Results;
using Monica.Invocation.Abstractions;
using Monica.Invocation.Models;

namespace Monica.Dapr.Providers;

internal sealed class DaprRemoteResponseClassifier : IRemoteResponseClassifier
{
    public string Transport => "dapr";

    public bool TryClassify(RemoteResponseContext response, [NotNullWhen(true)] out RemoteCallFailure? failure)
    {
        failure = null;
        if (response.Body.ValueKind != JsonValueKind.Object ||
            !response.Body.TryGetProperty("errorCode", out var code) ||
            code.ValueKind != JsonValueKind.String || code.GetString() != "ERR_DIRECT_INVOKE") return false;

        failure = new RemoteCallFailure(ResStatus.ServiceUnavailable, ResultErrorCodes.DependencyUnavailable,
            "dapr_invocation", response.StatusCode);
        return true;
    }
}
