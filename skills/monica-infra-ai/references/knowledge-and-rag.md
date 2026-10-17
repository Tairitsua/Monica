# Knowledge inventory and semantic retrieval

`AddKnowledgeBase()` provides knowledge-base metadata and document inventory without requiring embeddings or a vector store. It brings in the skill system and Markdown services. Its default document-index state and source-content stores are file-backed under `monica_data/rag/`, relative to the running application directory; replace them through the knowledge-base registration extensions only when the host needs another store. `KnowledgeBaseFacade` creates and manages bases, and `KnowledgeDocumentFacade` uploads or imports documents, previews source, and reports inventory.

For semantic search, register `AddRAG()` and choose exactly the vector-store implementation the deployment can operate. `UseVectorStoreInMemoryProvider()` is suitable for local work; `UseVectorStoreQdrantProvider(...)` or `UseVectorStoreProvider<TVectorStore>()` supplies an external/custom store. RAG composition fails if no vector-store feature is selected. In-memory vectors disappear on restart even though document metadata and source files persist. `AddRAG()` brings in knowledge-base and Markdown modules; an AI provider and matching embedding model are still required for real indexing. `AddFakeEmbeddingsModel(...)` exercises the normal AI path in development and tests.

The built-in vector providers use `Microsoft.Extensions.VectorData` contracts with `CommunityToolkit.VectorData.InMemory` and `CommunityToolkit.VectorData.Qdrant`. Read the current versions from `Monica.AI.csproj`; examples using the former Semantic Kernel vector connectors are obsolete. Provider selection does not change the lifetime of the source store or make in-memory vectors durable. When investigating protocol compatibility, inspect the installed connector and `Qdrant.Client` versions together, then exercise a real collection and search through the facade.

```csharp
builder.AddMonica(monica =>
{
    monica.AddRAG()
        .UseVectorStoreInMemoryProvider()
        .AddFakeEmbeddingsModel();
});
```

For real embeddings, register an AI provider that advertises an embedding model. Inside the same `AddMonica(...)` callback, the following adds a separate provider alongside any existing chat provider; select the deployment's vector store through the methods above:

```csharp
monica.AddAI().AddOpenAIProvider(options =>
{
    options.ApiKey = builder.Configuration["AI:EmbeddingApiKey"]
        ?? throw new InvalidOperationException("Embedding API key is required.");
    options.SupportedModels = ["text-embedding-3-small"];
}, providerId: "embeddings");
```

`text-embedding-3-small` is an embedding entry in Monica's reserved model catalog. For a custom model, register matching `EmbeddingModelInfo` through `AddModel(...)` before referencing it. Advertising only chat models does not supply an embedding generator. Use provider ID `embeddings` and the advertised model name in step 2 below; registering the provider does not select it for a knowledge base. Registration and model-kind checks are in `Monica.AI/Modules/ModuleAI.cs`, `Monica.AI/Providers/OpenAI/OpenAIReservedModels.cs`, and `Monica.AI/Providers/OpenAI/OpenAIProvider.cs`.

The application workflow is:

1. Create a base with `KnowledgeBaseFacade.CreateAsync(id, name, description)` and check the `Res` result. Import selected Markdown documents with `KnowledgeDocumentFacade.ImportMarkdownDocumentsAsync(...)` after configuring a Markdown document group, or use `UploadDocumentAsync(...)` for source text. Uploads and imports add pending inventory; they do not index vectors.
2. Select an available embedding provider/model for that base with `EmbeddingModelFacade.GetEmbeddingModelsAsync()` and `SetKnowledgeBaseEmbeddingModelAsync(kbId, providerId, modelName)`. The binding belongs to the knowledge base. Changing it with the default `clearIndex: true` clears existing index state and requires reindexing.
3. Read document IDs from `KnowledgeDocumentFacade.GetDocumentInventoryAsync(kbId)`. Use `RAGIndexingFacade.IndexDocumentAsync(kbId, documentId)` for one queued document or `StartBatchAsync(kbId)` for a tracked batch. `QueueDocumentForReindexAsync` and `QueueKnowledgeBaseForReindexAsync` reset existing work when content or indexing policy changes. Inspect each returned `Res`; a pending or failed document is not searchable merely because it appears in inventory.
4. Query with scoped `RAGSearchFacade.SearchAsync(query, knowledgeBaseIds, topK, ct: cancellationToken)`. It returns `Res<IReadOnlyList<TextSearchResult>>`; the default `topK` is 5. Pass only the bases the caller may search. `RAGIndexingFacade.GetDocumentChunksAsync(...)` helps inspect source/chunk correspondence when results look wrong.

```csharp
var search = await ragSearch.SearchAsync(
    "How are orders approved?", ["orders"], ct: cancellationToken);
if (search.IsFailed(out var error, out var hits))
    throw new InvalidOperationException(error.Message);
foreach (var hit in hits)
    Console.WriteLine(hit);
```

## Source identity and retrieval evidence

Keep the knowledge-base ID, document ID, `SourceKind`, and Markdown `SourceGroupKey` together when diagnosing imports, indexing, and chunk inspection. Markdown paths are relative to their recorded group; the same path in another group is a different source. Batch indexing and chunk inspection resolve that exact group, and an unavailable recorded source fails instead of borrowing a global path match. Uploaded content belongs to the base's source store and must not resolve through a Markdown catalog merely because its document ID matches. Inspect source preview as well as indexing when changing these paths; provenance captions alone do not prove the body came from the named source.

`TextSearchResult.ScoreKind` describes `Score`: `VectorSimilarity` is the vector search score, while `HybridRank` is the fused ranking score and must not become a similarity percentage. `SimilarityScore` carries a separate vector score when available for a hybrid result; a missing value stays unavailable. A retrieval page must retain the submitted query, base IDs, top K, and query embedding override with its results. Editing current controls does not change an earlier result's request scope. `RAGSearchEmbeddingOverride` changes query-vector generation for that request; it does not rebind or reindex a base, and its dimensions must match each searched base's binding.

`RAGVectorStoreFacade.ValidateAsync(kbId)` checks representative persisted keys: the first and, for multi-chunk documents, last chunk of each recorded `Done` document. `WasValidated`, `HasPersistedIndexMetadata`, and missing keys distinguish checked vectors, unavailable validation, and no indexed metadata. A successful representative check is not an exhaustive vector audit. Inventory, recorded chunk counts, vector availability, and a successful search are separate evidence; after an in-memory restart, preserve durable sources and use native reindex actions to repair missing vectors.

## Page-owned management state

`KnowledgeBaseManagePageStateFactory.Create(dispatch)` and `RAGManagePageStateFactory.Create(dispatch)` create independent state for each routed visit. Pass the page's renderer dispatch, such as `action => InvokeAsync(action)`, subscribe to the awaited `Func<Task>` state-change event, and detach it before awaiting state disposal. The page implements `IAsyncDisposable` and owns the returned state and private RAG poller; injected facades and UI services remain owned by the container. Migrate former direct scoped-state injection to these factories rather than manually disposing circuit-owned state.

Retirement rejects new work, cancels and drains actual producers and renderer publication, and closes owned dialogs. Selection generation checks reject late data even when selection changes A → B → A. Commands capture the originating base/document before confirmation or other awaits. `IsRetired` lets a page-local focus wrapper reject a late focus request after awaited selection; it must also confirm that the intended base remains selected. `ReloadSelectedInventoryAsync()` retries the inventory through the same tracked lifetime. Catalog, inventory/queue, and vector-validation errors remain explicit; an unavailable read must not be presented as an empty successful result. Successful selected inventory and queue refreshes reload persisted base metadata by ID rather than inferring aggregate chunks from filtered or preview rows.

An admitted indexing batch is owned by the host coordinator. Leaving its page stops that visit's polling and publication, but does not cancel the batch. The management page starts it without linking the route token and retains the explicit batch-cancel action. Keep cancellable visit reads separate from admitted durable indexing commands; drain a single-document command's actual producer after retirement while suppressing retired UI updates.

When reopening a running batch, the first vector validation may describe only the documents already indexed. The page's existing poller takes a final inventory receipt after the batch settles and refreshes vector evidence once for that still-current generation. Do not treat an earlier successful validation as the completed batch's count, or add another polling loop for this refresh.

`AddKnowledgeBaseUI()` contributes `ModuleUIOption.EnableMarkdown = true` because its source previews render recorded Markdown through the shared UI foundation's Markdown services. `AddRAGUI()` requires that UI module transitively, so chunk previews receive the same services. Registering the backend `AddMarkdown()` document catalog alone does not register `IMudMarkdownThemeService`.

`AddKnowledgeBaseUI()` supplies inventory pages but also requires `ModuleRAG`; `AddRAGUI()` adds indexing/search pages and depends on knowledge-base UI. Both UI paths therefore need a vector-store selection even when the immediate page is inventory. `AddKnowledgeBase()` alone is the vector-free inventory path. UI registrations do not choose the provider, embedding model, or persistence medium. Use `$monica-infra-ui` for shell and page access. To let a chat turn's knowledge tools consider selected bases, pass an `AIChatRuntimeContext` containing `KnowledgeBaseChatRuntimeContextKeys.KnowledgeSelection` and a `KnowledgeBaseSelection(ids)` to `ChatFacade.CreateSessionAsync(...)`, or call `UpdateRuntimeContext(...)` before the next turn. The AI UI builds this context from its selected base IDs. Document inventory alone does not make a chat turn retrieve from it.

Source: `Monica.AI/Modules/ModuleKnowledgeBase.cs` and `ModuleRAG.cs`; `Monica.AI/KnowledgeBase/Facades/KnowledgeBaseFacade.cs` and `KnowledgeDocumentFacade.cs`; `Monica.AI/RAG/Facades/EmbeddingModelFacade.cs`, `RAGIndexingFacade.cs`, `RAGVectorStoreFacade.cs`, and `RAGSearchFacade.cs`; `Monica.AI/RAG/Services/Support/MarkdownDocumentResolver.cs`, `BatchIndexCoordinator.cs`, and `ChunkViewCoordinator.cs`; `Monica.AI/RAG/Services/RAGSearchService.cs` and `RAGVectorStoreService.cs`; `Monica.AI/KnowledgeBase/Models/KnowledgeBaseChatRuntimeContextKeys.cs`; `Monica.AI.UI/UIKnowledgeBase/State/KnowledgeBaseManagePageStateFactory.cs`; `Monica.AI.UI/UIRAG/State/RAGManagePageStateFactory.cs`; `Monica.AI.UI/Shared/Support/KnowledgePageSession.cs`; and `Monica.AI.UI/Pages/RAGDebugPage.razor.cs`.
