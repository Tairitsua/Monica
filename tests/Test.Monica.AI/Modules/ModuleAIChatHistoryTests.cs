using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.AI.Chat.Abstractions;
using Monica.AI.Chat.Facades;
using Monica.AI.Chat.Providers;
using Monica.AI.Configuration.Facades;
using Monica.AI.Facades;
using Monica.AI.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Hosting;
using Test.Monica.AI.Chat.Providers;

namespace Test.Monica.AI.Modules;

public sealed class ModuleAIChatHistoryTests
{
    [Fact]
    public async Task Composition_WhenPersistenceIsNotConfigured_ShouldActivatePartitionedFileDefaults()
    {
        using var files = new FileChatStorageFixture();
        var factory = new ChatTestApplicationFactory(files.Options.Value.StorageRootPath);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var first = application.CreateScope(TestContext.Current.CancellationToken);
        await using var second = application.CreateScope(TestContext.Current.CancellationToken);

        first.Resolve<IChatHistoryProvider>().Should().BeOfType<FileChatHistoryProvider>()
            .And.BeSameAs(second.Resolve<IChatHistoryProvider>());
        first.Resolve<IChatAttachmentStore>().Should().BeOfType<FileChatAttachmentStore>();
        first.Resolve<IChatHistoryPartitionResolver>().Should().BeOfType<ChatHistoryPartitionResolver>()
            .And.NotBeSameAs(second.Resolve<IChatHistoryPartitionResolver>());
        first.Resolve<ChatHistoryFacade>().Should().NotBeSameAs(second.Resolve<ChatHistoryFacade>());
        first.Resolve<ChatAttachmentFacade>().Should().NotBeNull();
    }

    [Fact]
    public async Task CreateSessionAsync_WhenTrustedUsersDiffer_ShouldShareSettingsAndIsolateConversation()
    {
        using var files = new FileChatStorageFixture();
        var factory = new ChatTestApplicationFactory(files.Options.Value.StorageRootPath);
        var ct = TestContext.Current.CancellationToken;
        await using var application = await factory.CreateAsync(cancellationToken: ct);
        await using var alice = application.CreateScope(ct);
        await using var bob = application.CreateScope(ct);
        alice.Resolve<TestIdentityAccessor>().Subject = "alice";
        bob.Resolve<TestIdentityAccessor>().Subject = "bob";

        var created = await alice.Resolve<ChatFacade>().CreateSessionAsync(ct: ct);
        created.Status.Should().Be(ResultStatus.Ok);
        created.Message.Should().BeNull();
        await using var session = created.Data!;
        var saved = await alice.Resolve<ChatHistoryFacade>().SaveSessionAsync(session, 0, ct);
        saved.Status.Should().Be(ResultStatus.Ok);
        saved.Message.Should().BeNull();
        saved.Data!.IsPersisted.Should().BeTrue();

        var otherCatalog = await bob.Resolve<ChatHistoryFacade>().GetCatalogAsync(ct);
        otherCatalog.Status.Should().Be(ResultStatus.Ok);
        otherCatalog.Data!.Sessions.Should().BeEmpty();
        var otherSession = await bob.Resolve<ChatHistoryFacade>().LoadSessionAsync(session.SessionId, ct);
        otherSession.Status.Should().Be(ResultStatus.Ok);
        otherSession.Data.Should().BeNull();
        var crossUserSave = await bob.Resolve<ChatHistoryFacade>().SaveSessionAsync(session, 0, ct);
        crossUserSave.Status.Should().NotBe(ResultStatus.Ok);

        var aliceSettings = await alice.Resolve<AIConfigurationFacade>().GetAsync(ct);
        var bobSettings = await bob.Resolve<AIConfigurationFacade>().GetAsync(ct);
        aliceSettings.Status.Should().Be(ResultStatus.Ok);
        bobSettings.Status.Should().Be(ResultStatus.Ok);
        bobSettings.Data.Should().BeEquivalentTo(aliceSettings.Data);
        bobSettings.Data!.Providers.Should().ContainSingle();
    }

    private sealed class ChatTestApplicationFactory(string root) : MonicaTestApplicationFactory<ModuleAIChatHistoryTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddAI(options => { options.StorageRootPath = root; options.WorkspaceId = "test-workspace"; })
                .AddOpenAIProvider(options =>
                {
                    options.ApiKey = "test-key-without-network";
                    options.Models = [new LLMModelInfo { ModelName = "test-model", ContextWindow = 16000 }];
                }, "test-provider");
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddScoped<TestIdentityAccessor>();
            services.RemoveAll<IChatUserIdentityAccessor>();
            services.AddScoped<IChatUserIdentityAccessor>(provider => provider.GetRequiredService<TestIdentityAccessor>());
        }
    }

    private sealed class TestIdentityAccessor : IChatUserIdentityAccessor
    {
        public string? Subject { get; set; }

        public ValueTask<ClaimsPrincipal?> GetUserAsync(CancellationToken ct = default) => ValueTask.FromResult(
            Subject is null ? null : new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)], "test")));
    }
}
