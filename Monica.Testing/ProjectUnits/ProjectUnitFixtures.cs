using Monica.EventBus.Abstractions.Handlers;
using Monica.JobScheduler.Abstractions;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;

namespace Monica.Testing.ProjectUnits;

/// <summary>
/// Factory methods for Monica ProjectUnit fast-path fixtures.
/// </summary>
public static class ProjectUnitFixtures
{
    /// <summary>
    /// Creates a builder for a domain service ProjectUnit.
    /// </summary>
    /// <typeparam name="TService">The domain service under test.</typeparam>
    public static ProjectUnitFixtureBuilder<TService> DomainService<TService>()
        where TService : DomainService
    {
        return ProjectUnitFixture<TService>.Builder();
    }

    /// <summary>
    /// Creates a builder for a distributed domain event handler ProjectUnit.
    /// </summary>
    /// <typeparam name="THandler">The event handler under test.</typeparam>
    /// <typeparam name="TEvent">The event payload handled by the ProjectUnit.</typeparam>
    public static ProjectUnitFixtureBuilder<THandler> DomainEventHandler<THandler, TEvent>()
        where THandler : class, IDistributedEventHandler<TEvent>
    {
        return ProjectUnitFixture<THandler>.Builder();
    }

    /// <summary>
    /// Creates a builder for a local event handler ProjectUnit.
    /// </summary>
    /// <typeparam name="THandler">The event handler under test.</typeparam>
    /// <typeparam name="TEvent">The event payload handled by the ProjectUnit.</typeparam>
    public static ProjectUnitFixtureBuilder<THandler> LocalEventHandler<THandler, TEvent>()
        where THandler : class, ILocalEventHandler<TEvent>
    {
        return ProjectUnitFixture<THandler>.Builder();
    }

    /// <summary>
    /// Creates a builder for a recurring job ProjectUnit.
    /// </summary>
    /// <typeparam name="TJob">The recurring job under test.</typeparam>
    public static ProjectUnitFixtureBuilder<TJob> RecurringJob<TJob>()
        where TJob : RecurringJob
    {
        return ProjectUnitFixture<TJob>.Builder();
    }

    /// <summary>
    /// Creates a builder for a triggered job ProjectUnit.
    /// </summary>
    /// <typeparam name="TJob">The triggered job under test.</typeparam>
    /// <typeparam name="TArgs">The trigger argument type.</typeparam>
    public static ProjectUnitFixtureBuilder<TJob> TriggeredJob<TJob, TArgs>()
        where TJob : TriggeredJob<TArgs>
        where TArgs : class
    {
        return ProjectUnitFixture<TJob>.Builder();
    }
}
