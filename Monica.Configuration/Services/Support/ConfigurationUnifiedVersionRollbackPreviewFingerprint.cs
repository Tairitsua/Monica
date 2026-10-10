using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Binds a unified-version rollback request to the exact values, schema, destinations, and concurrency revisions
/// shown in its preview.
/// </summary>
internal static class ConfigurationUnifiedVersionRollbackPreviewFingerprint
{
    public static string Compute(
        long version,
        IReadOnlyList<ConfigurationUnifiedVersionApplyTarget> targets)
    {
        // Keep this projection explicit: display-only or computed model properties must not silently alter the
        // optimistic-concurrency contract.
        var input = JsonSerializer.Serialize(new
        {
            Version = version,
            Targets = targets.Select(static target => new
            {
                target.DefinitionKey,
                target.CurrentJson,
                target.TargetJson,
                target.CapturedSchemaHash,
                target.CurrentSchemaHash,
                target.CurrentSchemaVersion,
                ValidationRevision = target.ValidationReport?.ValidationRevision,
                ValidationCoverage = target.ValidationReport?.Coverage,
                target.Status,
                Mutations = target.Mutations.Select(static mutation => new
                {
                    mutation.LogicalPath,
                    mutation.ConfigurationPath,
                    mutation.MutationKind,
                    mutation.CurrentJson,
                    mutation.TargetJson,
                    mutation.SourceKey,
                    mutation.SourceKind,
                    mutation.BlockingSourceDisplayName,
                    mutation.ExpectedValueVersion,
                    mutation.ExpectedSourceRevision,
                    mutation.ExpectedSourceChainRevision,
                    mutation.Status
                }),
                Issues = target.ValidationIssues.Select(static issue => new
                {
                    issue.LogicalPath,
                    issue.Message,
                    issue.DetailsHidden
                })
            })
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
