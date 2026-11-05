using System;
using UnityEngine;
using OpenAI;

namespace OpenAI.RAG
{
    /// <summary>
    /// Configuration asset for Vector Database & RAG knowledge retrieval.
    /// Right-click in Assets/Resources/ -> Create -> OpenAI -> VectorDB Config
    /// </summary>
    [CreateAssetMenu(menuName = "OpenAI/VectorDB Config", fileName = "VectorDBConfig", order = 2)]
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

        [Header("Pre-Configured Cloud Vector Database Profiles")]
        [Header("1. Qdrant Cloud")]
        public string qdrantUrl = "https://e4aa181d-961a-4e8f-b5a5-118166e69d73.sa-east-1-0.aws.cloud.qdrant.io";
        public string qdrantApiKey = "";
        public string qdrantCollection = "documents";

        [Header("2. Pinecone Serverless")]
        public string pineconeIndexHost = "https://rag-provider-tests-ieyzlld.svc.aped-4627-b74a.pinecone.io";
        public string pineconeApiKey = "";
        public string pineconeNamespace = "rag-provider-tests";

        [Header("3. Supabase pgvector")]
        public string supabaseUrl = "https://hfiogejcvbknwhtvotfz.supabase.co";
        public string supabaseApiKey = "sb_publishable_HqUCMWNAaBI3wa6fRCanrg_9yia5-cr";
        public string supabaseTable = "rag_test_chunks";
        public string supabaseRpcFunction = "match_documents";

        private void OnValidate()
        {
            SyncActiveBackendFields();
        }

        public void SyncActiveBackendFields()
        {
            switch (backendType)
            {
                case VectorBackendType.Qdrant:
                    if (!string.IsNullOrWhiteSpace(qdrantUrl)) databaseUri = qdrantUrl;
                    if (!string.IsNullOrWhiteSpace(qdrantApiKey)) runtimeApiKey = qdrantApiKey;
                    if (!string.IsNullOrWhiteSpace(qdrantCollection)) collectionOrTableName = qdrantCollection;
                    break;

                case VectorBackendType.Pinecone:
                    if (!string.IsNullOrWhiteSpace(pineconeIndexHost)) databaseUri = pineconeIndexHost;
                    if (!string.IsNullOrWhiteSpace(pineconeApiKey)) runtimeApiKey = pineconeApiKey;
                    if (!string.IsNullOrWhiteSpace(pineconeNamespace)) collectionOrTableName = pineconeNamespace;
                    break;

                case VectorBackendType.SupabasePgVector:
                    if (!string.IsNullOrWhiteSpace(supabaseUrl)) databaseUri = supabaseUrl;
                    if (!string.IsNullOrWhiteSpace(supabaseApiKey)) runtimeApiKey = supabaseApiKey;
                    if (!string.IsNullOrWhiteSpace(supabaseTable)) collectionOrTableName = supabaseTable;
                    if (!string.IsNullOrWhiteSpace(supabaseRpcFunction)) supabaseRpcFunctionName = supabaseRpcFunction;
                    break;
            }
        }

        public string GetEffectiveDatabaseUri()
        {
            switch (backendType)
            {
                case VectorBackendType.Qdrant:
                    if (!string.IsNullOrWhiteSpace(qdrantUrl)) return qdrantUrl;
                    break;
                case VectorBackendType.Pinecone:
                    if (!string.IsNullOrWhiteSpace(pineconeIndexHost)) return pineconeIndexHost;
                    break;
                case VectorBackendType.SupabasePgVector:
                    if (!string.IsNullOrWhiteSpace(supabaseUrl)) return supabaseUrl;
                    break;
            }
            return databaseUri;
        }

        public string GetEffectiveCollectionOrTable()
        {
            switch (backendType)
            {
                case VectorBackendType.Qdrant:
                    if (!string.IsNullOrWhiteSpace(qdrantCollection)) return qdrantCollection;
                    break;
                case VectorBackendType.Pinecone:
                    if (!string.IsNullOrWhiteSpace(pineconeNamespace)) return pineconeNamespace;
                    break;
                case VectorBackendType.SupabasePgVector:
                    if (!string.IsNullOrWhiteSpace(supabaseTable)) return supabaseTable;
                    break;
            }
            return collectionOrTableName;
        }

        public string GetEffectiveRpcFunction()
        {
            if (backendType == VectorBackendType.SupabasePgVector && !string.IsNullOrWhiteSpace(supabaseRpcFunction))
                return supabaseRpcFunction;
            return supabaseRpcFunctionName;
        }

        [Header("Retrieval & Search Settings")]
        [Tooltip("Method used to retrieve and supply context to the conversation.")]
        public RagRetrievalMode retrievalMode = RagRetrievalMode.AgenticToolCall;

        [Range(1, 20)]
        [Tooltip("Maximum number of relevant document chunks to retrieve per search.")]
        public int topK = 3;

        [Range(0f, 1f)]
        [Tooltip("Minimum cosine similarity threshold (0.0 to 1.0). Matches below this score are discarded.")]
        public float similarityThreshold = 0.35f;

        [Header("Instant Retrieval (Local Cache)")]
        [Tooltip("Download the whole knowledge base at startup and search it in memory. Keyword hits need no network; semantic hits need only the query embedding.")]
        public bool enableLocalCache = true;

        [Tooltip("Keep a binary copy of the knowledge base in Application.persistentDataPath so the next launch is instant even offline.")]
        public bool persistLocalCacheToDisk = true;

        [Range(0.2f, 1f)]
        [Tooltip("A keyword (BM25) match is trusted without an embedding call when at least this fraction of the question's terms appear in the top chunk.")]
        public float lexicalConfidence = 0.6f;

        [Range(1, 10)]
        [Tooltip("Maximum chunks returned to the realtime model by the search tool. Fewer chunks means faster speech.")]
        public int toolMaxResults = 2;

        [Range(100, 2000)]
        [Tooltip("Maximum characters of each chunk returned to the realtime model by the search tool.")]
        public int toolMaxCharsPerChunk = 600;

        [Tooltip("Typical questions embedded once at startup. This primes the HTTPS connection and makes these queries instant.")]
        public string[] warmupQueries = new string[]
        {
            "mechanical seal torque specification",
            "impeller clearance inspection",
            "bearing lubrication oil",
            "shaft coupling alignment",
            "pump troubleshooting leakage"
        };

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
        /// Resolves the effective OpenAI API key from the referenced OpenAIConfig asset, Resources, or environment.
        /// </summary>
        public string EffectiveOpenAIApiKey
        {
            get
            {
                if (openAIConfig != null && !string.IsNullOrWhiteSpace(openAIConfig.apiKey))
                {
                    return openAIConfig.apiKey;
                }

                var loaded = Resources.Load<OpenAIConfig>("OpenAIConfig");
                if (loaded != null && !string.IsNullOrWhiteSpace(loaded.apiKey))
                {
                    return loaded.apiKey;
                }

                string envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (!string.IsNullOrWhiteSpace(envKey))
                {
                    return envKey;
                }

                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the effective API key for vector store queries (prefers runtimeApiKey, falls back to override).
        /// </summary>
        public string GetEffectiveBackendApiKey(string overrideKey = null)
        {
            if (!string.IsNullOrEmpty(overrideKey)) return overrideKey;

            switch (backendType)
            {
                case VectorBackendType.Qdrant:
                    if (!string.IsNullOrWhiteSpace(qdrantApiKey)) return qdrantApiKey;
                    break;
                case VectorBackendType.Pinecone:
                    if (!string.IsNullOrWhiteSpace(pineconeApiKey)) return pineconeApiKey;
                    break;
                case VectorBackendType.SupabasePgVector:
                    if (!string.IsNullOrWhiteSpace(supabaseApiKey)) return supabaseApiKey;
                    break;
            }

            return runtimeApiKey;
        }
    }
}
