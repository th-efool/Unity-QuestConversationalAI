# 02 — Multi-Backend RAG Engine & `<2ms` Local Vector Cache

> **Scope**: `com.convai.openai/Runtime/RAG/**`.
> **Update Rule**: If you modify vector providers, `IBulkExportProvider`, `RagLocalCache`, `RagLocalIndex`, `RagConversationBridge`, `RagConfig`, or `OpenAIEmbeddingClient`, update this file before completing your turn.

---

## 1. Architectural Overview (Zero-Network-Wait Local RAG)

To eliminate 1,000–2,500 ms of live remote vector DB + embedding round-trips during a voice conversation, the RAG subsystem pre-syncs the remote knowledge base into an in-memory + disk-persisted index (`RagLocalCache` + `RagLocalIndex`) at startup:

```text
Application Startup (RagConversationBridge.Register -> StartLocalCache)
   │
   ├──► Phase 1 (~5 ms): RagLocalCache loads local JSON snapshot from
   │                     Application.persistentDataPath/rag_cache_<backend>_<collection>.json
   │                     into RagLocalIndex (instant readiness before first user utterance!).
   │
   ├──► Phase 2 (Background): If _provider implements IBulkExportProvider (Qdrant, Pinecone,
   │                          Supabase, LocalBakedAsset), calls ExportAllAsync() to pull all
   │                          chunks + embeddings, atomic-swaps RagLocalIndex, and saves to disk.
   │
   └──► Phase 3 (Background): If warmUpEmbeddingsOnStart == true, batches up to 6 key phrases
                              in a single OpenAI /v1/embeddings call to prime the TLS connection
                              pool and populate the 64-entry LRU query embedding cache.

Runtime Query Execution (RagConversationBridge.ExecuteRetrieveContextAsync)
   │
   ├──► Path A — BM25 Lexical Fast-Path (< 0.5 ms, ZERO network calls):
   │             RagLocalIndex.SearchLexical(query) runs BM25 over tokenized chunks.
   │             If topBM25 >= lexicalFastPathThreshold (default 2.2), returns immediately!
   │
   ├──► Path B — Local Semantic Cosine Search (< 2 ms if query embedding cached, ~120 ms on warm socket):
   │             Gets query vector (from LRU cache or warm OpenAI /v1/embeddings socket) and runs
   │             L2-normalized dot-product over RagLocalIndex.SearchSemantic().
   │
   └──► Path C — Live Remote Provider Fallback:
                 Only used if enableLocalCache == false or the local index has 0 entries.
```

---

## 2. Core RAG Runtime Files & Exact Responsibilities

| File Path (under `com.convai.openai/Runtime/RAG/`) | Class / Interface | Exact Responsibility |
| :--- | :--- | :--- |
| `Models/RagConfig.cs` | `RagConfig : ScriptableObject` | Stores active `backendType` (`LocalBakedAsset`, `SupabasePgVector`, `Pinecone`, `Qdrant`), per-provider credential slots (`qdrant*`, `pinecone*`, `supabase*`), retrieval mode (`AgenticToolCall` vs `ProactiveContextInjection`), and local cache settings (`enableLocalCache`, `persistLocalCacheToDisk`, `warmUpEmbeddingsOnStart`, `lexicalFastPathThreshold`). |
| `Models/RagModels.cs` | `RagBackendType`, `RagRetrievalMode`, `DocumentChunk`, `RagSearchResult` | Core serializable data models and enums for chunks, metadata, and search results. |
| `Models/LocalVectorDatabaseSO.cs` | `LocalVectorDatabaseSO : ScriptableObject` | Offline ScriptableObject asset storing baked `List<DocumentChunk>` vectors inside the Unity project/build. |
| `Core/IVectorStoreProvider.cs` | `IVectorStoreProvider` | Base provider contract: `ProviderName`, `InitializeAsync`, `UpsertChunksAsync`, `DeleteAllAsync`, `QueryAsync`, `TestConnectionAsync`. |
| `Interfaces/IBulkExportProvider.cs` | `IBulkExportProvider` | Bulk sync contract: `Task<(List<DocumentChunk> chunks, string error)> ExportAllAsync(string apiKeyOverride = null)`. Implemented by **all 4 providers**. |
| `Core/VectorMath.cs` | `VectorMath` | SIMD-friendly cosine similarity, dot product, and L2 normalization utilities. |
| `Core/OpenAIEmbeddingClient.cs` | `OpenAIEmbeddingClient` | Calls `POST https://api.openai.com/v1/embeddings` (`text-embedding-3-small`, 1536 dims) for single (`GetEmbeddingAsync`) or batched (`GetEmbeddingsBatchAsync`) texts using `UnityWebRequest`. |
| `Providers/RagLocalIndex.cs` | `RagLocalIndex`, `RagIndexEntry` | Thread-safe, zero-dependency in-memory index. L2-normalizes vectors on construction for fast dot-product cosine similarity (`SearchSemantic`) and builds an inverted token dictionary with IDF weights for `<0.5 ms` BM25 search (`SearchLexical`). |
| `RuntimeBridge/RagLocalCache.cs` | `RagLocalCache` | Orchestrates disk snapshot load/save (`rag_cache_*.json`), background `IBulkExportProvider.ExportAllAsync()` sync, 64-entry LRU query embedding cache, startup batch warm-up (`WarmUpAsync`), and two-tier query routing (`QueryAsync`). |
| `RuntimeBridge/RagConversationBridge.cs` | `RagConversationBridge : MonoBehaviour` | Bridges `RealtimeConversationManager` and the RAG engine. Registers `search_knowledge_base` in `AgentToolRegistry` (Agentic mode) or hooks `onUserTranscript` with debounce (Proactive mode). Logs `[Latency] RAG search_knowledge_base: X ms`. |
| `Providers/VectorProviderFactory.cs` | `VectorProviderFactory` | Instantiates the active `IVectorStoreProvider` based on `RagConfig.backendType`. |
| `Providers/QdrantVectorProvider.cs` | `QdrantVectorProvider` | REST client for Qdrant Cloud (`/collections/{name}/points/scroll` with `with_vector: true` for `ExportAllAsync`, `/points/search` for live query). |
| `Providers/PineconeVectorProvider.cs` | `PineconeVectorProvider` | Dual-schema REST client for Pinecone Serverless: supports both standard Vector API (`2024-07`: `/vectors/list` + `/vectors/fetch`) and Document Schema API (`2026-07`: `/namespaces/{ns}/documents/list` + `/documents/fetch`). |
| `Providers/SupabaseVectorProvider.cs` | `SupabaseVectorProvider` | PostgREST + RPC client for Supabase `pgvector`. `ExportAllAsync` paginates `GET /rest/v1/{table}?select=...&limit=500&offset=...` and parses both `pgvector` text strings (`"[0.1,-0.2,...]"`) and `JArray` embeddings. |
| `Providers/LocalAssetVectorProvider.cs` | `LocalAssetVectorProvider` | Reads offline `LocalVectorDatabaseSO` assets. `ExportAllAsync` clones each `float[]` vector (`(float[])c.embedding.Clone()`) so `RagLocalIndex` in-place L2 normalization never mutates the Unity asset. |

---

## 3. Hard RAG Invariants (DO NOT BREAK)

1. **All Providers Must Implement `IBulkExportProvider`**:
   - Every `IVectorStoreProvider` registered in `VectorProviderFactory` must also implement `IBulkExportProvider.ExportAllAsync` returning `DocumentChunk` items with non-empty `embedding` arrays so `RagLocalCache` works identically across all backends.
2. **Never Mutate `LocalVectorDatabaseSO` Vectors In Place**:
   - `RagLocalIndex` L2-normalizes `chunk.embedding` in place during index construction. `LocalAssetVectorProvider.ExportAllAsync` must always clone `(float[])c.embedding.Clone()` so the serialized Unity asset is never modified at runtime.
3. **Pinecone Dual API-Version Support**:
   - Newer Pinecone indexes created with the `2026-07` Document Schema return `404` on `/vectors/list` and require `/namespaces/{ns}/documents/list` + `/documents/fetch` with `X-Pinecone-Api-Version: 2026-07`. Keep the automatic `2024-07` ➔ `2026-07` fallback in `PineconeVectorProvider.ExportAllAsync`.

---

## 4. Where to Butt In (Extension Points for Agents)

### Hook A: Adding a 5th Vector Store Backend (e.g., `Weaviate`, `Milvus`, `Chroma`)
1. Add the enum value to `RagBackendType` in `com.convai.openai/Runtime/RAG/Models/RagModels.cs`.
2. Create `com.convai.openai/Runtime/RAG/Providers/YourVectorProvider.cs` implementing **both** `IVectorStoreProvider` and `IBulkExportProvider`.
3. Register the new enum case in `com.convai.openai/Runtime/RAG/Providers/VectorProviderFactory.cs`.
4. Add credential fields and slot save/load logic in `com.convai.openai/Runtime/RAG/Models/RagConfig.cs`.
5. Expose the new fields in `com.convai.openai/Editor/RAG/RagConfigEditor.cs` and `RagKnowledgeBaseWindow.cs`.

### Hook B: Tuning BM25 vs. Semantic Search Thresholds
- Adjust `lexicalFastPathThreshold` on `RagConfig` (default `2.2f`):
  - Lower values (`1.5f`) favor `<0.5 ms` zero-network BM25 keyword matches.
  - Higher values (`3.5f`) or `0f` force semantic cosine vector search.


### Conversation and walkthrough behavior

RagConversationBridge agent tool results distinguish retrieved, no_sources and unavailable. Existing public SearchAsync callers retain list semantics. Use a source name and section only for a claim supported by the returned passage.
