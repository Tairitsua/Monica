using Microsoft.EntityFrameworkCore;
using Monica.Configuration.EfCore.DbContext;
using Monica.Configuration.EfCore.Entities;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.EfCore.Stores.Support;

/// <summary>Checks publication evidence while the caller holds the publication lock through commit.</summary>
internal static class ConfigurationPublishedWriteGuard
{
    internal static async Task ValidateAsync(ConfigurationDbContext context, ConfigurationEffectiveValueSaveRequest request,
        ConfigurationEffectiveValueEntity? document, CancellationToken cancellationToken)
    {
        if (request.PublishedDefinitionExpectation is not { } expectation) return;
        var identity = ConfigurationDefinitionIdentity.Compute(request.Definition.DefinitionKey);
        var definition = await context.ConfigurationDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.DefinitionIdentity == identity, cancellationToken);
        var active = await context.ConfigurationDefinitionPublisherStates.AsNoTracking()
            .AnyAsync(candidate => candidate.DefinitionIdentity == identity, cancellationToken);
        var entry = definition is null ? null : DatabaseConfigurationMetadataStore.ToPublishedDefinitionEntry(definition,
            active ? ConfigurationDefinitionLifecycleState.Active : ConfigurationDefinitionLifecycleState.Retired);
        expectation.Validate(entry, document is null ? null : ConfigurationEffectiveValueMapper.ToDocument(document));
        if (!ConfigurationPublishedDefinitionExpectation.Matches(request.Definition, expectation.Definition))
            throw new ConfigurationConcurrencyConflictException("The submitted definition does not match the reviewed publication.");
    }
}
