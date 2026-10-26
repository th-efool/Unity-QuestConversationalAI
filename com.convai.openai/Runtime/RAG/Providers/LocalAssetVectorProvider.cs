using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace OpenAI.RAG
{
    /// <summary>
    /// Local offline vector store provider that performs in-memory cosine similarity search
    /// over a pre-computed <see cref="LocalVectorDatabaseSO"/> ScriptableObject.
    /// Operates 100% offline with zero external network dependencies on client devices.
    /// </summary>
    public class LocalAssetVectorProvider : IVectorStoreProvider
    {
        private readonly RagConfig _config;

        /// <summary>
        /// The vector database backend type represented by this provider.
        /// </summary>
        public VectorBackendType BackendType => VectorBackendType.LocalBakedAsset;

        /// <summary>
        /// Initializes a new instance of <see cref="LocalAssetVectorProvider"/>.
        /// </summary>
        /// <param name="config">RAG configuration asset containing the local database reference.</param>
        public LocalAssetVectorProvider(RagConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Upserts a batch of document chunks into the configured LocalVectorDatabaseSO ScriptableObject.
        /// When executed within the Unity Editor, marks the asset dirty and saves assets to disk.
        /// </summary>
        public Task<(bool ok, string error)> UpsertChunksAsync(IReadOnlyList<DocumentChunk> chunks, string apiKeyOverride = null)
        {
            if (_config == null || _config.localDatabase == null)
            {
                return Task.FromResult((false, "Local database ScriptableObject is not assigned in RagConfig."));
            }

            if (chunks == null || chunks.Count == 0)
            {
                return Task.FromResult((true, (string)null));
            }

            try
            {
                _config.localDatabase.UpsertChunks(chunks);

#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(_config.localDatabase);
                UnityEditor.AssetDatabase.SaveAssets();
#endif
                return Task.FromResult((true, (string)null));
            }
            catch (Exception ex)
            {
                return Task.FromResult((false, $"Failed to upsert chunks into local database: {ex.Message}"));
            }
        }

        /// <summary>
        /// Queries the local offline database for document chunks most similar to the query embedding.
        /// Computes cosine similarity across all chunks, filters by minimum score, sorts descending,
        /// and returns up to topK results.
        /// </summary>
        public Task<(List<RagSearchResult> results, string error)> QueryAsync(
            float[] queryEmbedding,
            int topK,
            float minScore,
            string apiKeyOverride = null)
        {
            var results = new List<RagSearchResult>();

            if (_config == null || _config.localDatabase == null)
            {
                return Task.FromResult((results, (string)null));
            }

            var chunks = _config.localDatabase.chunks;
            if (chunks == null || chunks.Count == 0 || queryEmbedding == null || queryEmbedding.Length == 0)
            {
                return Task.FromResult((results, (string)null));
            }

            int targetTopK = topK > 0 ? topK : (_config.topK > 0 ? _config.topK : 3);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (chunk == null || chunk.embedding == null || chunk.embedding.Length == 0)
                {
                    continue;
                }

                float score = VectorMath.CosineSimilarity(queryEmbedding, chunk.embedding);
                if (score >= minScore)
                {
                    results.Add(new RagSearchResult
                    {
                        chunk = chunk,
                        score = score
                    });
                }
            }

            // Sort descending by similarity score
            results.Sort((a, b) => b.score.CompareTo(a.score));

            if (results.Count > targetTopK)
            {
                results = results.GetRange(0, targetTopK);
            }

            return Task.FromResult((results, (string)null));
        }

        /// <summary>
        /// Clears all chunks stored in the local database ScriptableObject.
        /// </summary>
        public Task<(bool ok, string error)> ClearAllAsync(string apiKeyOverride = null)
        {
            if (_config == null || _config.localDatabase == null)
            {
                return Task.FromResult((false, "Local database ScriptableObject is not assigned in RagConfig."));
            }

            try
            {
                _config.localDatabase.Clear();

#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(_config.localDatabase);
                UnityEditor.AssetDatabase.SaveAssets();
#endif
                return Task.FromResult((true, (string)null));
            }
            catch (Exception ex)
            {
                return Task.FromResult((false, $"Failed to clear local database: {ex.Message}"));
            }
        }

        /// <summary>
        /// Tests if the local database asset is assigned and reports current chunk count.
        /// </summary>
        public Task<(bool ok, string message)> TestConnectionAsync(string apiKeyOverride = null)
        {
            if (_config == null)
            {
                return Task.FromResult((false, "RagConfig is null."));
            }

            if (_config.localDatabase == null)
            {
                return Task.FromResult((false, "Local database ScriptableObject is not assigned in RagConfig."));
            }

            if (_config.localDatabase.chunks == null)
            {
                _config.localDatabase.chunks = new List<DocumentChunk>();
            }

            return Task.FromResult((true, $"Local database ready with {_config.localDatabase.chunks.Count} chunks."));
        }
    }
}
