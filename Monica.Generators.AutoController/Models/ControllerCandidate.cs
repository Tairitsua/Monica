using System;
using System.Collections.Immutable;
using System.Linq;

namespace Monica.Generators.AutoController.Models;

internal sealed class ControllerCandidate : IEquatable<ControllerCandidate>
{
    public ControllerCandidate(EndpointModel endpoint, ImmutableArray<string> tags, bool applicationReceiver = false)
    {
        Endpoint = endpoint;
        Tags = tags;
        ApplicationReceiver = applicationReceiver;
    }

    public EndpointModel Endpoint { get; }

    public ImmutableArray<string> Tags { get; }

    public bool ApplicationReceiver { get; }

    public bool Equals(ControllerCandidate? other)
    {
        return other is not null &&
               Endpoint.Equals(other.Endpoint) &&
               ApplicationReceiver == other.ApplicationReceiver &&
               Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is ControllerCandidate other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = Endpoint.GetHashCode();
            hashCode = (hashCode * 397) ^ ApplicationReceiver.GetHashCode();
            foreach (var tag in Tags)
            {
                hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(tag);
            }

            return hashCode;
        }
    }
}
