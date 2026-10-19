namespace Monica.EventBus.Annotations;

/// <summary>Assigns a stable workload owner to a distributed event handler.</summary>
/// <param name="workloadOwner">The logical consumer owner, independent of deployment and process identity.</param>
/// <remarks>
/// Typed distributed subscriptions use this inherited declaration before consulting the host's type activation policy.
/// It does not activate a handler or register it in dependency injection. Native transports use the owner to
/// preserve independent subscriptions when roles move between combined and split hosts.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class EventSubscriptionOwnerAttribute(string workloadOwner) : Attribute
{
    /// <summary>Gets the stable logical consumer owner.</summary>
    public string WorkloadOwner { get; } = string.IsNullOrWhiteSpace(workloadOwner)
        ? throw new ArgumentException("An event subscription workload owner cannot be empty.", nameof(workloadOwner))
        : workloadOwner;
}
