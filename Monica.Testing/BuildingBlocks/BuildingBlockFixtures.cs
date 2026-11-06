using Monica.EventBus.Abstractions.Handlers;
using Monica.JobScheduler.Abstractions;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;

namespace Monica.Testing.BuildingBlocks;

/// <summary>
/// Factory methods for Monica Building Block fast-path fixtures.
/// </summary>
public static class BuildingBlockFixtures
{
    /// <summary>
    /// Creates a builder for a domain service Building Block.
    /// </summary>
    /// <typeparam name="TService">The domain service under test.</typeparam>
    public static BuildingBlockFixtureBuilder<TService> DomainService<TService>()
        where TService : DomainService
    {
        return BuildingBlockFixture<TService>.Builder();
    }

    /// <summary>
    /// Creates a builder for a distributed domain event handler Building Block.
    /// </summary>
    /// <typeparam name="THandler">The event handler under test.</typeparam>
    /// <typeparam name="TEvent">The event payload handled by the Building Block.</typeparam>
    public static BuildingBlockFixtureBuilder<THandler> DomainEventHandler<THandler, TEvent>()
        where THandler : class, IDistributedEventHandler<TEvent>
    {
        return BuildingBlockFixture<THandler>.Builder();
    }

    /// <summary>
    /// Creates a builder for a local event handler Building Block.
    /// </summary>
    /// <typeparam name="THandler">The event handler under test.</typeparam>
    /// <typeparam name="TEvent">The event payload handled by the Building Block.</typeparam>
    public static BuildingBlockFixtureBuilder<THandler> LocalEventHandler<THandler, TEvent>()
        where THandler : class, ILocalEventHandler<TEvent>
    {
        return BuildingBlockFixture<THandler>.Builder();
    }

    /// <summary>
    /// Creates a builder for a recurring job Building Block.
    /// </summary>
    /// <typeparam name="TJob">The recurring job under test.</typeparam>
    public static BuildingBlockFixtureBuilder<TJob> RecurringJob<TJob>()
        where TJob : RecurringJob
    {
        return BuildingBlockFixture<TJob>.Builder();
    }

    /// <summary>
    /// Creates a builder for a triggered job Building Block.
    /// </summary>
    /// <typeparam name="TJob">The triggered job under test.</typeparam>
    /// <typeparam name="TArgs">The trigger argument type.</typeparam>
    public static BuildingBlockFixtureBuilder<TJob> TriggeredJob<TJob, TArgs>()
        where TJob : TriggeredJob<TArgs>
        where TArgs : class
    {
        return BuildingBlockFixture<TJob>.Builder();
    }
}
