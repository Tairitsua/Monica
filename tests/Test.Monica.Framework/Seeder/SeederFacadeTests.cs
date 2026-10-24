using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Facades;
using Monica.Framework.Seeder.Models;
using Monica.Framework.Seeder.Models.Internal;
using Monica.Framework.Seeder.Services.Support;
using Monica.Modules;
using Xunit;
using NSubstitute;

namespace Test.Monica.Framework.Seeder;

public sealed class SeederFacadeTests
{
    [Fact]
    public async Task GetSnapshotAsync_WhenStateIsAvailable_ShouldReturnConfigurationAndRun()
    {
        var options = new ModuleSeederOption
        {
            MaxConcurrency = 7,
            DefaultFailureBehavior = SeederFailureBehavior.FailFast,
            DefaultMaxAttempts = 3
        };
        var graph = SeederGraph.Create([typeof(FacadeSeeder)], options);
        ISeederState state = new SeederState(graph, TimeProvider.System);
        var facade = new SeederFacade(state, Options.Create(options), NullLogger<SeederFacade>.Instance,
            Substitute.For<ISeederExecutor>());

        var result = await facade.GetSnapshotAsync(TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Configuration.MaxConcurrency.Should().Be(7);
        result.Data.Configuration.DefaultMaxAttempts.Should().Be(3);
        result.Data.Configuration.DefaultFailureBehavior.Should().Be(SeederFailureBehavior.FailFast);
        result.Data.Run.Status.Should().Be(SeederRunStatus.Waiting);
        result.Data.Run.Seeders.Should().ContainSingle();
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCancelled_ShouldPropagateCancellation()
    {
        var options = new ModuleSeederOption();
        var graph = SeederGraph.Create([typeof(FacadeSeeder)], options);
        ISeederState state = new SeederState(graph, TimeProvider.System);
        var facade = new SeederFacade(state, Options.Create(options), NullLogger<SeederFacade>.Instance,
            Substitute.For<ISeederExecutor>());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> capture = async () => await facade.GetSnapshotAsync(cancellation.Token);

        await capture.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FacadeSeeder : ISeeder
    {
        public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
