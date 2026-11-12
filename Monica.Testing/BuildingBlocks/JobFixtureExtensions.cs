using Monica.JobScheduler.Abstractions;

namespace Monica.Testing.BuildingBlocks;

/// <summary>
/// Convenience execution helpers for job Building Block fixtures.
/// </summary>
public static class JobFixtureExtensions
{
    /// <summary>
    /// Executes the recurring job owned by the fixture.
    /// </summary>
    public static Task ExecuteAsync<TJob>(
        this BuildingBlockFixture<TJob> fixture,
        CancellationToken cancellationToken = default)
        where TJob : RecurringJob
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return fixture.BuildingBlock.ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// Executes the triggered job owned by the fixture with typed parameters.
    /// </summary>
    public static Task ExecuteAsync<TJob, TArgs>(
        this BuildingBlockFixture<TJob> fixture,
        TArgs parameters,
        CancellationToken cancellationToken = default)
        where TJob : TriggeredJob<TArgs>
        where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(parameters);
        return fixture.BuildingBlock.ExecuteAsync(parameters, cancellationToken);
    }
}
