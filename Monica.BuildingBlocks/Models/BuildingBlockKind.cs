namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Identifies the kind of a discovered building block.
/// </summary>
/// <remarks>
/// Numeric values are part of the serialized contract. Assign new kinds only by appending a new value.
/// Values 12 through 15 are reserved and are not currently discoverable.
/// </remarks>
public enum BuildingBlockKind
{
    /// <summary>
    /// No kind has been assigned.
    /// </summary>
    None = 0,
    /// <summary>
    /// Application handler for one mediator request.
    /// </summary>
    ApplicationRequestHandler = 1,
    /// <summary>
    /// CRUD application service that participates in automatic controller generation.
    /// </summary>
    CrudApplicationService = 2,
    /// <summary>
    /// Domain service.
    /// </summary>
    DomainService = 3,
    /// <summary>
    /// Repository.
    /// </summary>
    Repository = 4,
    /// <summary>
    /// Domain event.
    /// </summary>
    DomainEvent = 5,
    /// <summary>
    /// Distributed event handler, including explicit batch consumers.
    /// </summary>
    DistributedEventHandler = 6,
    /// <summary>
    /// Local event handler.
    /// </summary>
    LocalEventHandler = 7,
    /// <summary>
    /// Startup data seeder.
    /// </summary>
    Seeder = 8,
    /// <summary>
    /// Recurring scheduled job.
    /// </summary>
    RecurringJob = 9,
    /// <summary>
    /// Triggered job.
    /// </summary>
    TriggeredJob = 10,
    /// <summary>
    /// HTTP API.
    /// </summary>
    HttpApi = 11,
    /// <summary>
    /// gRPC API.
    /// </summary>
    GrpcApi = 12,
    /// <summary>
    /// State store.
    /// </summary>
    StateStore = 13,
    /// <summary>
    /// Event bus.
    /// </summary>
    EventBus = 14,
    /// <summary>
    /// Actor.
    /// </summary>
    Actor = 15,
    /// <summary>
    /// Entity or aggregate.
    /// </summary>
    Entity = 16,
    /// <summary>
    /// Request DTO.
    /// </summary>
    RequestDto = 17,
    /// <summary>
    /// Configuration model.
    /// </summary>
    Configuration = 18,
    /// <summary>
    /// Host-managed long-running or lifecycle service.
    /// </summary>
    HostedService = 19
}
