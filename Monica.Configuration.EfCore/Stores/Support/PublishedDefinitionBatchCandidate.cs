using Monica.Configuration.EfCore.Entities;
using Monica.Configuration.Models;

namespace Monica.Configuration.EfCore.Stores.Support;

/// <summary>
/// Owns the normalized publisher-state and definition candidates for one complete publication batch.
/// </summary>
internal sealed class PublishedDefinitionBatchCandidate
{
    private readonly IReadOnlyDictionary<string, PublishedDefinitionCandidate> _definitionsByIdentity;

    private PublishedDefinitionBatchCandidate(
        ConfigurationPublisherIdentity publisher,
        string publisherIdentity,
        IReadOnlyList<PublishedDefinitionPublisherStateCandidate> publisherStates,
        IReadOnlyDictionary<string, PublishedDefinitionCandidate> definitionsByIdentity,
        IReadOnlySet<string> excludedDefinitionIdentities)
    {
        Publisher = publisher;
        PublisherIdentity = publisherIdentity;
        PublisherStates = publisherStates;
        _definitionsByIdentity = definitionsByIdentity;
        ExcludedDefinitionIdentities = excludedDefinitionIdentities;
    }

    internal ConfigurationPublisherIdentity Publisher { get; }

    internal string PublisherIdentity { get; }

    internal IReadOnlyList<PublishedDefinitionPublisherStateCandidate> PublisherStates { get; }

    internal IReadOnlySet<string> ExcludedDefinitionIdentities { get; }

    internal IEnumerable<string> DefinitionIdentities => _definitionsByIdentity.Keys;

    internal IReadOnlyList<PublishedDefinitionCandidate> Definitions => _definitionsByIdentity.Values.ToArray();

    internal static PublishedDefinitionBatchCandidate Create(ConfigurationDefinitionPublicationBatch batch)
    {
        var publisherIdentity = PublishedDefinitionPublisherIdentity.Compute(batch.Publisher.PublisherKey);
        var publisherStates = new PublishedDefinitionPublisherStateCandidate[batch.Publications.Count];
        var definitionsByIdentity = new Dictionary<string, PublishedDefinitionCandidate>(
            batch.Publications.Count,
            StringComparer.Ordinal);

        for (var index = 0; index < batch.Publications.Count; index++)
        {
            var publication = batch.Publications[index];
            var definitionIdentity = ConfigurationDefinitionIdentity.Compute(publication.Definition.DefinitionKey);
            var publisherState = PublishedDefinitionPublisherStateCandidate.FromPublication(
                batch.Publisher,
                publisherIdentity,
                definitionIdentity,
                publication);
            publisherStates[index] = publisherState;
            definitionsByIdentity.Add(
                publisherState.DefinitionIdentity,
                PublishedDefinitionCandidate.FromPublication(
                    publication,
                    definitionIdentity,
                    ConfigurationReloadBehaviorObservation.Aggregate([publication.ReloadBehaviorObservation])));
        }

        return new PublishedDefinitionBatchCandidate(
            batch.Publisher,
            publisherIdentity,
            publisherStates,
            definitionsByIdentity,
            batch.ExcludedDefinitionKeys.Select(ConfigurationDefinitionIdentity.Compute).ToHashSet(StringComparer.Ordinal));
    }

    internal bool MatchesCurrentPublisherAndCanonicalEnvelope(
        IReadOnlyList<ConfigurationDefinitionPublisherStateEntity> currentPublisherStates,
        IReadOnlyList<ConfigurationDefinitionEntity> currentDefinitions)
    {
        if (currentPublisherStates.Count != PublisherStates.Count
            || currentDefinitions.Count != _definitionsByIdentity.Count)
        {
            return false;
        }

        var statesByIdentity = currentPublisherStates.ToDictionary(
            static state => state.DefinitionIdentity,
            StringComparer.Ordinal);
        if (PublisherStates.Any(candidate =>
                !statesByIdentity.TryGetValue(candidate.DefinitionIdentity, out var current)
                || !candidate.Matches(current)))
        {
            return false;
        }

        var definitionsByIdentity = currentDefinitions.ToDictionary(
            static definition => definition.DefinitionIdentity,
            StringComparer.Ordinal);
        foreach (var (definitionIdentity, candidate) in _definitionsByIdentity)
        {
            if (!definitionsByIdentity.TryGetValue(definitionIdentity, out var current)
                || !Enum.TryParse<ConfigurationReloadBehavior>(current.ReloadBehavior, out var currentReloadBehavior)
                || !Enum.IsDefined(currentReloadBehavior)
                || !candidate.MatchesEnvelopeIgnoringReloadBehavior(current))
            {
                return false;
            }
        }

        // Effective reload behavior aggregates every publisher. Recomputing it requires the cross-publisher query this
        // warm path intentionally skips, so a matching canonical envelope trusts the already persisted aggregate.
        return true;
    }

    internal PublishedDefinitionCandidate GetDefinition(
        string definitionIdentity,
        ConfigurationReloadBehavior effectiveReloadBehavior)
    {
        return _definitionsByIdentity[definitionIdentity] with
        {
            ReloadBehavior = effectiveReloadBehavior.ToString()
        };
    }

    internal bool ContainsDefinition(string definitionIdentity)
    {
        return _definitionsByIdentity.ContainsKey(definitionIdentity);
    }
}
