using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OpenAI.RAG
{
    /// <summary>
    /// Mirrors the remote vector store into memory at startup so retrieval needs no network round trip:
    /// keyword (BM25) hits answer in well under a millisecond, and semantic hits only need one embedding call
    /// (skipped entirely for warm-up and repeated queries). A binary copy is kept on disk so the next launch
    /// is instant even before the remote refresh finishes.
    /// </summary>
    public sealed class RagLocalCache
    {
        private const int MaxCachedQueries = 256;

        private readonly RagConfig _config;
        private readonly Dictionary<string, float[]> _queryEmbeddings = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private RagLocalIndex _index;
        private string _fingerprint;
        private string _cachePath;

        /// <summary>Human readable state: idle, loading, ready (disk), ready (remote), unavailable, empty.</summary>
        public string Status { get; private set; } = "idle";

        /// <summary>True when an index with at least one chunk is loaded.</summary>
        public bool IsReady
        {
            get { var i = _index; return i != null && i.Count > 0; }
        }

        /// <summary>Number of chunks currently held in memory.</summary>
        public int Count
        {
            get { var i = _index; return i != null ? i.Count : 0; }
        }

        public RagLocalCache(RagConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Loads the disk copy (if any), refreshes from the remote store, then primes the embedding connection.
        /// Safe to fire and forget; never throws for network problems.
        /// </summary>
        public async Task InitializeAsync(IVectorStoreProvider provider, OpenAIEmbeddingClient embedder)
        {
            var exporter = provider as IBulkExportProvider;
            if (exporter == null)
            {
                Status = "unavailable";
                return;
            }

            _fingerprint = _config.backendType + "|" + _config.GetEffectiveDatabaseUri() + "|" +
                           _config.GetEffectiveCollectionOrTable() + "|" + _config.dimensions + "|" + _config.embeddingModel;
            _cachePath = Path.Combine(Application.persistentDataPath, "rag_cache_" + HashHex(_fingerprint) + ".bin");

            string path = _cachePath;
            string fp = _fingerprint;
            bool persist = _config.persistLocalCacheToDisk;
            var sw = Stopwatch.StartNew();
            Status = "loading";

            if (persist)
            {
                var onDisk = await Task.Run(() => RagLocalIndex.Load(path, fp));
                if (onDisk != null && onDisk.Count > 0)
                {
                    _index = await Task.Run(() => new RagLocalIndex(onDisk));
                    Status = "ready (disk)";
                    Debug.Log($"[OpenAI.RAG] Local cache loaded from disk: {_index.Count} chunk(s) in {sw.ElapsedMilliseconds} ms.");
                }
            }

            // Always refresh from the remote store (stale-while-revalidate).
            var (chunks, error) = await exporter.ExportAllAsync();
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogWarning($"[OpenAI.RAG] Local cache refresh failed: {error}");
                Status = IsReady ? "ready (disk, refresh failed)" : "unavailable";
            }
            else if (chunks == null || chunks.Count == 0)
            {
                Debug.LogWarning("[OpenAI.RAG] Remote knowledge base returned no chunks; local cache is empty.");
                _index = null;
                Status = "empty";
            }
            else
            {
                var entries = ToEntries(chunks);
                var index = await Task.Run(() => new RagLocalIndex(entries));
                _index = index;
                Status = "ready (remote)";
                Debug.Log($"[OpenAI.RAG] Local cache synced from remote: {index.Count} chunk(s) in {sw.ElapsedMilliseconds} ms.");

                if (persist)
                {
                    var snapshot = index.Entries;
                    await Task.Run(() =>
                    {
                        try { RagLocalIndex.Save(path, fp, snapshot); }
                        catch (Exception ex) { Debug.LogWarning($"[OpenAI.RAG] Could not write local cache file: {ex.Message}"); }
                    });
                }
            }

            await WarmUpAsync(embedder);
        }

        /// <summary>
        /// Searches the local index. Returns (null, reason) when the cache cannot answer, so the caller
        /// can fall back to the live remote path.
        /// </summary>
        public async Task<(List<RagSearchResult> results, string mode)> SearchAsync(string query, int topK, float minScore, OpenAIEmbeddingClient embedder)
        {
            var index = _index;
            if (index == null || index.Count == 0 || string.IsNullOrWhiteSpace(query))
            {
                return (null, "not-ready");
            }

            int k = Mathf.Max(1, topK);

            // 1) Keyword path: no network, no embedding.
            var lexical = index.SearchLexical(query, k);
            if (lexical.Count > 0)
            {
                var best = lexical[0];
                if (best.matched >= Math.Min(2, best.queryTerms) && best.Coverage >= _config.lexicalConfidence)
                {
                    return (ToResults(index, lexical, true), "lexical");
                }
            }

            // 2) Semantic path: one embedding call (skipped when the query embedding is cached), then local cosine.
            string key = NormalizeQuery(query);
            float[] queryVector;
            bool cached = _queryEmbeddings.TryGetValue(key, out queryVector);
            if (!cached)
            {
                if (embedder == null)
                {
                    return (null, "no-embedder");
                }

                var (vector, error) = await embedder.GetEmbeddingAsync(query);
                if (!string.IsNullOrEmpty(error) || vector == null)
                {
                    Debug.LogWarning($"[OpenAI.RAG] Query embedding failed: {error}");
                    return (null, "embed-error");
                }

                Remember(query, vector);
                queryVector = vector;
            }

            var semantic = index.SearchSemantic(queryVector, k, minScore);
            return (ToResults(index, semantic, false), cached ? "semantic, cached embedding" : "semantic");
        }

        private async Task WarmUpAsync(OpenAIEmbeddingClient embedder)
        {
            if (embedder == null) return;

            var queries = new List<string>();
            if (_config.warmupQueries != null)
            {
                foreach (var q in _config.warmupQueries)
                {
                    if (!string.IsNullOrWhiteSpace(q)) queries.Add(q.Trim());
                }
            }
            if (queries.Count == 0) queries.Add("ready");

            var sw = Stopwatch.StartNew();
            var (vectors, error) = await embedder.GetEmbeddingsBatchAsync(queries);
            if (!string.IsNullOrEmpty(error) || vectors == null || vectors.Count != queries.Count)
            {
                Debug.LogWarning($"[OpenAI.RAG] Embedding warm-up skipped: {error}");
                return;
            }

            for (int i = 0; i < queries.Count; i++) Remember(queries[i], vectors[i]);
            Debug.Log($"[OpenAI.RAG] Embedding warm-up: {queries.Count} quer(ies) cached, connection primed in {sw.ElapsedMilliseconds} ms.");
        }

        private void Remember(string query, float[] vector)
        {
            if (_queryEmbeddings.Count >= MaxCachedQueries) _queryEmbeddings.Clear();
            _queryEmbeddings[NormalizeQuery(query)] = vector;
        }

        private static string NormalizeQuery(string query)
        {
            var parts = query.Trim().ToLowerInvariant().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        private static List<RagIndexEntry> ToEntries(List<DocumentChunk> chunks)
        {
            var entries = new List<RagIndexEntry>(chunks.Count);
            foreach (var c in chunks)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.text)) continue;
                entries.Add(new RagIndexEntry
                {
                    id = c.id,
                    documentId = c.documentId,
                    sourceName = c.sourceName,
                    chunkIndex = c.chunkIndex,
                    text = c.text,
                    metadataJson = c.metadataJson,
                    vector = c.embedding
                });
            }
            return entries;
        }

        private static List<RagSearchResult> ToResults(RagLocalIndex index, List<RagIndexHit> hits, bool lexical)
        {
            var results = new List<RagSearchResult>(hits.Count);
            foreach (var h in hits)
            {
                var e = index.GetEntry(h.index);
                var chunk = new DocumentChunk(e.id, e.documentId, e.sourceName, e.chunkIndex, e.text, null, e.metadataJson);
                results.Add(new RagSearchResult(chunk, lexical ? Mathf.Clamp01(h.Coverage) : h.score));
            }
            return results;
        }

        private static string HashHex(string value)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
