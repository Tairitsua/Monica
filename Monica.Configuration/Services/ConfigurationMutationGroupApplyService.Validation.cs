using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Monica.Configuration.Binding;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

internal sealed partial class ConfigurationMutationGroupApplyService
{
    private static readonly byte[] VALIDATION_FINGERPRINT_SALT = RandomNumberGenerator.GetBytes(32);
    private const int MAX_INDEPENDENT_SOURCES = 8;

    public async Task<ConfigurationMutationGroupValidationPreview> PreviewAsync(
        ConfigurationMutationGroupApplyRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        try { return (await BuildValidationPlanAsync(request, request.Context, cancellationToken, false)).Preview; }
        catch (ConfigurationValidationExecutionException exception)
        {
            return new ConfigurationMutationGroupValidationPreview
            {
                ValidationFingerprint = string.Empty,
                Definitions = [new ConfigurationCandidateValidationReport
                {
                    DefinitionKey = exception.DefinitionKey, DefinitionDisplayName = exception.DefinitionKey,
                    ScopePath = LogicalPath.Root, Scope = ConfigurationValidationScope.CompleteAggregate,
                    Coverage = ConfigurationValidationCoverage.Failed,
                    Issues = [new ConfigurationCandidateValidationIssue
                    {
                        DefinitionKey = exception.DefinitionKey, DefinitionDisplayName = exception.DefinitionKey,
                        LogicalPath = exception.LogicalPath, LogicalPaths = [exception.LogicalPath], Kind = exception.Kind,
                        NodeDisplayName = exception.DefinitionKey, Problem = exception.Message
                    }]
                }]
            };
        }
    }

    private async Task EnsureValidationBaselineAsync(ConfigurationMutationGroupApplyRequest request,
        ConfigurationMutationContext context, string fingerprint, CancellationToken cancellationToken)
    {
        var current = await BuildValidationPlanAsync(request, context, cancellationToken, true);
        if (!current.Preview.CanApply || !string.Equals(current.Preview.ValidationFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new ConfigurationConcurrencyConflictException("The complete configuration baseline changed before persistence.");
        }
    }

    private async Task<CompleteMutationValidationPlan> BuildValidationPlanAsync(
        ConfigurationMutationGroupApplyRequest request, ConfigurationMutationContext context,
        CancellationToken cancellationToken, bool throwOnExecutionFault)
    {
        var prepared = new List<PreparedConfigurationMutation>(request.Commands.Count);
        foreach (var command in request.Commands)
            prepared.Add(await mutationPlanner.PrepareAsync(command, context, cancellationToken));
        ValidateReviewedSourceChains(prepared);
        ValidateTargets(prepared);
        var root = runtimeContext.Root
            ?? throw new ConfigurationValidationFailedException("Complete mutation validation requires the host provider root.");
        var providers = root.Providers.ToArray();
        var sources = sourceInspector.GetSources();
        var definitions = prepared.Select(static mutation => mutation.Definition)
            .DistinctBy(static definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase).ToArray();
        var baselineBefore = CaptureRuntimeBaseline(providers, definitions);
        var evidence = new List<string> { baselineBefore, JsonSerializer.Serialize(request.Commands) };
        var reports = new List<ConfigurationCandidateValidationReport>();
        var problems = new List<ConfigurationMutationValidationProblem>();
        var providerBaselines = new List<ProviderAdoptionBaseline>();
        var physicalRevisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var physicalDocuments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var physicalProviders = new Dictionary<int, IConfigurationProvider>();
        var documentVersions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Where(static source => source.Kind == ConfigurationSourceKind.JsonFile
                     && !string.IsNullOrWhiteSpace(source.PhysicalPath)))
        {
            var snapshot = await sourceWriter.ReadPhysicalValuesAsync(source, [string.Empty], cancellationToken);
            var documentJson = snapshot.Values.GetValueOrDefault(string.Empty)?.Json ?? "{}";
            physicalDocuments[source.SourceKey] = documentJson;
            physicalRevisions[source.SourceKey] = snapshot.Revision;
            evidence.Add($"physical:{source.SourceKey}:{snapshot.Revision}");
            using var physicalDocument = JsonDocument.Parse(documentJson);
            var physicalProvider = new ConfigurationValueProjectionSource(
                ConfigurationValueProjectionFactory.CreateDocument(physicalDocument.RootElement)).Build(new ConfigurationBuilder());
            physicalProvider.Load();
            physicalProviders[source.PriorityIndex] = physicalProvider;
        }

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (index, physicalProvider) in physicalProviders)
            {
                if (index < 0 || index >= providers.Length
                    || !string.Equals(CaptureProviderFingerprint(physicalProvider, definition.SectionPath),
                        CaptureProviderFingerprint(providers[index], definition.SectionPath), StringComparison.Ordinal))
                    problems.Add(new ConfigurationMutationValidationProblem
                    {
                        Code = "RuntimeOutOfSync", DefinitionKey = definition.DefinitionKey,
                        Message = "A contributing JSON file differs from its loaded runtime values. Reload the configuration and review the complete candidate again."
                    });
            }
            var definitionMutations = prepared.Where(mutation =>
                string.Equals(mutation.Definition.DefinitionKey, definition.DefinitionKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            var candidates = new Dictionary<int, IConfigurationProvider>();
            var contributionIssues = new List<ConfigurationValueValidationIssue>();
            string? baselineEffectiveJson;
            try { baselineEffectiveJson = validationCoordinator.ReadEffectiveJson(definition, root); }
            catch (ConfigurationValidationExecutionException)
            {
                // An unbindable existing value must remain correctable. This comparison never certifies its validity;
                // every final stored/effective candidate is independently materialized and validated below.
                baselineEffectiveJson = null;
            }
            var observedDocument = await effectiveValueStore.GetAsync(definition.DefinitionKey, cancellationToken);
            var observedVersion = observedDocument?.Version ?? 0;
            documentVersions[definition.DefinitionKey] = observedVersion;
            if (definition.Origin == ConfigurationDefinitionOrigin.LocalScan)
            {
                var loadedVersion = reloadCoordinator.GetLoadedMonicaProjectionVersion(definition.DefinitionKey) ?? 0;
                var contributionMatches = loadedVersion == observedVersion;
                if (contributionMatches && observedDocument is not null)
                {
                    var storeSource = sources.SingleOrDefault(static source => source.Kind == ConfigurationSourceKind.MonicaEffectiveStore);
                    if (storeSource is null || storeSource.PriorityIndex < 0 || storeSource.PriorityIndex >= providers.Length)
                        contributionMatches = false;
                    else
                    {
                        var storedProvider = new ConfigurationValueProjectionSource(
                            validationCoordinator.ProjectStoredValue(definition, observedDocument.Json)).Build(new ConfigurationBuilder());
                        storedProvider.Load();
                        contributionMatches = string.Equals(
                            CaptureContributionFingerprint(storedProvider, definition.SectionPath),
                            CaptureContributionFingerprint(providers[storeSource.PriorityIndex], definition.SectionPath),
                            StringComparison.Ordinal);
                    }
                }
                if (!contributionMatches)
                    problems.Add(new ConfigurationMutationValidationProblem
                    {
                        Code = "RuntimeOutOfSync", DefinitionKey = definition.DefinitionKey,
                        Message = "A contributing effective-store document differs from its loaded runtime projection. Reload the configuration and review the complete candidate again."
                    });
            }
            foreach (var targetGroup in definitionMutations.GroupBy(mutation =>
                         mutation.Command.Target is ConfigurationExternalSourceMutationTarget external
                             ? external.SourceKey : "$effective", StringComparer.OrdinalIgnoreCase))
            {
                var targetMutations = targetGroup.ToArray();
                ConfigurationSourceDescriptor? source;
                string json;
                string? externalDocumentJson = null;
                if (targetMutations[0].Command.Target is ConfigurationEffectiveStoreMutationTarget)
                {
                    source = sources.SingleOrDefault(static descriptor => descriptor.Kind == ConfigurationSourceKind.MonicaEffectiveStore);
                    var document = observedDocument;
                    var version = document?.Version ?? 0;
                    documentVersions[definition.DefinitionKey] = version;
                    json = document?.Json ?? seedFactory.CreateSeedJson(definition);
                    evidence.Add(JsonSerializer.Serialize(new { definition.DefinitionKey, version, json }));
                    foreach (var mutation in targetMutations)
                    {
                        var target = (ConfigurationEffectiveStoreMutationTarget)mutation.Command.Target;
                        if (target.ExpectedVersion is not null && target.ExpectedVersion != version)
                            throw new ConfigurationConcurrencyConflictException("The effective-store document changed after review.");
                        ReplaceTarget(prepared, mutation, target with { ExpectedVersion = version });
                    }
                }
                else
                {
                    var target = (ConfigurationExternalSourceMutationTarget)targetMutations[0].Command.Target;
                    source = sourceInspector.GetRequiredSource(target.SourceKey);
                    if (sources.Count(descriptor => descriptor.Kind == ConfigurationSourceKind.JsonFile
                            && string.Equals(descriptor.PhysicalPath, source.PhysicalPath, StringComparison.OrdinalIgnoreCase)) > 1)
                        throw new ConfigurationValidationFailedException("The physical target has multiple independently reloaded provider aliases. Use one registered source.");
                    externalDocumentJson = physicalDocuments[source.SourceKey];
                    json = ConfigurationJsonFileSourceWriter.ReadSection(externalDocumentJson, definition.SectionPath)?.Json ?? "{}";
                    var physicalRevision = physicalRevisions[source.SourceKey];
                    evidence.Add(JsonSerializer.Serialize(new { source.SourceKey, physicalRevision, json }));
                    foreach (var mutation in targetMutations)
                    {
                        var observedTarget = (ConfigurationExternalSourceMutationTarget)mutation.Command.Target;
                        if (observedTarget.ExpectedRevision is not null && observedTarget.ExpectedRevision != physicalRevision)
                            throw new ConfigurationConcurrencyConflictException("The external source changed after review.");
                        ReplaceTarget(prepared, mutation, observedTarget with { ExpectedRevision = physicalRevision });
                    }
                }
                if (source is null || source.PriorityIndex < 0 || source.PriorityIndex >= providers.Length)
                {
                    problems.Add(new ConfigurationMutationValidationProblem
                    {
                        Code = "UnknownProviderPriority", DefinitionKey = definition.DefinitionKey,
                        Message = "The persistence target has no faithful position in the host provider stack."
                    });
                    continue;
                }
                ConfigurationStoredValue? externalCandidate = null;
                string? externalCandidateDocument = null;
                if (targetMutations[0].Command.Target is ConfigurationExternalSourceMutationTarget)
                {
                    var sourceKey = ((ConfigurationExternalSourceMutationTarget)targetMutations[0].Command.Target).SourceKey;
                    var sourceMutations = prepared.Where(mutation => mutation.Command.Target is ConfigurationExternalSourceMutationTarget target
                        && string.Equals(target.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
                    externalCandidateDocument = ConfigurationJsonFileSourceWriter.PreviewMutationBatch(
                        externalDocumentJson!, sourceMutations.Select(static mutation => new ConfigurationJsonFileMutation
                        {
                            ConfigurationPath = mutation.ConfigurationPath, MutationKind = mutation.Request.MutationKind, Value = mutation.Request.Value
                        }).ToArray());
                    externalCandidate = ConfigurationJsonFileSourceWriter.ReadSection(externalCandidateDocument, definition.SectionPath);
                    json = externalCandidate?.Json ?? "{}";
                }
                else
                    foreach (var mutation in targetMutations)
                        json = documentEditor.ApplyMutation(definition, json, mutation.Request);
                contributionIssues.AddRange(validationCoordinator.ValidateSourceContribution(definition, json));
                if (targetMutations[0].Command.Target is ConfigurationEffectiveStoreMutationTarget)
                {
                    try
                    {
                        var storedValidation = validationCoordinator.ValidateCompleteValue(definition, json);
                        reports.Add(ToCompleteCandidateReport(definition, storedValidation) with
                        {
                            Target = ConfigurationCandidateValidationTarget.StoredDocument
                        });
                    }
                    catch (ConfigurationValidationExecutionException exception) when (!throwOnExecutionFault)
                    {
                        reports.Add(ToExecutionCandidateReport(definition, exception) with
                            { Target = ConfigurationCandidateValidationTarget.StoredDocument });
                    }
                }
                var value = ConfigurationRegexTextCodec.NormalizeStoredValue(definition.Root, ConfigurationStoredValue.FromJson(json));
                using var documentValue = JsonDocument.Parse(value.Json);
                using var externalDocumentValue = externalCandidateDocument is null ? null : JsonDocument.Parse(externalCandidateDocument);
                var projection = externalDocumentValue is not null
                    ? ConfigurationValueProjectionFactory.CreateDocument(externalDocumentValue.RootElement)
                    : ConfigurationValueProjectionFactory.Create(definition.SectionPath, documentValue.RootElement);
                var replacement = new ConfigurationValueProjectionSource(projection).Build(new ConfigurationBuilder());
                replacement.Load();
                candidates.Add(source.PriorityIndex, replacement);
            }

            for (var index = 0; index < providers.Length; index++)
                providerBaselines.Add(new ProviderAdoptionBaseline(definition, index, providers[index],
                    CaptureProviderFingerprint(providers[index], definition.SectionPath),
                    candidates.TryGetValue(index, out var candidate)
                        ? CaptureProviderFingerprint(candidate, definition.SectionPath) : null));

            if (candidates.Count > MAX_INDEPENDENT_SOURCES)
            {
                problems.Add(new ConfigurationMutationValidationProblem
                {
                    Code = "UnprovableAdoption", DefinitionKey = definition.DefinitionKey,
                    Message = "This aggregate spans too many independent source boundaries to prove safe adoption."
                });
                continue;
            }
            var candidateEntries = candidates.ToArray();
            var fullMask = (1 << candidateEntries.Length) - 1;
            for (var mask = fullMask; mask > 0; mask--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var overlays = candidateEntries.Where((_, index) => (mask & (1 << index)) != 0)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                using var candidateRoot = new ConfigurationRoot(providers.Select((provider, index) =>
                    (IConfigurationProvider)new ValidationProviderView(provider, overlays.GetValueOrDefault(index),
                        candidates.ContainsKey(index) && sources.Any(source => source.PriorityIndex == index && source.Kind == ConfigurationSourceKind.JsonFile)
                            ? string.Empty : definition.SectionPath)).ToArray());
                ConfigurationCompleteValidationResult complete;
                try
                {
                    complete = validationCoordinator.ValidateConfiguration(definition, candidateRoot, ConfigurationValidationProfile.Mutation);
                }
                catch (ConfigurationValidationExecutionException exception) when (!throwOnExecutionFault)
                {
                    reports.Add(ToExecutionCandidateReport(definition, exception));
                    problems.Add(new ConfigurationMutationValidationProblem
                    {
                        Code = "ValidationExecution", DefinitionKey = definition.DefinitionKey,
                        Message = "The complete validation could not execute safely."
                    });
                    break;
                }
                if (mask == fullMask)
                {
                    reports.Add(new ConfigurationCandidateValidationReport
                    {
                        DefinitionKey = definition.DefinitionKey, DefinitionDisplayName = definition.DisplayName,
                        ScopePath = LogicalPath.Root, Scope = complete.Scope, Coverage = complete.Coverage,
                        ValidationRevision = complete.ValidationRevision,
                        HasEffectiveChange = baselineEffectiveJson is null || !ConfigurationJsonSemanticComparer.Equals(baselineEffectiveJson,
                            validationCoordinator.ReadEffectiveJson(definition, candidateRoot)),
                        Issues = complete.Issues.Concat(contributionIssues)
                            .Select(issue => ConfigurationCandidateValidationService.ToCandidateIssue(definition, issue)).ToArray()
                    });
                }
                else if (!complete.IsValid)
                {
                    problems.Add(new ConfigurationMutationValidationProblem
                    {
                        Code = "UnsafeIndependentAdoption", DefinitionKey = definition.DefinitionKey,
                        Message = "An independently observable source-adoption state violates the complete contract. Submit one atomic source change."
                    });
                    break;
                }
            }
        }
        if (!string.Equals(baselineBefore, CaptureRuntimeBaseline(providers, definitions), StringComparison.Ordinal))
            throw new ConfigurationConcurrencyConflictException("The host configuration changed while complete validation was being prepared.");
        var plan = new CompleteMutationValidationPlan(prepared, new ConfigurationMutationGroupValidationPreview
        {
            Definitions = reports, Problems = problems, ValidationFingerprint = Fingerprint(string.Join("\n", evidence))
        }, providerBaselines, providers.Length, physicalRevisions, documentVersions);
        await EnsureAdoptionBaselineAsync(plan, cancellationToken);
        return plan;
    }

    private async Task EnsureAdoptionBaselineAsync(CompleteMutationValidationPlan plan, CancellationToken cancellationToken)
    {
        var providers = runtimeContext.Root?.Providers.ToArray()
            ?? throw new ConfigurationConcurrencyConflictException("The host provider stack is no longer available.");
        if (providers.Length != plan.ProviderCount)
            throw new ConfigurationConcurrencyConflictException("The contributing provider stack changed after validation.");
        foreach (var baseline in plan.ProviderBaselines)
        {
            if (baseline.ProviderIndex >= providers.Length || !ReferenceEquals(providers[baseline.ProviderIndex], baseline.Provider))
                throw new ConfigurationConcurrencyConflictException("The contributing provider stack changed after validation.");
            var definition = await definitionResolver.GetRequiredAsync(baseline.Definition.DefinitionKey, cancellationToken);
            if (definition.SchemaHash != baseline.Definition.SchemaHash || definition.SchemaVersion != baseline.Definition.SchemaVersion
                || definition.SectionPath != baseline.Definition.SectionPath
                || definition.ValidationContract != baseline.Definition.ValidationContract)
                throw new ConfigurationConcurrencyConflictException("A contributing validation contract changed after validation.");
            var current = CaptureProviderFingerprint(providers[baseline.ProviderIndex], definition.SectionPath);
            if (!string.Equals(current, baseline.OriginalFingerprint, StringComparison.Ordinal)
                && !string.Equals(current, baseline.CandidateFingerprint, StringComparison.Ordinal))
                throw new ConfigurationConcurrencyConflictException("A contributing value changed outside the validated adoption states.");
        }
        foreach (var (key, version) in plan.DocumentVersions)
        {
            if (((await effectiveValueStore.GetAsync(key, cancellationToken))?.Version ?? 0) != version)
                throw new ConfigurationConcurrencyConflictException("A contributing effective-store document changed after validation.");
        }
        foreach (var (key, revision) in plan.PhysicalRevisions)
        {
            var source = sourceInspector.GetRequiredSource(key);
            if (!string.Equals(await sourceWriter.GetRevisionAsync(source, cancellationToken), revision, StringComparison.Ordinal))
                throw new ConfigurationConcurrencyConflictException("A contributing physical source changed after validation.");
        }
    }

    private static void ReplaceTarget(List<PreparedConfigurationMutation> prepared,
        PreparedConfigurationMutation mutation, ConfigurationMutationTarget target)
    {
        var index = prepared.IndexOf(mutation);
        prepared[index] = mutation with { Command = mutation.Command with { Target = target } };
    }

    private static ConfigurationCandidateValidationReport ToCompleteCandidateReport(ConfigurationDefinition definition,
        ConfigurationCompleteValidationResult complete) => new()
    {
        DefinitionKey = definition.DefinitionKey, DefinitionDisplayName = definition.DisplayName,
        ScopePath = LogicalPath.Root, Scope = complete.Scope, Coverage = complete.Coverage,
        ValidationRevision = complete.ValidationRevision,
        Issues = complete.Issues.Select(issue => ConfigurationCandidateValidationService.ToCandidateIssue(definition, issue)).ToArray()
    };

    private static ConfigurationCandidateValidationReport ToExecutionCandidateReport(ConfigurationDefinition definition,
        ConfigurationValidationExecutionException exception) => new()
    {
        DefinitionKey = definition.DefinitionKey, DefinitionDisplayName = definition.DisplayName,
        ScopePath = LogicalPath.Root, Scope = ConfigurationValidationScope.CompleteAggregate,
        Coverage = ConfigurationValidationCoverage.Failed, ValidationRevision = definition.ValidationContract.Revision,
        Issues = [new ConfigurationCandidateValidationIssue
        {
            DefinitionKey = definition.DefinitionKey, DefinitionDisplayName = definition.DisplayName,
            LogicalPath = exception.LogicalPath, LogicalPaths = [exception.LogicalPath], Kind = exception.Kind,
            NodeDisplayName = definition.DisplayName, Problem = exception.Message
        }]
    };

    private static string CaptureRuntimeBaseline(IReadOnlyList<IConfigurationProvider> providers,
        IReadOnlyList<ConfigurationDefinition> definitions)
    {
        var entries = new List<string>();
        foreach (var definition in definitions)
        {
            entries.Add(JsonSerializer.Serialize(new { definition.DefinitionKey, definition.SchemaVersion,
                definition.SchemaHash, definition.ValidationContract }));
            for (var index = 0; index < providers.Count; index++)
            {
                entries.Add($"{index}:{providers[index].GetType().FullName}");
                entries.Add(CaptureProviderFingerprint(providers[index], definition.SectionPath));
            }
        }
        return Fingerprint(string.Join("\n", entries));
    }

    private static string CaptureProviderFingerprint(IConfigurationProvider provider, string path)
    {
        if (provider is ChainedConfigurationProvider) provider = new ValidationProviderView(provider, null, string.Empty);
        var entries = new List<string>();
        var ancestor = path;
        while (ancestor.Length > 0)
        {
            var separator = ancestor.LastIndexOf(':');
            ancestor = separator < 0 ? string.Empty : ancestor[..separator];
            if (provider.TryGet(ancestor, out var value)) entries.Add(JsonSerializer.Serialize(new { ancestor, value }));
            if (provider is IConfigurationValueShapeProvider shapes && shapes.TryGetShape(ancestor, out var shape))
                entries.Add($"ancestor-shape:{ancestor}:{shape}");
        }
        CaptureProviderValues(provider, path, entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return Fingerprint(string.Join("\n", entries));
    }

    private static string CaptureContributionFingerprint(IConfigurationProvider provider, string path)
    {
        // The Monica provider merges multiple definition projections. Ancestors can belong to another
        // definition; this comparison owns only this document's subtree. Adoption guards still freeze ancestors.
        var entries = new List<string>();
        CaptureProviderValues(provider, path, entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return Fingerprint(string.Join("\n", entries));
    }

    private static void CaptureProviderValues(IConfigurationProvider provider, string path,
        ICollection<string> entries, ISet<string> visited)
    {
        if (!visited.Add(path)) throw new ConfigurationValidationFailedException("A provider exposes a cyclic configuration tree.");
        if (provider.TryGet(path, out var value)) entries.Add(JsonSerializer.Serialize(new { path, value }));
        if (provider is IConfigurationValueShapeProvider shapes && shapes.TryGetShape(path, out var shape))
            entries.Add($"shape:{path}:{shape}");
        foreach (var key in provider.GetChildKeys([], string.IsNullOrEmpty(path) ? null : path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            CaptureProviderValues(provider, string.IsNullOrEmpty(path) ? key : $"{path}:{key}", entries, visited);
    }

    private static string Fingerprint(string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(VALIDATION_FINGERPRINT_SALT);
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private sealed record CompleteMutationValidationPlan(
        IReadOnlyList<PreparedConfigurationMutation> Mutations, ConfigurationMutationGroupValidationPreview Preview,
        IReadOnlyList<ProviderAdoptionBaseline> ProviderBaselines, int ProviderCount,
        Dictionary<string, string> PhysicalRevisions, Dictionary<string, long> DocumentVersions);

    private sealed record ProviderAdoptionBaseline(ConfigurationDefinition Definition, int ProviderIndex,
        IConfigurationProvider Provider, string OriginalFingerprint, string? CandidateFingerprint);

    // The candidate root owns these wrappers, never the production providers or their reload lifecycle.
    private sealed class ValidationProviderView(IConfigurationProvider original, IConfigurationProvider? replacement, string prefix)
        : IConfigurationProvider, IConfigurationValueShapeProvider
    {
        private readonly ConfigurationShapeView? _chainedView = original is ChainedConfigurationProvider
            { Configuration: IConfigurationRoot chained } ? new ConfigurationShapeView(chained) : null;
        private bool IsTarget(string key) => prefix.Length == 0 || key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase);
        public bool TryGet(string key, out string? value)
        {
            if (replacement is not null && IsTarget(key)) return replacement.TryGet(key, out value);
            if (_chainedView is null) return original.TryGet(key, out value);
            if (_chainedView.TryGetShape(key, out _))
            {
                value = _chainedView.GetSection(key).Value;
                return true;
            }
            value = null;
            return false;
        }
        public bool TryGetShape(string path, out ConfigurationValueShape shape)
        {
            var provider = replacement is not null && IsTarget(path) ? replacement : original;
            if (provider is IConfigurationValueShapeProvider shapes) return shapes.TryGetShape(path, out shape);
            if (ReferenceEquals(provider, original) && _chainedView is not null) return _chainedView.TryGetShape(path, out shape);
            shape = default;
            return false;
        }
        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) =>
            replacement is not null && IsTarget(parentPath ?? string.Empty)
                ? replacement.GetChildKeys(earlierKeys, parentPath)
                : _chainedView is not null
                    ? _chainedView.GetSection(parentPath ?? string.Empty).GetChildren().Select(static child => child.Key)
                        .Concat(earlierKeys).Order(ConfigurationKeyComparer.Instance)
                    : original.GetChildKeys(earlierKeys, parentPath);
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
        public void Load() { }
        public void Set(string key, string? value) => throw new NotSupportedException("Validation views are read-only.");
    }
}
