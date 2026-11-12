using AwesomeAssertions;
using Monica.Configuration.Models;
using Xunit;

namespace Test.Monica.Configuration.EfCore;

public sealed partial class DatabaseConfigurationStorePublishTests
{
    [Fact]
    public async Task PublishAsync_WhenDefinitionBecomesStartup_ShouldPreserveLegacyManagedPublicationAndRevisionHistory()
    {
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var stores = CreateStores(databasePath, 1);
        var legacy = CreateDefinition("Test.Startup.Legacy");
        await stores.Items[0].PublishAsync(CreatePublication([legacy]), TestContext.Current.CancellationToken);
        var before = await GetOverviewAsync(stores, legacy.DefinitionKey);
        var fresh = CreateDefinition("Test.Startup.New") with { Kind = ConfigurationKind.Startup };

        await stores.Items[0].PublishAsync(CreatePublication([legacy with { Kind = ConfigurationKind.Startup }, fresh]),
            TestContext.Current.CancellationToken);

        var after = await GetOverviewAsync(stores, legacy.DefinitionKey);
        after.Should().BeEquivalentTo(before);
        (await stores.Items[0].ListPublishedDefinitionEntriesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(entry => entry.Metadata.DefinitionKey == legacy.DefinitionKey);
    }
}
