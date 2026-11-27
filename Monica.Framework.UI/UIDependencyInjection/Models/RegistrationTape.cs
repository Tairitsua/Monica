using Microsoft.Extensions.DependencyInjection;
using Monica.DependencyInjection.Models;

namespace Monica.Framework.UI.UIDependencyInjection.Models;

internal sealed class RegistrationTape(DependencyInjectionWorkbenchSnapshot snapshot)
{
    public sealed record Segment(int From, int To, string Family, bool Mixed, int Conventional, int[] Lifetimes)
    {
        public int Count => To - From + 1;
    }

    public sealed record Arc(int From, int To, string Type, string Display, ConventionFinding Reason);

    public Segment[] Segments { get; } = BuildSegments(snapshot.Descriptors);
    public Arc[] Arcs { get; } = snapshot.Implementations
        .SelectMany(i => i.Exposures
            .Where(HasArc)
            .Select(e =>
            {
                var from = e.Own!;
                if (from.Index == e.Winner!.Index)
                {
                    from = e.Identity!.Descriptors.First(d => d.Index != e.Winner.Index &&
                        DependencyInjectionWorkbenchSnapshot.NamesImplementation(d, i.Type));
                }

                return new Arc(from.Index, e.Winner.Index, i.Type, i.Display, e.Finding);
            })).ToArray();

    public ConventionImplementation[] Missing { get; } = snapshot.Implementations.Where(i => i.Matches(ConventionFinding.NotRegistered)).ToArray();
    public int[] Advisory { get; } = snapshot.Descriptors.Where(d => d.HasWarningIssues).Select(d => d.Index).ToArray();
    public int[] Replaced { get; } = snapshot.Descriptors.Where(d =>
        d.IsDirectConventionMatch && d.AutoRegistration?.Outcome == DependencyInjectionAutoRegistrationOutcome.ReplacedExisting).Select(d => d.Index).ToArray();
    public (int From, int To)[] Conventional { get; } = Runs(snapshot.Descriptors.Where(d => d.IsAutoRegistered));
    public (int From, int To)[][] Lifetimes { get; } = DependencyInjectionPresentation.Lifetimes
        .Select(l => Runs(snapshot.Descriptors.Where(d => d.Lifetime == l))).ToArray();

    internal static bool HasArc(ConventionImplementation implementation) => implementation.Exposures.Any(HasArc);

    private static bool HasArc(Exposure exposure)
        => exposure.Finding != ConventionFinding.Attention && exposure.Own is not null && exposure.Winner is not null;

    private static (int From, int To)[] Runs(IEnumerable<DependencyInjectionDescriptorInfo> descriptors)
    {
        var runs = new List<(int From, int To)>();
        foreach (var descriptor in descriptors)
        {
            if (runs.Count > 0 && runs[^1].To + 1 == descriptor.Index)
            {
                runs[^1] = (runs[^1].From, descriptor.Index);
            }
            else
            {
                runs.Add((descriptor.Index, descriptor.Index));
            }
        }

        return runs.ToArray();
    }

    private static Segment[] BuildSegments(DependencyInjectionDescriptorInfo[] descriptors)
    {
        var runs = new List<(int From, int To, string Family)>();
        for (var from = 0; from < descriptors.Length;)
        {
            var family = FamilyOf(descriptors[from]);
            var next = from + 1;
            while (next < descriptors.Length && FamilyOf(descriptors[next]) == family)
            {
                next++;
            }

            runs.Add((from, next - 1, family));
            from = next;
        }

        var floor = Math.Max(8, (int)Math.Round(descriptors.Length * .025, MidpointRounding.AwayFromZero));
        var folded = new List<(int From, int To)>();
        foreach (var run in runs)
        {
            if (folded.Count > 0 && (run.To - run.From + 1 < floor || folded[^1].To - folded[^1].From + 1 < floor))
            {
                folded[^1] = (folded[^1].From, run.To);
            }
            else
            {
                folded.Add((run.From, run.To));
            }
        }

        var segments = new List<Segment>();
        foreach (var (from, to) in folded)
        {
            var current = Build(from, to);
            if (segments.Count > 0 && segments[^1].Family == current.Family)
            {
                segments[^1] = Build(segments[^1].From, to);
            }
            else
            {
                segments.Add(current);
            }
        }

        return segments.ToArray();

        Segment Build(int from, int to)
        {
            var slice = descriptors[from..(to + 1)];
            var dominant = slice.GroupBy(FamilyOf).OrderByDescending(g => g.Count()).First();
            return new(slice[0].Index, slice[^1].Index, dominant.Key, dominant.Count() < slice.Length * .8,
                slice.Count(d => d.IsAutoRegistered),
                DependencyInjectionPresentation.Lifetimes.Select(l => slice.Count(d => d.Lifetime == l)).ToArray());
        }
    }

    private static string FamilyOf(DependencyInjectionDescriptorInfo d)
    {
        var service = d.ServiceType;
        var subject = d.ImplementationType ?? service;
        if (service.StartsWith("Microsoft.Extensions.Options.", StringComparison.Ordinal))
        {
            var generic = service.IndexOf('<');
            if (generic >= 0 && service.EndsWith('>'))
            {
                subject = service[(generic + 1)..^1];
            }
        }

        var dot = subject.IndexOf('.');
        return dot > 0 ? subject[..dot] : subject;
    }
}
