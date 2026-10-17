using System.Diagnostics.CodeAnalysis;
using Monica.Rpc.Models;

namespace Monica.Rpc.Abstractions;

/// <summary>Recognizes one provider's non-Monica JSON response without reading streams or retrying calls.</summary>
public interface IRemoteResponseClassifier
{
    /// <summary>Gets the transport key to which this classifier is exclusively bound.</summary>
    string Transport { get; }

    /// <summary>Recognizes provider-owned evidence; borrowed JSON must not be retained.</summary>
    bool TryClassify(RemoteResponseContext response, [NotNullWhen(true)] out RemoteCallFailure? failure);
}
