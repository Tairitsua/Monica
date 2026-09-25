# Knowledge inventory and semantic retrieval

`AddKnowledgeBase()` provides knowledge-base metadata and document inventory without requiring embeddings or a vector store. It brings in the skill system and Markdown services. Its default document-index state and source-content stores are file-backed under `monica_data/rag/`, relative to the running application directory; replace them through the knowledge-base registration extensions only when the host needs another store. `KnowledgeBaseFacade` creates and manages bases, and `KnowledgeDocumentFacade` uploads or imports documents, previews source, and reports inventory.

For semantic search, register `AddRAG()` and choose exactly the vector-store implementation the deployment can operate. `UseVectorStoreInMemoryProvider()` is suitable for local work; `UseVectorStoreQdrantProvider(...)` or `UseVectorStoreProvider<TVectorStore>()` supplies an external/custom store. RAG composition fails if no vector-store feature is selected. In-memory vectors disappear on restart even though document metadata and source files persist. `AddRAG()` brings in knowledge-base and Markdown modules; an AI provider and matching embedding model are still required for real indexing. `AddFakeEmbeddingsModel(...)` exercises the normal AI path in development and tests.

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

`AddKnowledgeBaseUI()` supplies inventory pages but also requires `ModuleRAG`; `AddRAGUI()` adds indexing/search pages and depends on knowledge-base UI. Both UI paths therefore need a vector-store selection even when the immediate page is inventory. `AddKnowledgeBase()` alone is the vector-free inventory path. UI registrations do not choose the provider, embedding model, or persistence medium. Use `$monica-infra-ui` for shell and page access. To let a chat turn's knowledge tools consider selected bases, pass an `AIChatRuntimeContext` containing `KnowledgeBaseChatRuntimeContextKeys.KnowledgeSelection` and a `KnowledgeBaseSelection(ids)` to `ChatFacade.CreateSessionAsync(...)`, or call `UpdateRuntimeContext(...)` before the next turn. The AI UI builds this context from its selected base IDs. Document inventory alone does not make a chat turn retrieve from it.

Source and checks: `Monica.AI/Modules/ModuleKnowledgeBase.cs` and `ModuleRAG.cs`; `Monica.AI/KnowledgeBase/Facades/KnowledgeBaseFacade.cs` and `KnowledgeDocumentFacade.cs`; `Monica.AI/RAG/Facades/EmbeddingModelFacade.cs`, `RAGIndexingFacade.cs`, and `RAGSearchFacade.cs`; `Monica.AI/KnowledgeBase/Models/KnowledgeBaseChatRuntimeContextKeys.cs`; `Monica.AI.UI/UIKnowledgeBase/Modules/ModuleKnowledgeBaseUI.cs`; `Monica.AI.UI/UIChat/State/ChatPageState.RuntimeContext.cs`; and `Monica.AI.UI/UIRAG/State/RAGManagePageState.Indexing.cs`.
