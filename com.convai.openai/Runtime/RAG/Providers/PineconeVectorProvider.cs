using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenAI.RAG
{
    /// <summary>
    /// Vector store provider implementation for Pinecone Serverless vector database.
    /// Interacts directly with Pinecone Data Plane REST APIs using UnityWebRequest.
    /// Supports Upsert, Similarity Query with metadata, Clear/Delete, and Connection Testing.
    /// </summary>
    public class PineconeVectorProvider : IVectorStoreProvider
    {
        private const string PineconeApiVersion = "2024-07";

        private readonly RagConfig _config;

        /// <summary>
        /// The vector database backend type represented by this provider.
        /// </summary>
        public VectorBackendType BackendType => VectorBackendType.Pinecone;

        /// <summary>
        /// Initializes a new instance of the PineconeVectorProvider with the specified RAG configuration.
        /// </summary>
        /// <param name="config">RAG configuration asset supplying endpoint URI, namespace, and credentials.</param>
        public PineconeVectorProvider(RagConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Upserts a batch of document chunks and their embedding vectors into the Pinecone index.
        /// Endpoint: POST {databaseUri}/vectors/upsert
        /// </summary>
        /// <param name="chunks">List of document chunks with pre-computed embedding vectors.</param>
        /// <param name="apiKeyOverride">Optional API key override (e.g. editor admin key).</param>
        /// <returns>Tuple indicating success and error description if failed.</returns>
        public async Task<(bool ok, string error)> UpsertChunksAsync(IReadOnlyList<DocumentChunk> chunks, string apiKeyOverride = null)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return (true, null);
            }

            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, "Pinecone API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseUri = ResolveBaseUri(out string uriError);
            if (!string.IsNullOrEmpty(uriError))
            {
                return (false, uriError);
            }

            string ns = _config != null ? _config.collectionOrTableName : string.Empty;
            var vectorsArray = new JArray();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (chunk == null) continue;

                if (chunk.embedding == null || chunk.embedding.Length == 0)
                {
                    return (false, $"Chunk '{chunk.id ?? $"index_{i}"}' has no embedding vector. Generate embeddings before upserting.");
                }

                var valuesArray = new JArray();
                for (int v = 0; v < chunk.embedding.Length; v++)
                {
                    valuesArray.Add(chunk.embedding[v]);
                }

                var metadata = new JObject
                {
                    ["document_id"] = chunk.documentId ?? string.Empty,
                    ["source_name"] = chunk.sourceName ?? string.Empty,
                    ["chunk_index"] = chunk.chunkIndex,
                    ["text"] = chunk.text ?? string.Empty
                };

                var vectorObj = new JObject
                {
                    ["id"] = !string.IsNullOrEmpty(chunk.id) ? chunk.id : Guid.NewGuid().ToString("N"),
                    ["values"] = valuesArray,
                    ["metadata"] = metadata
                };

                vectorsArray.Add(vectorObj);
            }

            if (vectorsArray.Count == 0)
            {
                return (true, null);
            }

            var requestBody = new JObject
            {
                ["namespace"] = ns ?? string.Empty,
                ["vectors"] = vectorsArray
            };

            string url = $"{baseUri}/vectors/upsert";
            string jsonString = requestBody.ToString(Formatting.None);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonString);

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Api-Key", apiKey);
                request.SetRequestHeader("X-Pinecone-API-Version", PineconeApiVersion);
                request.timeout = 60;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Pinecone upsert network request failed: {ex.Message}");
                }

                long responseCode = request.responseCode;
                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (!string.IsNullOrEmpty(responseText) && responseText.IndexOf("document schema", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return await UpsertDocumentsAsync(chunks, baseUri, ns, apiKey);
                    }
                    return (false, FormatHttpError(request, responseCode, responseText));
                }

                return (true, null);
            }
        }

        /// <summary>
        /// Queries the Pinecone vector index for chunks nearest to the query embedding.
        /// Endpoint: POST {databaseUri}/query
        /// </summary>
        /// <param name="queryEmbedding">The query embedding vector.</param>
        /// <param name="topK">Maximum number of results to retrieve.</param>
        /// <param name="minScore">Minimum similarity score threshold (0.0 to 1.0).</param>
        /// <param name="apiKeyOverride">Optional API key override.</param>
        /// <returns>Ranked search results and error description if failed.</returns>
        public async Task<(List<RagSearchResult> results, string error)> QueryAsync(float[] queryEmbedding, int topK, float minScore, string apiKeyOverride = null)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
            {
                return (null, "Query embedding cannot be null or empty.");
            }

            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (null, "Pinecone API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseUri = ResolveBaseUri(out string uriError);
            if (!string.IsNullOrEmpty(uriError))
            {
                return (null, uriError);
            }

            int effectiveTopK = topK > 0 ? topK : (_config != null ? _config.topK : 3);
            if (effectiveTopK <= 0) effectiveTopK = 3;

            string ns = _config != null ? _config.collectionOrTableName : string.Empty;

            var vectorArray = new JArray();
            for (int i = 0; i < queryEmbedding.Length; i++)
            {
                vectorArray.Add(queryEmbedding[i]);
            }

            var requestBody = new JObject
            {
                ["namespace"] = ns ?? string.Empty,
                ["vector"] = vectorArray,
                ["topK"] = effectiveTopK,
                ["includeMetadata"] = true,
                ["includeValues"] = false
            };

            string url = $"{baseUri}/query";
            string jsonString = requestBody.ToString(Formatting.None);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonString);

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Api-Key", apiKey);
                request.SetRequestHeader("X-Pinecone-API-Version", PineconeApiVersion);
                request.timeout = 30;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (null, $"Pinecone query network request failed: {ex.Message}");
                }

                long responseCode = request.responseCode;
                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (!string.IsNullOrEmpty(responseText) && responseText.IndexOf("document schema", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return await SearchDocumentsAsync(queryEmbedding, effectiveTopK, minScore, baseUri, ns, apiKey);
                    }
                    return (null, FormatHttpError(request, responseCode, responseText));
                }

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return (new List<RagSearchResult>(), null);
                }

                return ParseQueryResponse(responseText, minScore);
            }
        }

        /// <summary>
        /// Deletes all vectors within the configured namespace in the Pinecone index.
        /// Endpoint: POST {databaseUri}/vectors/delete
        /// </summary>
        /// <param name="apiKeyOverride">Optional API key override (e.g. editor admin key).</param>
        /// <returns>Tuple indicating success and error description if failed.</returns>
        public async Task<(bool ok, string error)> ClearAllAsync(string apiKeyOverride = null)
        {
            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, "Pinecone API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseUri = ResolveBaseUri(out string uriError);
            if (!string.IsNullOrEmpty(uriError))
            {
                return (false, uriError);
            }

            string ns = _config != null ? _config.collectionOrTableName : string.Empty;

            var requestBody = new JObject
            {
                ["deleteAll"] = true,
                ["namespace"] = ns ?? string.Empty
            };

            string url = $"{baseUri}/vectors/delete";
            string jsonString = requestBody.ToString(Formatting.None);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonString);

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Api-Key", apiKey);
                request.SetRequestHeader("X-Pinecone-API-Version", PineconeApiVersion);
                request.timeout = 30;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Pinecone clear network request failed: {ex.Message}");
                }

                long responseCode = request.responseCode;
                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (!string.IsNullOrEmpty(responseText) && responseText.IndexOf("document schema", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return await UpsertDocumentsAsync(chunks, baseUri, ns, apiKey);
                    }
                    return (false, FormatHttpError(request, responseCode, responseText));
                }

                return (true, null);
            }
        }

        /// <summary>
        /// Tests connectivity and credentials against the Pinecone index by requesting index statistics.
        /// Endpoint: POST {databaseUri}/describe_index_stats
        /// </summary>
        /// <param name="apiKeyOverride">Optional API key override.</param>
        /// <returns>Tuple indicating success and status/diagnostic message.</returns>
        public async Task<(bool ok, string message)> TestConnectionAsync(string apiKeyOverride = null)
        {
            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, "Pinecone API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseUri = ResolveBaseUri(out string uriError);
            if (!string.IsNullOrEmpty(uriError))
            {
                return (false, uriError);
            }

            string url = $"{baseUri}/describe_index_stats";
            byte[] bodyRaw = Encoding.UTF8.GetBytes("{}");

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Api-Key", apiKey);
                request.SetRequestHeader("X-Pinecone-API-Version", PineconeApiVersion);
                request.timeout = 20;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Pinecone connection test failed to connect: {ex.Message}");
                }

                long responseCode = request.responseCode;
                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return (false, FormatHttpError(request, responseCode, responseText));
                }

                try
                {
                    var root = JObject.Parse(responseText);
                    int totalVectors = (int?)root["totalVectorCount"] ?? 0;
                    int dimension = (int?)root["dimension"] ?? 0;
                    string ns = _config != null ? _config.collectionOrTableName : string.Empty;

                    string nsInfo = string.Empty;
                    if (!string.IsNullOrEmpty(ns) && root["namespaces"] is JObject nsDict && nsDict[ns] is JObject targetNs)
                    {
                        int nsCount = (int?)targetNs["vectorCount"] ?? 0;
                        nsInfo = $" (Namespace '{ns}' contains {nsCount} vectors)";
                    }

                    return (true, $"Pinecone connection successful! Dimension: {dimension}, Total vectors in index: {totalVectors}{nsInfo}.");
                }
                catch (Exception ex)
                {
                    return (true, $"Pinecone connection successful (HTTP {responseCode}), but could not parse stats JSON: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Resolves the effective API key for Pinecone requests (override key takes precedence over RagConfig).
        /// </summary>
        private string ResolveApiKey(string apiKeyOverride)
        {
            if (!string.IsNullOrWhiteSpace(apiKeyOverride))
            {
                return apiKeyOverride.Trim();
            }

            return _config != null ? _config.GetEffectiveBackendApiKey()?.Trim() ?? string.Empty : string.Empty;
        }

        /// <summary>
        /// Resolves and normalizes the Pinecone database base URI from RagConfig.
        /// </summary>
        private string ResolveBaseUri(out string error)
        {
            error = null;
            string uri = _config != null ? _config.databaseUri?.Trim() : null;

            if (string.IsNullOrWhiteSpace(uri))
            {
                error = "Pinecone database URI is missing. Set it in RagConfig (e.g. 'https://my-index-xxxx.svc.aped-4627-b74a.pinecone.io').";
                return null;
            }

            if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                uri = "https://" + uri;
            }

            return uri.TrimEnd('/');
        }

        private async Task<(bool ok, string error)> UpsertDocumentsAsync(IReadOnlyList<DocumentChunk> chunks, string baseUri, string ns, string apiKey)
        {
            string url = string.IsNullOrEmpty(ns)
                ? $"{baseUri}/documents/upsert"
                : $"{baseUri}/namespaces/{Uri.EscapeDataString(ns)}/documents/upsert";

            var docsArray = new JArray();
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (chunk == null) continue;

                var embArray = new JArray();
                if (chunk.embedding != null)
                {
                    for (int v = 0; v < chunk.embedding.Length; v++)
                    {
                        embArray.Add(chunk.embedding[v]);
                    }
                }

                var doc = new JObject
                {
                    ["_id"] = !string.IsNullOrEmpty(chunk.id) ? chunk.id : Guid.NewGuid().ToString("N"),
                    ["text"] = chunk.text ?? string.Empty,
                    ["document_id"] = chunk.documentId ?? string.Empty,
                    ["source_name"] = chunk.sourceName ?? string.Empty,
                    ["chunk_index"] = chunk.chunkIndex,
                    ["embedding"] = embArray
                };
                docsArray.Add(doc);
            }

            var bodyObj = new JObject { ["documents"] = docsArray };
            byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyObj.ToString(Formatting.None));

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Api-Key", apiKey);
                request.SetRequestHeader("X-Pinecone-API-Version", "2026-07");
                request.timeout = 60;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Pinecone document upsert failed: {ex.Message}");
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return (false, FormatHttpError(request, request.responseCode, request.downloadHandler?.text));
                }
                return (true, null);
            }
        }

        private async Task<(List<RagSearchResult> results, string error)> SearchDocumentsAsync(float[] queryEmbedding, int topK, float minScore, string baseUri, string ns, string apiKey)
        {
            string url = string.IsNullOrEmpty(ns)
                ? $"{baseUri}/documents/search"
                : $"{baseUri}/namespaces/{Uri.EscapeDataString(ns)}/documents/search";

            var valuesArray = new JArray();
            for (int v = 0; v < queryEmbedding.Length; v++)
            {
                valuesArray.Add(queryEmbedding[v]);
            }

            var searchBody = new JObject
            {
                ["top_k"] = topK,
                ["score_by"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "dense_vector",
                        ["fields"] = new JArray("embedding"),
                        ["values"] = valuesArray
                    }
                },
                ["fields"] = new JArray("text", "source_name", "document_id", "chunk_index")
            };

            byte[] bodyRaw = Encoding.UTF8.GetBytes(searchBody.ToString(Formatting.None));

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Api-Key", apiKey);
                request.SetRequestHeader("X-Pinecone-API-Version", "2026-07");
                request.timeout = 30;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (null, $"Pinecone document search failed: {ex.Message}");
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return (null, FormatHttpError(request, request.responseCode, request.downloadHandler?.text));
                }

                return ParseQueryResponse(request.downloadHandler?.text, minScore);
            }
        }

        /// <summary>
        /// Parses the JSON response from Pinecone query endpoint and returns matching chunks sorted by score.
        /// </summary>
        private static (List<RagSearchResult> results, string error) ParseQueryResponse(string json, float minScore)
        {
            try
            {
                var root = JObject.Parse(json);
                if (root["error"] != null)
                {
                    string errorMsg = root["error"]?["message"]?.ToString() ?? root["error"]?.ToString();
                    return (null, $"Pinecone Query Error: {errorMsg}");
                }

                var matches = root["matches"] as JArray;
                var results = new List<RagSearchResult>();

                if (matches == null || matches.Count == 0)
                {
                    return (results, null);
                }

                foreach (var item in matches)
                {
                    float score = (float?)item["score"] ?? (float?)item["_score"] ?? 0f;
                    if (score < minScore)
                    {
                        continue;
                    }

                    string id = item["id"]?.ToString() ?? item["_id"]?.ToString() ?? string.Empty;
                    var metadata = (item["metadata"] as JObject) ?? (item["fields"] as JObject);

                    string docId = metadata?["document_id"]?.ToString() ?? string.Empty;
                    string source = metadata?["source_name"]?.ToString() ?? string.Empty;
                    int chunkIndex = (int?)metadata?["chunk_index"] ?? 0;
                    string text = metadata?["text"]?.ToString() ?? item["text"]?.ToString() ?? string.Empty;

                    var chunk = new DocumentChunk(
                        id: id,
                        documentId: docId,
                        sourceName: source,
                        chunkIndex: chunkIndex,
                        text: text,
                        embedding: null,
                        metadataJson: metadata != null ? metadata.ToString(Formatting.None) : null
                    );

                    results.Add(new RagSearchResult(chunk, score));
                }

                results.Sort((a, b) => b.score.CompareTo(a.score));
                return (results, null);
            }
            catch (Exception ex)
            {
                return (null, $"Failed to parse Pinecone query response: {ex.Message}");
            }
        }

        /// <summary>
        /// Formats error information from a failed UnityWebRequest, extracting Pinecone error details if available.
        /// </summary>
        private static string FormatHttpError(UnityWebRequest request, long responseCode, string responseBody)
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                try
                {
                    var json = JObject.Parse(responseBody);
                    string msg = json["message"]?.ToString();
                    if (string.IsNullOrEmpty(msg) && json["error"] != null)
                    {
                        msg = json["error"]?["message"]?.ToString() ?? json["error"]?.ToString();
                    }

                    if (!string.IsNullOrEmpty(msg))
                    {
                        return $"Pinecone Error ({responseCode}): {msg}";
                    }
                }
                catch
                {
                    // Fall back if response is not valid JSON
                }
            }

            string details = !string.IsNullOrWhiteSpace(responseBody) ? responseBody : request.error;
            return $"Pinecone HTTP Error ({responseCode}): {details}";
        }

        /// <summary>
        /// Wraps UnityWebRequestAsyncOperation using TaskCompletionSource so it executes cleanly
        /// both in Editor outside Play Mode and at runtime without blocking the calling thread.
        /// </summary>
        private static Task<bool> SendWebRequestAsync(UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<bool>();
            var asyncOp = request.SendWebRequest();

            if (asyncOp.isDone)
            {
                tcs.TrySetResult(true);
                return tcs.Task;
            }

#if UNITY_EDITOR
            UnityEditor.EditorApplication.CallbackFunction editorUpdate = null;
            editorUpdate = () =>
            {
                if (asyncOp.isDone)
                {
                    UnityEditor.EditorApplication.update -= editorUpdate;
                    tcs.TrySetResult(true);
                }
            };
            UnityEditor.EditorApplication.update += editorUpdate;
#endif

            asyncOp.completed += _ =>
            {
#if UNITY_EDITOR
                if (editorUpdate != null)
                {
                    UnityEditor.EditorApplication.update -= editorUpdate;
                }
#endif
                tcs.TrySetResult(true);
            };

            return tcs.Task;
        }
    }
}
