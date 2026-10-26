using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenAI.RAG
{
    /// <summary>
    /// ScriptableObject storing pre-computed document chunks and vector embeddings.
    /// Enables 100% offline, local cosine-similarity RAG search directly on client devices (e.g. Meta Quest, mobile).
    /// </summary>
    [CreateAssetMenu(menuName = "OpenAI/RAG/Local Vector Database", fileName = "LocalVectorDatabase")]
    public class LocalVectorDatabaseSO : ScriptableObject
    {
        [Tooltip("The embedding model used when generating embeddings for these chunks.")]
        public string embeddingModel = "text-embedding-3-small";

        [Tooltip("Vector dimensionality of the stored embeddings (1536 for text-embedding-3-small).")]
        public int dimensions = 1536;

        [Tooltip("List of ingested document chunks with pre-calculated embedding vectors.")]
        public List<DocumentChunk> chunks = new List<DocumentChunk>();

        /// <summary>
        /// Total number of stored chunks.
        /// </summary>
        public int Count => chunks != null ? chunks.Count : 0;

        /// <summary>
        /// Upserts (adds or updates) a collection of document chunks, deduplicating by chunk id.
        /// </summary>
        /// <param name="newChunks">The chunks to insert or update.</param>
        public void UpsertChunks(IEnumerable<DocumentChunk> newChunks)
        {
            if (newChunks == null) return;
            if (chunks == null) chunks = new List<DocumentChunk>();

            var indexMap = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i] != null && !string.IsNullOrEmpty(chunks[i].id))
                {
                    indexMap[chunks[i].id] = i;
                }
            }

            foreach (var chunk in newChunks)
            {
                if (chunk == null) continue;

                if (!string.IsNullOrEmpty(chunk.id) && indexMap.TryGetValue(chunk.id, out int existingIndex))
                {
                    chunks[existingIndex] = chunk;
                }
                else
                {
                    if (!string.IsNullOrEmpty(chunk.id))
                    {
                        indexMap[chunk.id] = chunks.Count;
                    }
                    chunks.Add(chunk);
                }
            }
        }

        /// <summary>
        /// Removes all chunks associated with a specific document source name.
        /// </summary>
        /// <param name="sourceName">File name or source identifier to purge.</param>
        /// <returns>Number of chunks removed.</returns>
        public int RemoveBySource(string sourceName)
        {
            if (chunks == null || string.IsNullOrEmpty(sourceName)) return 0;
            int initialCount = chunks.Count;
            chunks.RemoveAll(c => c != null && string.Equals(c.sourceName, sourceName, StringComparison.OrdinalIgnoreCase));
            return initialCount - chunks.Count;
        }

        /// <summary>
        /// Clears all document chunks from this local database.
        /// </summary>
        public void Clear()
        {
            if (chunks != null)
            {
                chunks.Clear();
            }
            else
            {
                chunks = new List<DocumentChunk>();
            }
        }
    }
}
