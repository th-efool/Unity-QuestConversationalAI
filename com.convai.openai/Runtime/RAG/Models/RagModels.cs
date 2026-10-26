using System;
using UnityEngine;

namespace OpenAI.RAG
{
    /// <summary>
    /// Supported vector database backends for RAG knowledge retrieval.
    /// </summary>
    public enum VectorBackendType
    {
        /// <summary>
        /// Offline vector store baked directly into a Unity ScriptableObject asset.
        /// Zero network dependencies; works fully offline on mobile / Quest.
        /// </summary>
        LocalBakedAsset = 0,

        /// <summary>
        /// Cloud PostgreSQL database using pgvector extension via Supabase PostgREST RPC.
        /// </summary>
        SupabasePgVector = 1,

        /// <summary>
        /// Pinecone serverless vector index via REST API.
        /// </summary>
        Pinecone = 2,

        /// <summary>
        /// Qdrant vector database via HTTP REST API.
        /// </summary>
        Qdrant = 3
    }

    /// <summary>
    /// Operating mode for how retrieved RAG context is supplied to the OpenAI Realtime model.
    /// </summary>
    public enum RagRetrievalMode
    {
        /// <summary>
        /// Registers a client-side tool (e.g. search_knowledge_base) with OpenAI Realtime.
        /// The model decides autonomously when to call the tool based on user queries.
        /// </summary>
        AgenticToolCall = 0,

        /// <summary>
        /// Proactively runs vector search when user finishes speaking (speech transcript done)
        /// and injects the retrieved context directly into the conversation stream.
        /// </summary>
        ProactiveContextInjection = 1
    }

    /// <summary>
    /// Represents an individual ingested chunk of text with its corresponding embedding vector and metadata.
    /// </summary>
    [Serializable]
    public class DocumentChunk
    {
        [Tooltip("Unique identifier for this chunk (e.g. documentId_chunkIndex).")]
        public string id;

        [Tooltip("Identifier for the parent document.")]
        public string documentId;

        [Tooltip("Original file name or URI source of this chunk.")]
        public string sourceName;

        [Tooltip("Zero-based index of this chunk within the source document.")]
        public int chunkIndex;

        [TextArea(3, 8)]
        [Tooltip("Extracted text content of the chunk.")]
        public string text;

        [Tooltip("Normalized embedding vector for this chunk.")]
        public float[] embedding;

        [TextArea(1, 3)]
        [Tooltip("Optional serialized JSON metadata (page numbers, section headers, timestamps).")]
        public string metadataJson;

        public DocumentChunk()
        {
        }

        public DocumentChunk(string id, string documentId, string sourceName, int chunkIndex, string text, float[] embedding = null, string metadataJson = null)
        {
            this.id = id;
            this.documentId = documentId;
            this.sourceName = sourceName;
            this.chunkIndex = chunkIndex;
            this.text = text;
            this.embedding = embedding;
            this.metadataJson = metadataJson ?? string.Empty;
        }

        public override string ToString()
        {
            var preview = !string.IsNullOrEmpty(text) 
                ? text.Substring(0, Math.Min(60, text.Length)).Replace("\n", " ") 
                : string.Empty;
            return $"[{sourceName} #{chunkIndex}] {preview}...";
        }
    }

    /// <summary>
    /// Represents a similarity search match returned from a vector database query.
    /// </summary>
    [Serializable]
    public class RagSearchResult
    {
        [Tooltip("The matched document chunk.")]
        public DocumentChunk chunk;

        [Tooltip("Similarity score (typically cosine similarity between 0.0 and 1.0).")]
        public float score;

        public RagSearchResult()
        {
        }

        public RagSearchResult(DocumentChunk chunk, float score)
        {
            this.chunk = chunk;
            this.score = score;
        }

        public override string ToString()
        {
            return $"[Score: {score:F3}] {chunk?.sourceName} (#{chunk?.chunkIndex})";
        }
    }
}
