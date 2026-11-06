using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Monica.AI.KnowledgeBase.Abstractions;
using Monica.AI.KnowledgeBase.Facades;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.RAG.Facades;
using Monica.AI.RAG.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Markdown.Abstractions;
using Monica.Markdown.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Monica.Tool.Algorithms.Trees;
using NSubstitute;
using Test.Monica.AI.Chat.Providers;

namespace Test.Monica.AI.Services.Support;

public sealed class RAGSourceProvenanceTests
{
    private const string DOCUMENT_ID = "shared.md";
    private const string UPLOADED_CONTENT = "# Upload\n\nThis content belongs to the uploaded source.";
    private const string FIRST_CONTENT = "# First group\n\nThis is the unrelated same-path document.";
    private const string SECOND_CONTENT = "# Second group\n\nThis content belongs to the recorded group.";

    [Fact]
    public async Task IndexDocumentAsync_WhenCallerCancels_ShouldPropagateCancellation()
    {
        using var files = new FileChatStorageFixture();
        await using var application = await CreateApplicationAsync(files, CreateCatalog());
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Resolve<RAGIndexingFacade>().IndexDocumentAsync("source", DOCUMENT_ID, ct: cancellation.Token));
    }

    [Fact]
    public async Task CancelBatchAsync_WhenNoBatchIsActive_ShouldReturnTheOperationMessage()
    {
        using var files = new FileChatStorageFixture();
        await using var application = await CreateApplicationAsync(files, CreateCatalog());
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);

        var result = await scope.Resolve<RAGIndexingFacade>().CancelBatchAsync("source", scope.CancellationToken);

        result.IsSuccess().Should().BeTrue();
        result.Message.Should().Be("No active indexing task found. Cancellation treated as no-op.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexDocumentAsync_WhenUploadSharesMarkdownPath_ShouldKeepUploadedContentAndProvenance(bool batch)
    {
        using var files = new FileChatStorageFixture();
        var catalog = CreateCatalog();
        await using var application = await CreateApplicationAsync(files, catalog);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);
        var uploaded = await scope.Resolve<KnowledgeDocumentFacade>().UploadDocumentAsync("source", DOCUMENT_ID, UPLOADED_CONTENT);
        uploaded.Status.Should().Be(ResultStatus.Ok);
        await AssertSourcePreviewAsync(scope, UPLOADED_CONTENT, KnowledgeDocumentSourceKinds.Uploaded, null);

        await IndexAsync(scope, batch);

        var state = await scope.Resolve<IDocumentIndexStateStore>().GetDocumentStateAsync("source", DOCUMENT_ID, scope.CancellationToken);
        state.Should().NotBeNull();
        state!.Status.Should().Be(DocumentStatus.Done);
        state.SourceKind.Should().Be(KnowledgeDocumentSourceKinds.Uploaded);
        state.SourceGroupKey.Should().BeNull();
        (await scope.Resolve<IKnowledgeDocumentSourceStore>().GetContentAsync("source", DOCUMENT_ID, scope.CancellationToken))
            .Should().Be(UPLOADED_CONTENT);
        await AssertChunkViewAsync(scope, UPLOADED_CONTENT, KnowledgeDocumentSourceKinds.Uploaded, null, isPreview: false);
        await AssertSourcePreviewAsync(scope, UPLOADED_CONTENT, KnowledgeDocumentSourceKinds.Uploaded, null);
        await AssertSearchAsync(scope, UPLOADED_CONTENT);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexDocumentAsync_WhenMarkdownPathsOverlapAcrossGroups_ShouldUseRecordedGroupForPreviewAndIndex(bool batch)
    {
        using var files = new FileChatStorageFixture();
        var catalog = CreateCatalog();
        await using var application = await CreateApplicationAsync(files, catalog);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);
        var imported = await scope.Resolve<KnowledgeDocumentFacade>().ImportMarkdownDocumentsAsync("source", "second", [DOCUMENT_ID]);
        imported.Status.Should().Be(ResultStatus.Ok);
        imported.Message.Should().BeNull();
        imported.Data.Should().NotBeNull();
        var importDetails = imported.Data!;
        importDetails.AddedCount.Should().Be(1);
        importDetails.SkippedCount.Should().Be(0);
        await AssertSourcePreviewAsync(scope, SECOND_CONTENT, KnowledgeDocumentSourceKinds.Markdown, "second");
        await AssertChunkViewAsync(scope, SECOND_CONTENT, KnowledgeDocumentSourceKinds.Markdown, "second", isPreview: true);

        await IndexAsync(scope, batch);

        var state = await scope.Resolve<IDocumentIndexStateStore>().GetDocumentStateAsync("source", DOCUMENT_ID, scope.CancellationToken);
        state.Should().NotBeNull();
        state!.Status.Should().Be(DocumentStatus.Done);
        state.SourceKind.Should().Be(KnowledgeDocumentSourceKinds.Markdown);
        state.SourceGroupKey.Should().Be("second");
        (await scope.Resolve<IKnowledgeDocumentSourceStore>().GetContentAsync("source", DOCUMENT_ID, scope.CancellationToken))
            .Should().Be(SECOND_CONTENT);
        await AssertChunkViewAsync(scope, SECOND_CONTENT, KnowledgeDocumentSourceKinds.Markdown, "second", isPreview: false);
        await AssertSourcePreviewAsync(scope, SECOND_CONTENT, KnowledgeDocumentSourceKinds.Markdown, "second");
        await AssertSearchAsync(scope, SECOND_CONTENT);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("second")]
    [InlineData("unavailable")]
    public async Task StartBatchAsync_WhenRecordedMarkdownSourceIsMissing_ShouldFailWithoutBorrowingAnotherGroup(string? group)
    {
        using var files = new FileChatStorageFixture();
        var catalog = CreateCatalog(includeSecondDocument: false);
        await using var application = await CreateApplicationAsync(files, catalog);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);
        await SeedDocumentAsync(scope, KnowledgeDocumentSourceKinds.Markdown, group);
        await AssertSourcePreviewMissingAsync(scope);

        var preview = await scope.Resolve<RAGIndexingFacade>().GetDocumentChunksAsync("source", DOCUMENT_ID);
        preview.Status.Should().NotBe(ResultStatus.Ok);
        preview.Message.Should().NotBeNullOrWhiteSpace();
        preview.Data.Should().BeNull();
        var indexed = await scope.Resolve<RAGIndexingFacade>().StartBatchAsync("source", [DOCUMENT_ID], ct: scope.CancellationToken);
        indexed.Status.Should().NotBe(ResultStatus.Ok);
        indexed.Message.Should().Contain("completed with errors");

        var state = await scope.Resolve<IDocumentIndexStateStore>().GetDocumentStateAsync("source", DOCUMENT_ID, scope.CancellationToken);
        state.Should().NotBeNull();
        state!.Status.Should().Be(DocumentStatus.Error);
        state.SourceKind.Should().Be(KnowledgeDocumentSourceKinds.Markdown);
        state.SourceGroupKey.Should().Be(group);
        state.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        (await scope.Resolve<IKnowledgeDocumentSourceStore>().GetContentAsync("source", DOCUMENT_ID, scope.CancellationToken))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(KnowledgeDocumentSourceKinds.Uploaded, null)]
    [InlineData(KnowledgeDocumentSourceKinds.Uploaded, "first")]
    [InlineData(KnowledgeDocumentSourceKinds.Unknown, null)]
    [InlineData(KnowledgeDocumentSourceKinds.Unknown, "first")]
    public async Task StartBatchAsync_WhenNonMarkdownSourceIsMissing_ShouldFailInsteadOfUsingCatalogContent(string sourceKind, string? group)
    {
        using var files = new FileChatStorageFixture();
        await using var application = await CreateApplicationAsync(files, CreateCatalog());
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);
        await SeedDocumentAsync(scope, sourceKind, group);
        await AssertSourcePreviewMissingAsync(scope);

        var preview = await scope.Resolve<RAGIndexingFacade>().GetDocumentChunksAsync("source", DOCUMENT_ID);
        preview.Status.Should().NotBe(ResultStatus.Ok);
        preview.Message.Should().Contain("no available source content");
        preview.Data.Should().BeNull();
        var indexed = await scope.Resolve<RAGIndexingFacade>().StartBatchAsync("source", [DOCUMENT_ID], ct: scope.CancellationToken);
        indexed.Status.Should().NotBe(ResultStatus.Ok);
        indexed.Message.Should().Contain("completed with errors");

        var state = await scope.Resolve<IDocumentIndexStateStore>().GetDocumentStateAsync("source", DOCUMENT_ID, scope.CancellationToken);
        state.Should().NotBeNull();
        state!.Status.Should().Be(DocumentStatus.Error);
        state.SourceKind.Should().Be(sourceKind);
        state.SourceGroupKey.Should().Be(group);
        (await scope.Resolve<IKnowledgeDocumentSourceStore>().GetContentAsync("source", DOCUMENT_ID, scope.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetDocumentChunksAsync_WhenStoredSourceHasUnknownProvenance_ShouldPreserveItWithoutCatalogEnrichment()
    {
        using var files = new FileChatStorageFixture();
        await using var application = await CreateApplicationAsync(files, CreateCatalog());
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);
        await SeedDocumentAsync(scope, KnowledgeDocumentSourceKinds.Unknown, null);
        await scope.Resolve<IKnowledgeDocumentSourceStore>().SaveContentAsync("source", DOCUMENT_ID, UPLOADED_CONTENT, scope.CancellationToken);
        await AssertSourcePreviewAsync(scope, UPLOADED_CONTENT, KnowledgeDocumentSourceKinds.Unknown, null);

        await AssertChunkViewAsync(scope, UPLOADED_CONTENT, KnowledgeDocumentSourceKinds.Unknown, null, isPreview: false);
        await IndexAsync(scope, batch: false);
        await AssertSourcePreviewAsync(scope, UPLOADED_CONTENT, KnowledgeDocumentSourceKinds.Unknown, null);
        await AssertChunkViewAsync(scope, UPLOADED_CONTENT, KnowledgeDocumentSourceKinds.Unknown, null, isPreview: false);
    }

    [Fact]
    public async Task GetDocumentChunksAsync_WhenCatalogPathIsAbsentFromInventory_ShouldRejectUnownedDocument()
    {
        using var files = new FileChatStorageFixture();
        await using var application = await CreateApplicationAsync(files, CreateCatalog());
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);

        await AssertSourcePreviewMissingAsync(scope);
        var result = await scope.Resolve<RAGIndexingFacade>().GetDocumentChunksAsync("source", DOCUMENT_ID);

        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().Contain("not found in knowledge base");
        result.Data.Should().BeNull();
    }

    [Theory]
    [InlineData(KnowledgeDocumentSourceKinds.Markdown, "unavailable")]
    [InlineData(KnowledgeDocumentSourceKinds.Uploaded, "first")]
    [InlineData(KnowledgeDocumentSourceKinds.Unknown, "first")]
    public async Task GetDocumentPreviewAsync_WhenStoredSourceExists_ShouldKeepSnapshotAndRecordedProvenance(string sourceKind, string group)
    {
        using var files = new FileChatStorageFixture();
        await using var application = await CreateApplicationAsync(files, CreateCatalog());
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);
        await SeedDocumentAsync(scope, sourceKind, group);
        await scope.Resolve<IKnowledgeDocumentSourceStore>().SaveContentAsync("source", DOCUMENT_ID, UPLOADED_CONTENT, scope.CancellationToken);

        await AssertSourcePreviewAsync(scope, UPLOADED_CONTENT, sourceKind, group);
    }

    [Fact]
    public async Task SearchAsync_WhenBatchIndexesMultipleCjkDocumentNames_ShouldPreserveEveryIdentityAndAggregateCount()
    {
        using var files = new FileChatStorageFixture();
        await using var application = await CreateApplicationAsync(files, CreateCatalog());
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        await CreateKnowledgeBaseAsync(scope);
        string[] names = ["知识甲.md", "知识乙.md", "知识丙.md"];
        int[] sections = [5, 2, 5];
        for (var index = 0; index < names.Length; index++)
        {
            var content = string.Join("\n\n", Enumerable.Range(0, sections[index])
                .Select(section => $"# Section {section}\n\nRecorded content for {names[index]} section {section}."));
            var uploaded = await scope.Resolve<KnowledgeDocumentFacade>().UploadDocumentAsync("source", names[index], content);
            uploaded.Status.Should().Be(ResultStatus.Ok);
        }

        var indexed = await scope.Resolve<RAGIndexingFacade>().StartBatchAsync("source", names, maxConcurrency: 3, ct: scope.CancellationToken);
        indexed.Status.Should().Be(ResultStatus.Ok);
        indexed.Message.Should().NotBeNullOrWhiteSpace();
        var inventory = await scope.Resolve<KnowledgeDocumentFacade>().GetDocumentInventoryAsync("source");
        inventory.Status.Should().Be(ResultStatus.Ok);
        inventory.Message.Should().BeNull();
        inventory.Data.Should().NotBeNull();
        var documents = inventory.Data!;
        documents.Should().HaveCount(names.Length).And.OnlyContain(row => row.Status == DocumentStatus.Done);
        var totalChunks = documents.Sum(row => row.ChunkCount);
        totalChunks.Should().BeGreaterThan(names.Length);
        var knowledgeBase = await scope.Resolve<KnowledgeBaseFacade>().GetByIdAsync("source");
        knowledgeBase.Status.Should().Be(ResultStatus.Ok);
        knowledgeBase.Message.Should().BeNull();
        knowledgeBase.Data.Should().NotBeNull();
        var aggregate = knowledgeBase.Data!;
        aggregate.DocumentCount.Should().Be(names.Length);
        aggregate.ChunkCount.Should().Be(totalChunks);

        // Fake embeddings have arbitrary rank; request all records and assert identity and persisted content.
        var search = await scope.Resolve<RAGSearchFacade>().SearchAsync("Recorded content", ["source"], topK: totalChunks, ct: scope.CancellationToken);
        search.Status.Should().Be(ResultStatus.Ok);
        search.Message.Should().BeNull();
        search.Data.Should().NotBeNull();
        var results = search.Data!;
        results.Should().HaveCount(totalChunks);
        foreach (var document in documents)
        {
            var hits = results.Where(hit => hit.DocumentId == document.Id).ToList();
            hits.Should().HaveCount(document.ChunkCount);
            hits.Should().OnlyContain(hit => hit.KnowledgeBaseId == "source" && hit.SourceName == document.Name);
            hits.Select(hit => hit.ChunkIndex).Should().OnlyHaveUniqueItems();
        }
    }

    private static Task<MonicaTestApplication> CreateApplicationAsync(FileChatStorageFixture files, IMarkdownDocumentCatalog catalog)
        => new SourceTestApplicationFactory(files.Options.Value.StorageRootPath).CreateAsync(
            seams => seams.With<IMarkdownDocumentCatalog>(catalog), TestContext.Current.CancellationToken);

    private static async Task CreateKnowledgeBaseAsync(MonicaTestScope scope)
    {
        var created = await scope.Resolve<KnowledgeBaseFacade>().CreateAsync("source", "Source provenance");
        created.Status.Should().Be(ResultStatus.Ok);
        created.Message.Should().BeNull();
        created.Data.Should().NotBeNull();
        created.Data!.Id.Should().Be("source");
        var bound = await scope.Resolve<EmbeddingModelFacade>().SetKnowledgeBaseEmbeddingModelAsync("source", "Fake", "Fake-Embeddings-384d");
        bound.Status.Should().Be(ResultStatus.Ok);
    }

    private static Task SeedDocumentAsync(MonicaTestScope scope, string sourceKind, string? group)
        => scope.Resolve<IDocumentIndexStateStore>().UpsertDocumentStateAsync(new DocumentIndexState
        {
            KnowledgeBaseId = "source", DocumentPath = DOCUMENT_ID, DocumentName = "Recorded source",
            SourceKind = sourceKind, SourceGroupKey = group
        }, scope.CancellationToken);

    private static async Task IndexAsync(MonicaTestScope scope, bool batch)
    {
        var facade = scope.Resolve<RAGIndexingFacade>();
        var result = batch
            ? await facade.StartBatchAsync("source", [DOCUMENT_ID], ct: scope.CancellationToken)
            : await facade.IndexDocumentAsync("source", DOCUMENT_ID, ct: scope.CancellationToken);
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    private static async Task AssertChunkViewAsync(MonicaTestScope scope, string content, string kind, string? group, bool isPreview)
    {
        var result = await scope.Resolve<RAGIndexingFacade>().GetDocumentChunksAsync("source", DOCUMENT_ID);
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        var view = result.Data!;
        view.DocumentPath.Should().Be(DOCUMENT_ID);
        view.OriginalText.Should().Be(content);
        view.SourceKind.Should().Be(kind);
        view.SourceGroupKey.Should().Be(group);
        view.IsPreview.Should().Be(isPreview);
        view.Chunks.Should().NotBeEmpty();
    }

    private static async Task AssertSourcePreviewAsync(MonicaTestScope scope, string content, string kind, string? group)
    {
        var result = await scope.Resolve<KnowledgeDocumentFacade>().GetDocumentPreviewAsync("source", DOCUMENT_ID);
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        var preview = result.Data!;
        preview.KnowledgeBaseId.Should().Be("source");
        preview.DocumentId.Should().Be(DOCUMENT_ID);
        preview.Content.Should().Be(content);
        preview.SourceKind.Should().Be(kind);
        preview.SourceGroupKey.Should().Be(group);
    }

    private static async Task AssertSourcePreviewMissingAsync(MonicaTestScope scope)
    {
        var result = await scope.Resolve<KnowledgeDocumentFacade>().GetDocumentPreviewAsync("source", DOCUMENT_ID);
        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().NotBeNullOrWhiteSpace();
        result.Data.Should().BeNull();
    }

    private static async Task AssertSearchAsync(MonicaTestScope scope, string content)
    {
        var result = await scope.Resolve<RAGSearchFacade>().SearchAsync("source content", ["source"], topK: 1, ct: scope.CancellationToken);
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        var hit = result.Data!.Should().ContainSingle().Which;
        hit.KnowledgeBaseId.Should().Be("source");
        hit.DocumentId.Should().Be(DOCUMENT_ID);
        hit.ChunkIndex.Should().Be(0);
        hit.Text.Should().Be(content);
        hit.ScoreKind.Should().Be(TextSearchScoreKind.VectorSimilarity);
        hit.Score.Should().NotBeNull();
        hit.SimilarityScore.Should().Be(hit.Score);
    }

    private static IMarkdownDocumentCatalog CreateCatalog(bool includeSecondDocument = true)
    {
        var catalog = Substitute.For<IMarkdownDocumentCatalog>();
        var first = Document("first");
        var second = Document("second");
        catalog.GetAllDocumentGroupsAsync().Returns(new List<MarkdownDocumentGroup>
        {
            Group("first", first), Group("second", includeSecondDocument ? second : null)
        });
        catalog.GetDocumentsAsync("first").Returns(new List<MarkdownDocument> { first });
        catalog.GetDocumentsAsync("second").Returns(includeSecondDocument ? new List<MarkdownDocument> { second } : []);
        catalog.GetDocumentsAsync("unavailable").Returns(
            Task.FromException<List<MarkdownDocument>>(new KeyNotFoundException("Recorded group unavailable.")));
        catalog.GetDocumentContentAsync(first).Returns(FIRST_CONTENT);
        catalog.GetDocumentContentAsync(second).Returns(SECOND_CONTENT);
        // A real global-path fallback could select the unrelated first group. Make that collision return
        // valid content, so missing recorded owners cannot pass merely because an unconfigured seam fails.
        catalog.GetDocumentByPathAsync(DOCUMENT_ID).Returns(first);
        catalog.GetDocumentContentByPathAsync(DOCUMENT_ID).Returns(FIRST_CONTENT);
        return catalog;
    }

    private static MarkdownDocument Document(string group) => new()
    {
        GroupKey = group, Title = group, NavigationTitle = group,
        FilePath = Path.Combine(Path.GetTempPath(), group, DOCUMENT_ID), RelativePath = DOCUMENT_ID,
        NavigationRelativePath = DOCUMENT_ID, Level = 0, FileSize = 100,
        LastModifiedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static MarkdownDocumentGroup Group(string key, MarkdownDocument? document) => new()
    {
        Key = key, Title = key, BasePath = Path.Combine(Path.GetTempPath(), key), IsValid = true,
        DocumentCount = document is null ? 0 : 1, Documents = document is null ? [] : [document],
        RootNode = new TreeNode<MarkdownDocumentNodeData>(new MarkdownDocumentNodeData(key, IsDocument: false))
    };

    private sealed class SourceTestApplicationFactory(string root) : MonicaTestApplicationFactory<RAGSourceProvenanceTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddAI(options => options.StorageRootPath = root);
            builder.AddKnowledgeBase(options =>
            {
                options.DocumentIndexStateStoreFilePath = Path.Combine(root, "rag", "state.json");
                options.UploadedDocumentSourceRootPath = Path.Combine(root, "rag", "sources");
            });
            builder.AddRAG(options => options.ChunkerRoutingStoreFilePath = Path.Combine(root, "rag", "routes.json"))
                .UseVectorStoreInMemoryProvider().AddFakeEmbeddingsModel();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        }
    }
}
