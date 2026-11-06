using AwesomeAssertions;
using Monica.BuildingBlocks.Models;
using Xunit;

namespace Test.Monica.BuildingBlocks;

public sealed class BuildingBlockKindTests
{
    [Fact]
    public void Values_ShouldPreservePublishedNumericContract()
    {
        var expected = new Dictionary<BuildingBlockKind, int>
        {
            [BuildingBlockKind.None] = 0,
            [BuildingBlockKind.ApplicationService] = 1,
            [BuildingBlockKind.CrudApplicationService] = 2,
            [BuildingBlockKind.DomainService] = 3,
            [BuildingBlockKind.Repository] = 4,
            [BuildingBlockKind.DomainEvent] = 5,
            [BuildingBlockKind.DomainEventHandler] = 6,
            [BuildingBlockKind.LocalEventHandler] = 7,
            [BuildingBlockKind.Seeder] = 8,
            [BuildingBlockKind.RecurringJob] = 9,
            [BuildingBlockKind.TriggeredJob] = 10,
            [BuildingBlockKind.HttpApi] = 11,
            [BuildingBlockKind.GrpcApi] = 12,
            [BuildingBlockKind.StateStore] = 13,
            [BuildingBlockKind.EventBus] = 14,
            [BuildingBlockKind.Actor] = 15,
            [BuildingBlockKind.Entity] = 16,
            [BuildingBlockKind.RequestDto] = 17,
            [BuildingBlockKind.Configuration] = 18,
            [BuildingBlockKind.HostedService] = 19
        };

        Enum.GetValues<BuildingBlockKind>().Should().HaveCount(expected.Count);
        foreach (var (kind, value) in expected)
        {
            ((int)kind).Should().Be(value);
        }
    }
}
