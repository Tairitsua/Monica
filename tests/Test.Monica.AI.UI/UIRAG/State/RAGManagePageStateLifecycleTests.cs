using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.AI.Abstractions;
using Monica.AI.KnowledgeBase.Facades;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.Models;
using Monica.AI.RAG.Facades;
using Monica.AI.UI.Localization;
using Monica.AI.UI.UIRAG.State;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Hosting;
using MudBlazor;
using NSubstitute;

namespace Test.Monica.AI.UI.UIRAG.State;

/// <summary>Exercises route retirement against the real indexing pipeline without rendering UI.</summary>
public sealed class RAGManagePageStateLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WhenIndexingIsAdmitted_ShouldDrainWithoutCancellingDurableWork(bool batch)
    {
        var root = Path.Combine(Path.GetTempPath(), "Test.Monica.AI.UI", nameof(RAGManagePageStateLifecycleTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var boundary = new ControlledEmbeddings();
            var snackbar = Substitute.For<ISnackbar>();
            var localizer = Substitute.For<IStringLocalizer<AIResource>>();
            localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
            localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));
            await using var application = await new LifecycleApplicationFactory(root).CreateAsync(seams => seams
                .With<IAIProviderFactory>(boundary.Factory)
                .With<ISnackbar>(snackbar)
                .With<IDialogService>(Substitute.For<IDialogService>())
                .With<IStringLocalizer<AIResource>>(localizer), TestContext.Current.CancellationToken);
            await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
            var bases = scope.Resolve<KnowledgeBaseFacade>();
            var documents = scope.Resolve<KnowledgeDocumentFacade>();
            (await bases.CreateAsync("owned", "Process-owned indexing")).Status.Should().Be(ResStatus.Ok);
            (await documents.UploadDocumentAsync("owned", "owned.md", "# Owned source\n\nA route visit must not cancel durable indexing.")).Status.Should().Be(ResStatus.Ok);
            (await scope.Resolve<EmbeddingModelFacade>()
                .SetKnowledgeBaseEmbeddingModelAsync("owned", "controlled", "controlled-4d")).Status.Should().Be(ResStatus.Ok);
            var state = scope.Resolve<RAGManagePageStateFactory>().Create(action => { action(); return Task.CompletedTask; });
            try
            {
                await state.InitializeAsync();
                await state.SelectKnowledgeBaseAsync(state.KnowledgeBases.Single());
                state.SelectAllIndexableDocuments();
                var lateNotifications = 0;
                state.StateChanged += () => { if (state.IsRetired) Interlocked.Increment(ref lateNotifications); return Task.CompletedTask; };
                var operation = batch ? state.StartBatchIndexingAsync() : state.StartDocumentIndexingAsync(state.DocumentQueue.Single());
                await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

                var retirement = state.DisposeAsync().AsTask();

                state.IsRetired.Should().BeTrue();
                retirement.IsCompleted.Should().BeFalse();
                boundary.RequestToken.IsCancellationRequested.Should().BeFalse();
                var notificationsAtRetirement = snackbar.ReceivedCalls().Count();
                boundary.Release.SetResult();
                await Task.WhenAll(operation, retirement);

                var inventory = await documents.GetDocumentInventoryAsync("owned");
                inventory.Status.Should().Be(ResStatus.Ok);
                inventory.Data.Should().NotBeNull();
                var row = inventory.Data!.Should().ContainSingle().Which;
                row.Status.Should().Be(DocumentStatus.Done);
                row.ChunkCount.Should().BeGreaterThan(0);
                row.SourceKind.Should().Be(KnowledgeDocumentSourceKinds.Uploaded);
                row.SourceGroupKey.Should().BeNull();
                var current = await bases.GetByIdAsync("owned");
                current.Status.Should().Be(ResStatus.Ok);
                current.Data!.ChunkCount.Should().Be(row.ChunkCount);
                lateNotifications.Should().Be(0);
                snackbar.ReceivedCalls().Count().Should().Be(notificationsAtRetirement);
            }
            finally
            {
                boundary.Release.TrySetResult();
                await state.DisposeAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SelectKnowledgeBaseAsync_WhenExistingBatchSettles_ShouldRefreshPartialVectorValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Test.Monica.AI.UI", nameof(RAGManagePageStateLifecycleTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var boundary = new ControlledEmbeddings { HoldRequests = false };
            var localizer = Substitute.For<IStringLocalizer<AIResource>>();
            localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
            localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));
            await using var application = await new LifecycleApplicationFactory(root).CreateAsync(seams => seams
                .With<IAIProviderFactory>(boundary.Factory)
                .With<ISnackbar>(Substitute.For<ISnackbar>())
                .With<IDialogService>(Substitute.For<IDialogService>())
                .With<IStringLocalizer<AIResource>>(localizer), TestContext.Current.CancellationToken);
            await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
            var bases = scope.Resolve<KnowledgeBaseFacade>();
            var documents = scope.Resolve<KnowledgeDocumentFacade>();
            var indexing = scope.Resolve<RAGIndexingFacade>();
            (await bases.CreateAsync("owned", "Reopened batch")).Status.Should().Be(ResStatus.Ok);
            (await documents.UploadDocumentAsync("owned", "first.md", "# First source\n\nAlready indexed content.")).Status.Should().Be(ResStatus.Ok);
            (await scope.Resolve<EmbeddingModelFacade>()
                .SetKnowledgeBaseEmbeddingModelAsync("owned", "controlled", "controlled-4d")).Status.Should().Be(ResStatus.Ok);
            (await indexing.IndexDocumentAsync("owned", "first.md", ct: TestContext.Current.CancellationToken)).Status.Should().Be(ResStatus.Ok);
            var initial = await documents.GetDocumentInventoryAsync("owned");
            initial.Status.Should().Be(ResStatus.Ok);
            var first = initial.Data!.Should().ContainSingle().Which;
            first.Status.Should().Be(DocumentStatus.Done);
            var firstChunks = first.ChunkCount;
            firstChunks.Should().BeGreaterThan(0);
            (await documents.UploadDocumentAsync("owned", "second.md", "# Second source\n\nContent completed after the visit opens.")).Status.Should().Be(ResStatus.Ok);

            boundary.HoldRequests = true;
            var batch = indexing.StartBatchAsync("owned", ["second.md"], ct: TestContext.Current.CancellationToken);
            var state = scope.Resolve<RAGManagePageStateFactory>().Create(action => { action(); return Task.CompletedTask; });
            try
            {
                await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                var partialValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var completedValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                state.StateChanged += () =>
                {
                    if (state.SelectedKnowledgeBaseVectorValidation is { WasValidated: true } validation)
                    {
                        if (validation.IndexedDocumentCount == 1) partialValidation.TrySetResult();
                        if (validation.IndexedDocumentCount == 2) completedValidation.TrySetResult();
                    }
                    return Task.CompletedTask;
                };
                await state.InitializeAsync();
                await state.SelectKnowledgeBaseAsync(state.KnowledgeBases.Single());
                await partialValidation.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                state.SelectedKnowledgeBaseVectorValidation!.IndexedChunkCount.Should().Be(firstChunks);

                boundary.Release.SetResult();
                (await batch).Status.Should().Be(ResStatus.Ok);
                await completedValidation.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

                state.DocumentQueue.Should().HaveCount(2).And.OnlyContain(row => row.Status == DocumentStatus.Done);
                var finalChunks = state.DocumentQueue.Sum(row => row.ChunkCount);
                finalChunks.Should().BeGreaterThan(firstChunks);
                state.SelectedKnowledgeBase!.ChunkCount.Should().Be(finalChunks);
                state.SelectedKnowledgeBaseVectorValidation!.IndexedChunkCount.Should().Be(finalChunks);
                state.SelectedKnowledgeBaseVectorValidation.HasValidVectors.Should().BeTrue();
            }
            finally
            {
                boundary.Release.TrySetResult();
                await batch;
                await state.DisposeAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ControlledEmbeddings
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken RequestToken { get; private set; }
        internal bool HoldRequests { get; set; } = true;
        internal IAIProviderFactory Factory { get; } = Substitute.For<IAIProviderFactory>();

        internal ControlledEmbeddings()
        {
            var info = new AIProviderInfo
            {
                ProviderId = "controlled", DisplayName = "Controlled embedding boundary", ProviderType = "Fake",
                SupportedModels = [new EmbeddingModelInfo { ModelName = "controlled-4d", Dimensions = 4 }]
            };
            var provider = Substitute.For<IAIProvider>();
            var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
            provider.Info.Returns(info);
            provider.ProviderId.Returns(info.ProviderId);
            provider.GetEmbeddingGenerator(Arg.Any<string>()).Returns(generator);
            Factory.GetAllProviderInfos().Returns(new[] { info });
            Factory.GetProviderInfo(info.ProviderId).Returns(info);
            Factory.AcquireProvider(info.ProviderId).Returns(_ =>
            {
                var lease = Substitute.For<IAIProviderLease>();
                lease.Provider.Returns(provider);
                return lease;
            });
            generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (HoldRequests)
                    {
                        RequestToken = call.Arg<CancellationToken>();
                        Started.TrySetResult();
                        await Release.Task.WaitAsync(RequestToken);
                    }
                    return new GeneratedEmbeddings<Embedding<float>>(
                        call.Arg<IEnumerable<string>>().Select(_ => new Embedding<float>(new float[] { 1, 0, 0, 0 })).ToList());
                });
        }
    }

    private sealed class LifecycleApplicationFactory(string root) : MonicaTestApplicationFactory<RAGManagePageStateLifecycleTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddAI(options => options.StorageRootPath = Path.Combine(root, "ai"));
            builder.AddKnowledgeBase(options =>
            {
                options.DocumentIndexStateStoreFilePath = Path.Combine(root, "state.json");
                options.UploadedDocumentSourceRootPath = Path.Combine(root, "sources");
            });
            builder.AddRAG(options => options.ChunkerRoutingStoreFilePath = Path.Combine(root, "routing.json"))
                .UseVectorStoreInMemoryProvider();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            // Exercise the production page owner against the real backend without composing shell endpoints.
            services.AddScoped<RAGManagePageStateFactory>();
            services.AddOptions<ModuleShellUIOption>();
            services.AddOptions<ModuleClockOption>();
            services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        }
    }
}
