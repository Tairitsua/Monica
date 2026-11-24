using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Framework.Seeder.Models;

namespace Monica.Framework.UI.UISeeder.Support;

internal sealed class SeederGraphView
{
    private readonly Dictionary<string, int> _waves = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SeederExecutionSnapshot>> _dependents = new(StringComparer.Ordinal);

    public SeederGraphView(SeederStateSnapshot run)
    {
        Run = run;
        ByType = run.Seeders.ToDictionary(seeder => seeder.SeederTypeName, StringComparer.Ordinal);
        foreach (var seeder in run.Seeders)
        {
            GetWave(seeder);
            foreach (var prerequisite in seeder.Dependencies)
            {
                if (!_dependents.TryGetValue(prerequisite, out var dependents))
                {
                    _dependents.Add(prerequisite, dependents = []);
                }

                dependents.Add(seeder);
            }
        }

        var ordered = run.Seeders.OrderBy(GetWave)
            .ThenBy(seeder => seeder.StartedAtUtc ?? seeder.CompletedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(seeder => seeder.SeederTypeName, StringComparer.Ordinal);
        Lanes = ordered.Where(seeder => !IsOutsideBatch(seeder)).ToArray();
        Outside = ordered.Where(IsOutsideBatch).OrderBy(seeder => seeder.Status == SeederStatus.Disabled).ToArray();
        OverrideCount = run.Seeders.Count(HasOverride);
    }

    public SeederStateSnapshot Run { get; }
    public IReadOnlyDictionary<string, SeederExecutionSnapshot> ByType { get; }
    public IReadOnlyList<SeederExecutionSnapshot> Lanes { get; }
    public IReadOnlyList<SeederExecutionSnapshot> Outside { get; }
    public int OverrideCount { get; }

    public static bool IsOutsideBatch(SeederExecutionSnapshot seeder) =>
        seeder.Status == SeederStatus.Disabled ||
        seeder.Status == SeederStatus.Held && !seeder.ParticipatesInReadiness;

    public static bool HasOverride(SeederExecutionSnapshot seeder) =>
        seeder.ExecutionModeSource == SeederPolicySource.SeederOverride ||
        seeder.CriticalitySource == SeederPolicySource.SeederOverride ||
        seeder.FailureBehaviorSource == SeederPolicySource.SeederOverride ||
        seeder.MaxAttemptsSource == SeederPolicySource.SeederOverride;

    public int GetWave(SeederExecutionSnapshot seeder)
    {
        if (_waves.TryGetValue(seeder.SeederTypeName, out var wave))
        {
            return wave;
        }

        foreach (var name in seeder.Dependencies)
        {
            if (ByType.TryGetValue(name, out var prerequisite))
            {
                wave = Math.Max(wave, GetWave(prerequisite) + 1);
            }
        }

        _waves.Add(seeder.SeederTypeName, wave);
        return wave;
    }

    public IReadOnlyList<SeederExecutionSnapshot> GetDependents(string typeName) =>
        _dependents.GetValueOrDefault(typeName) ?? [];

    public IEnumerable<SeederExecutionSnapshot> GetPrerequisites(SeederExecutionSnapshot seeder) =>
        seeder.Dependencies.Select(name => ByType.GetValueOrDefault(name)).OfType<SeederExecutionSnapshot>();

    public IEnumerable<SeederExecutionSnapshot> GetBlockingPrerequisites(SeederExecutionSnapshot seeder) =>
        GetPrerequisites(seeder).Where(prerequisite => Blocks(prerequisite, seeder));

    public IEnumerable<SeederExecutionSnapshot> GetBlockedDescendants(SeederExecutionSnapshot seeder) =>
        Traverse(seeder, current => GetDependents(current.SeederTypeName).Where(dependent => Blocks(current, dependent)));

    public HashSet<string> GetUpstream(SeederExecutionSnapshot seeder) =>
        Traverse(seeder, GetPrerequisites).Select(node => node.SeederTypeName).ToHashSet(StringComparer.Ordinal);

    public HashSet<string> GetDownstream(SeederExecutionSnapshot seeder) =>
        Traverse(seeder, current => GetDependents(current.SeederTypeName))
            .Select(node => node.SeederTypeName).ToHashSet(StringComparer.Ordinal);

    public IEnumerable<SeederAttemptSnapshot> GetBatchAttempts(SeederExecutionSnapshot seeder) =>
        seeder.AttemptHistory.Where(attempt => IsInBatch(attempt.StartedAtUtc));

    public bool IsInBatch(DateTimeOffset? instant) =>
        Run.StartedAtUtc is { } start && instant is { } time &&
        time >= start && time <= (Run.CompletedAtUtc ?? Run.CapturedAtUtc);

    public SeederRunPreview Preview(SeederExecutionSnapshot target)
    {
        var included = new Dictionary<string, SeederExecutionSnapshot>(StringComparer.Ordinal);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        Include(target);
        return new SeederRunPreview(
            target.SeederTypeName,
            included.Values.OrderBy(GetWave).ThenBy(seeder => seeder.SeederTypeName, StringComparer.Ordinal).ToArray(),
            unavailable.Order(StringComparer.Ordinal).ToArray());

        void Include(SeederExecutionSnapshot seeder)
        {
            if (!included.TryAdd(seeder.SeederTypeName, seeder))
            {
                return;
            }

            if (seeder.Status == SeederStatus.Disabled || seeder.Activation == ActivationMode.Disabled)
            {
                unavailable.Add(seeder.SeederTypeName);
            }

            foreach (var name in seeder.Dependencies)
            {
                if (!ByType.TryGetValue(name, out var prerequisite))
                {
                    unavailable.Add(name);
                }
                else if (prerequisite.Status != SeederStatus.Succeeded)
                {
                    Include(prerequisite);
                }
            }
        }
    }

    private static bool Blocks(SeederExecutionSnapshot prerequisite, SeederExecutionSnapshot dependent) =>
        dependent.Status == SeederStatus.Blocked &&
        prerequisite.Status is SeederStatus.Failed or SeederStatus.Blocked or SeederStatus.Cancelled &&
        prerequisite.CompletedAtUtc is { } failedAt && dependent.CompletedAtUtc >= failedAt;

    private static IEnumerable<SeederExecutionSnapshot> Traverse(
        SeederExecutionSnapshot start,
        Func<SeederExecutionSnapshot, IEnumerable<SeederExecutionSnapshot>> next)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { start.SeederTypeName };
        var pending = new Queue<SeederExecutionSnapshot>(next(start));
        while (pending.TryDequeue(out var seeder))
        {
            if (!visited.Add(seeder.SeederTypeName))
            {
                continue;
            }

            yield return seeder;
            foreach (var neighbor in next(seeder))
            {
                pending.Enqueue(neighbor);
            }
        }
    }
}

internal sealed record SeederRunPreview(
    string TargetTypeName,
    IReadOnlyList<SeederExecutionSnapshot> Seeders,
    IReadOnlyList<string> Unavailable)
{
    public bool Matches(SeederRunPreview other) =>
        TargetTypeName == other.TargetTypeName &&
        Seeders.Select(seeder => seeder.SeederTypeName)
            .SequenceEqual(other.Seeders.Select(seeder => seeder.SeederTypeName), StringComparer.Ordinal);
}
