using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAI.RAG
{
    /// <summary>
    /// Common abstraction for vector storage and similarity retrieval providers.
    /// Supports local offline storage (ScriptableObject) and remote providers (Supabase, Pinecone, Qdrant).
    /// </summary>
    public interface IVectorStoreProvider
    {
        /// <summary>
        /// The vector database backend type represented by this provider.
        /// </summary>
        VectorBackendType BackendType { get; }

        /// <summary>
        /// Upserts a batch of document chunks with their embeddings into the store.
        /// </summary>
        /// <param name="chunks">List of document chunks to store.</param>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>Tuple indicating success and error description if failed.</returns>
        Task<(bool ok, string error)> UpsertChunksAsync(IReadOnlyList<DocumentChunk> chunks, string apiKeyOverride = null);

        /// <summary>
        /// Queries the store for nearest chunks based on cosine similarity with the query embedding.
        /// </summary>
        /// <param name="queryEmbedding">The embedding vector of the search query.</param>
        /// <param name="topK">Maximum number of results to return.</param>
        /// <param name="minScore">Minimum similarity score threshold (0.0 to 1.0).</param>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>Ranked search results and error description if failed.</returns>
        Task<(List<RagSearchResult> results, string error)> QueryAsync(float[] queryEmbedding, int topK, float minScore, string apiKeyOverride = null);

        /// <summary>
        /// Deletes all indexed chunks from the collection or table.
        /// </summary>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>Tuple indicating success and error description if failed.</returns>
        Task<(bool ok, string error)> ClearAllAsync(string apiKeyOverride = null);

        /// <summary>
        /// Tests connectivity and credentials against the vector store backend.
        /// </summary>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>Tuple indicating success and a status/diagnostic message.</returns>
        Task<(bool ok, string message)> TestConnectionAsync(string apiKeyOverride = null);
    }
}
