using Monica.Locker.Leases;

namespace Monica.Locker.EfCore.Internal;

internal sealed class LeaseRecord
{
    public string Scope { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string HolderId { get; set; } = null!;
    public string TermId { get; set; } = null!;
    public long AcquiredAtUtcTicks { get; set; }
    public long ConfirmedAtUtcTicks { get; set; }
    public long ExpiresAtUtcTicks { get; set; }

    public LeaseGrant ToGrant() => new(new LeaseIdentity(new LeaseResource(Scope, Name), HolderId, TermId),
        TimeSpan.FromTicks(ExpiresAtUtcTicks - ConfirmedAtUtcTicks), new DateTimeOffset(ConfirmedAtUtcTicks, TimeSpan.Zero));
    public LeaseObservation ToObservation() => new(new LeaseResource(Scope, Name), HolderId,
        new DateTimeOffset(AcquiredAtUtcTicks, TimeSpan.Zero), new DateTimeOffset(ConfirmedAtUtcTicks, TimeSpan.Zero),
        new DateTimeOffset(ExpiresAtUtcTicks, TimeSpan.Zero));
}
