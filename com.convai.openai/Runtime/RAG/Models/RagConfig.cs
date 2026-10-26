using System;
using UnityEngine;
using OpenAI;

namespace OpenAI.RAG
{
    /// <summary>
    /// Configuration asset for OpenAI-powered Retrieval-Augmented Generation (RAG).
    /// Configures embeddings, vector storage backends (Local, Supabase, Pinecone, Qdrant),
    /// retrieval parameters, and OpenAI Realtime tool definition.
    /// </summary>
    [CreateAssetMenu(menuName = "OpenAI/RAG/RAG Configuration", fileName = "RagConfig")]
    public class RagConfig : ScriptableObject
    {
        [Header("OpenAI Authentication")]
        [Tooltip("Reference to the project OpenAIConfig ScriptableObject (reuses API key & settings).")]
        public OpenAIConfig openAIConfig;

        [Header("Embeddings Settings")]
        [Tooltip("OpenAI embeddings REST endpoint URL.")]
        public string embeddingEndpoint = "https://api.openai.com/v1/embeddings";

        [Tooltip("OpenAI embedding model identifier.")]
        public string embeddingModel = "text-embedding-3-small";

        [Tooltip("Vector dimensionality of the embedding model (1536 for text-embedding-3-small).")]
        public int dimensions = 1536;

        [Header("Vector Store Backend")]
        [Tooltip("Target vector storage backend provider.")]
        public VectorBackendType backendType = VectorBackendType.LocalBakedAsset;

        [Tooltip("Reference to a LocalVectorDatabaseSO asset (used when backendType is LocalBakedAsset).")]
        public LocalVectorDatabaseSO localDatabase;

        [Tooltip("REST URI or base URL for external vector database (Supabase, Pinecone, or Qdrant).")]
        public string databaseUri = "";

        [Tooltip("Table name (Supabase), index namespace (Pinecone), or collection name (Qdrant).")]
        public string collectionOrTableName = "documents";

        [Tooltip("PostgreSQL RPC stored function name for similarity search in Supabase.")]
        public string supabaseRpcFunctionName = "match_documents";

        [Tooltip("Runtime read/query API key for cloud vector databases (e.g. Supabase anon key, Pinecone read key). Admin/write keys can be entered safely in the Editor Window.")]
        public string runtimeApiKey = "";

        [Header("Retrieval & Search Settings")]
        [Tooltip("Method used to retrieve and supply context to the conversation.")]
        public RagRetrievalMode retrievalMode = RagRetrievalMode.AgenticToolCall;

        [Range(1, 20)]
        [Tooltip("Maximum number of relevant document chunks to retrieve per search.")]
        public int topK = 3;

        [Range(0f, 1f)]
        [Tooltip("Minimum cosine similarity threshold (0.0 to 1.0). Matches below this score are discarded.")]
        public float similarityThreshold = 0.35f;

        [Header("Realtime Tool Definition")]
        [Tooltip("Function name registered in AgentToolRegistry for autonomous tool calling.")]
        public string toolName = "search_knowledge_base";

        [TextArea(2, 4)]
        [Tooltip("Description provided to the OpenAI Realtime model explaining what knowledge can be searched.")]
        public string toolDescription = "Searches the domain knowledge base for technical documentation, troubleshooting procedures, manuals, and factual reference context.";

        [Header("Document Ingestion & Chunking")]
        [Range(100, 4000)]
        [Tooltip("Target character length per document chunk.")]
        public int chunkSize = 600;

        [Range(0, 500)]
        [Tooltip("Overlap in characters between consecutive chunks to maintain context continuity.")]
        public int chunkOverlap = 100;

        [Range(1, 100)]
        [Tooltip("Number of chunks to send per OpenAI embedding batch request.")]
        public int embeddingBatchSize = 50;

        /// <summary>
        /// Resolves the effective OpenAI API key from the referenced OpenAIConfig asset.
        /// </summary>
        public string EffectiveOpenAIApiKey => openAIConfig != null ? openAIConfig.apiKey : string.Empty;

        /// <summary>
        /// Returns the effective API key for vector store queries (prefers runtimeApiKey, falls back to override).
        /// </summary>
        public string GetEffectiveBackendApiKey(string overrideKey = null)
        {
            if (!string.IsNullOrEmpty(overrideKey)) return overrideKey;
            return runtimeApiKey;
        }
    }
}
