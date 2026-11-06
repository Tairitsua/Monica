using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Monica.AI.AgentCapabilities.Abstractions;
using Monica.AI.AgentCapabilities.Models;
using Monica.AI.Facades;
using Monica.AI.Mcp.Abstractions;
using Monica.AI.Mcp.Facades;
using Monica.AI.Mcp.Models;
using Monica.AI.Mcp.Services;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Hosting;
using Test.Monica.AI.Chat.Providers;

namespace Test.Monica.AI.Mcp.Facades;

public sealed class McpFacadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteExternalProfileAsync_WhenEntryIsCached_ShouldRemoveItWithoutRequiringAnOverride(
        bool hasRuntimeOverride)
    {
        using var files = new FileChatStorageFixture();
        var ct = TestContext.Current.CancellationToken;
        var root = files.Options.Value.StorageRootPath;
        var profilesPath = files.Files.GetPath("external-profiles.json");
        Directory.CreateDirectory(root);
        // A legacy invalid endpoint produces cached discovery metadata through real validation,
        // without opening a network connection. Unavailable profiles must remain deletable.
        var profile = new ExternalMcpClientProfile
        {
            Name = "legacy-client",
            Description = "Persisted external client awaiting endpoint repair",
            Endpoint = "invalid-endpoint"
        };
        await File.WriteAllTextAsync(profilesPath, JsonSerializer.Serialize(new ExternalMcpClientProfileStorePayload
        {
            Profiles = [profile]
        }), Encoding.UTF8, ct);

        var factory = new McpTestApplicationFactory(root, profilesPath);
        await using var application = await factory.CreateAsync(cancellationToken: ct);
        await using var scope = application.CreateScope(ct);
        var capabilityService = scope.Resolve<IAgentCapabilityService>();
        var stateStore = scope.Resolve<IAgentCapabilityStateStore>();
        var profileStore = scope.Resolve<IExternalMcpClientProfileStore>();
        profileStore.Should().BeOfType<FileExternalMcpClientProfileStore>();

        if (hasRuntimeOverride)
        {
            await capabilityService.SetEntryEnabledAsync(AgentCapabilityKind.Mcp, profile.Name, false, ct);
        }

        var beforeState = await stateStore.LoadAsync(ct);
        beforeState.McpEntries.ContainsKey(profile.Name).Should().Be(hasRuntimeOverride);
        var before = await capabilityService.GetManagementInfoAsync(ct);
        var cachedEntry = before.McpEntries.Should().ContainSingle(entry => entry.Name == profile.Name).Which;
        cachedEntry.IsUserManaged.Should().BeTrue();
        cachedEntry.DiscoveryError.Should().Contain("absolute HTTP or HTTPS URL");

        var deleted = await scope.Resolve<McpFacade>().DeleteExternalProfileAsync(profile.Name, ct);

        deleted.Status.Should().Be(ResultStatus.Ok);
        deleted.Message.Should().BeNull();
        deleted.Data.Should().NotBeNull();
        deleted.Data!.McpEntries.Should().NotContain(entry => entry.Name == profile.Name);
        (await profileStore.LoadAsync(ct)).Should().NotContain(entry => entry.Name == profile.Name);
        var afterState = await stateStore.LoadAsync(ct);
        afterState.McpEntries.Should().NotContainKey(profile.Name);
        afterState.Revision.Should().Be(beforeState.Revision + (hasRuntimeOverride ? 1 : 0));

        // A fresh scope still uses the host-owned catalog; no explicit refresh repairs the cache.
        await using var subsequentScope = application.CreateScope(ct);
        var subsequent = await subsequentScope.Resolve<AgentCapabilityFacade>().GetManagementInfoAsync(ct);
        subsequent.Status.Should().Be(ResultStatus.Ok);
        subsequent.Message.Should().BeNull();
        subsequent.Data.Should().NotBeNull();
        subsequent.Data!.McpEntries.Should().NotContain(entry => entry.Name == profile.Name);
        var catalogEntries = await subsequentScope.Resolve<MonicaMcpCatalog>().GetEntriesAsync(afterState, ct);
        catalogEntries.Should().NotContain(entry => entry.Name == profile.Name);
    }

    private sealed class McpTestApplicationFactory(string root, string profilesPath)
        : MonicaTestApplicationFactory<McpFacadeTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddAI(options => options.StorageRootPath = root);
            builder.AddMcp(options => options.ExternalMcpClientProfileStoreFilePath = profilesPath);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        }
    }
}
