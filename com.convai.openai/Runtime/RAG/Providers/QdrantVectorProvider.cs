using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenAI.RAG
{
    /// <summary>
    /// Vector store provider implementation for the Qdrant vector database (cloud or self-hosted).
    /// Interacts directly with Qdrant REST APIs using UnityWebRequest.
    /// Supports automatic collection creation, deterministic UUID point indexing, similarity search,
    /// batch upserting, and collection purging.
    /// </summary>
    public class QdrantVectorProvider : IVectorStoreProvider
    {
        private const int MaxUpsertBatchSize = 100;

        private readonly RagConfig _config;

        /// <summary>
        /// The vector database backend type represented by this provider.
        /// </summary>
        public VectorBackendType BackendType => VectorBackendType.Qdrant;

        /// <summary>
        /// Initializes a new instance of <see cref="QdrantVectorProvider"/> with the specified RAG configuration.
        /// </summary>
        /// <param name="config">RAG configuration asset supplying endpoint URI, collection name, dimensions, and credentials.</param>
        public QdrantVectorProvider(RagConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Upserts a batch of document chunks and their embedding vectors into the Qdrant collection.
        /// Automatically verifies or creates the collection before storing points.
        /// Deterministically maps chunk string IDs to RFC 4122 UUIDs for Qdrant compatibility.
        /// Endpoint: PUT /collections/{collection}/points?wait=true
        /// </summary>
        /// <param name="chunks">List of document chunks containing pre-computed embedding vectors.</param>
        /// <param name="apiKeyOverride">Optional API key override (e.g. editor admin key).</param>
        /// <returns>A tuple indicating success and an error description if failed.</returns>
        public async Task<(bool ok, string error)> UpsertChunksAsync(IReadOnlyList<DocumentChunk> chunks, string apiKeyOverride = null)
        {
            if (_config == null)
            {
                return (false, "RagConfig is null.");
            }

            if (chunks == null || chunks.Count == 0)
            {
                return (true, null);
            }

            string baseUri = GetBaseUri();
            if (string.IsNullOrEmpty(baseUri))
            {
                return (false, "Qdrant Database URI is not configured in RagConfig.");
            }

            // Ensure the collection exists before attempting to upsert points
            var (colOk, colErr) = await EnsureCollectionExistsAsync(apiKeyOverride);
            if (!colOk)
            {
                return (false, $"Failed to verify or create Qdrant collection: {colErr}");
            }

            string collection = GetCollectionName();
            string apiKey = GetApiKey(apiKeyOverride);
            string url = $"{baseUri}/collections/{collection}/points?wait=true";

            // Process chunks in manageable batches to respect HTTP payload size limits
            for (int i = 0; i < chunks.Count; i += MaxUpsertBatchSize)
            {
                int batchCount = Math.Min(MaxUpsertBatchSize, chunks.Count - i);
                var pointsArray = new JArray();

                for (int j = 0; j < batchCount; j++)
                {
                    var chunk = chunks[i + j];
                    if (chunk == null)
                    {
                        continue;
                    }

                    if (chunk.embedding == null || chunk.embedding.Length == 0)
                    {
                        return (false, $"Chunk at index {i + j} (id: '{chunk.id}') has no embedding vector.");
                    }

                    string pointId = GenerateDeterministicGuid(chunk.id).ToString();

                    var payload = new JObject
                    {
                        ["chunk_id"] = chunk.id ?? string.Empty,
                        ["document_id"] = chunk.documentId ?? string.Empty,
                        ["source_name"] = chunk.sourceName ?? string.Empty,
                        ["chunk_index"] = chunk.chunkIndex,
                        ["text"] = chunk.text ?? string.Empty,
                        ["metadata_json"] = chunk.metadataJson ?? string.Empty
                    };

                    var pointObj = new JObject
                    {
                        ["id"] = pointId,
                        ["vector"] = new JArray(chunk.embedding),
                        ["payload"] = payload
                    };

                    pointsArray.Add(pointObj);
                }

                if (pointsArray.Count == 0)
                {
                    continue;
                }

                var requestBody = new JObject
                {
                    ["points"] = pointsArray
                };

                byte[] bodyRaw = Encoding.UTF8.GetBytes(requestBody.ToString(Formatting.None));
                using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT))
                {
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    ApplyHeaders(request, apiKey);
                    request.timeout = 60;

                    try
                    {
                        await SendWebRequestAsync(request);
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Network error during Qdrant upsert: {ex.Message}");
                    }

                    string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                    if (request.result != UnityWebRequest.Result.Success && request.responseCode != 200)
                    {
                        return (false, FormatHttpError(request, request.responseCode, responseText));
                    }
                }
            }

            return (true, null);
        }

        /// <summary>
        /// Queries the Qdrant collection for points most similar to the query embedding.
        /// Endpoint: POST /collections/{collection}/points/search
        /// </summary>
        /// <param name="queryEmbedding">The embedding vector of the search query.</param>
        /// <param name="topK">Maximum number of results to return.</param>
        /// <param name="minScore">Minimum similarity score threshold (0.0 to 1.0).</param>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>Ranked search results and an error description if failed.</returns>
        public async Task<(List<RagSearchResult> results, string error)> QueryAsync(
            float[] queryEmbedding,
            int topK,
            float minScore,
            string apiKeyOverride = null)
        {
            var results = new List<RagSearchResult>();

            if (_config == null)
            {
                return (results, "RagConfig is null.");
            }

            if (queryEmbedding == null || queryEmbedding.Length == 0)
            {
                return (results, "Query embedding vector is empty or null.");
            }

            string baseUri = GetBaseUri();
            if (string.IsNullOrEmpty(baseUri))
            {
                return (results, "Qdrant Database URI is not configured in RagConfig.");
            }

            string collection = GetCollectionName();
            string apiKey = GetApiKey(apiKeyOverride);
            string url = $"{baseUri}/collections/{collection}/points/search";

            int limit = topK > 0 ? topK : 3;

            var requestBody = new JObject
            {
                ["vector"] = new JArray(queryEmbedding),
                ["limit"] = limit,
                ["with_payload"] = true
            };

            if (minScore > 0f)
            {
                requestBody["score_threshold"] = minScore;
            }

            byte[] bodyRaw = Encoding.UTF8.GetBytes(requestBody.ToString(Formatting.None));
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                ApplyHeaders(request, apiKey);
                request.timeout = 30;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (results, $"Network error during Qdrant query: {ex.Message}");
                }

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (request.result != UnityWebRequest.Result.Success && request.responseCode != 200)
                {
                    return (results, FormatHttpError(request, request.responseCode, responseText));
                }

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return (results, null);
                }

                try
                {
                    var root = JObject.Parse(responseText);
                    var matches = root["result"] as JArray;
                    if (matches != null)
                    {
                        foreach (var match in matches)
                        {
                            float score = (float)(match["score"] ?? 0.0);
                            if (minScore > 0f && score < minScore)
                            {
                                continue;
                            }

                            var payload = match["payload"] as JObject;
                            string chunkId = payload?["chunk_id"]?.ToString() ?? match["id"]?.ToString();
                            string docId = payload?["document_id"]?.ToString() ?? string.Empty;
                            string sourceName = payload?["source_name"]?.ToString() ?? string.Empty;
                            int chunkIndex = payload?["chunk_index"]?.Value<int>() ?? 0;
                            string text = payload?["text"]?.ToString() ?? string.Empty;
                            string metadata = payload?["metadata_json"]?.ToString() ?? string.Empty;

                            var chunk = new DocumentChunk(chunkId, docId, sourceName, chunkIndex, text, null, metadata);
                            results.Add(new RagSearchResult(chunk, score));
                        }
                    }

                    results.Sort((a, b) => b.score.CompareTo(a.score));
                    return (results, null);
                }
                catch (Exception ex)
                {
                    return (results, $"Failed to parse Qdrant search response: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Clears all points from the collection.
        /// First attempts to delete points via POST /collections/{collection}/points/delete with an empty filter.
        /// Falls back to dropping and re-creating the collection if the filter endpoint is unsupported.
        /// </summary>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>A tuple indicating success and an error description if failed.</returns>
        public async Task<(bool ok, string error)> ClearAllAsync(string apiKeyOverride = null)
        {
            string baseUri = GetBaseUri();
            if (string.IsNullOrEmpty(baseUri))
            {
                return (false, "Qdrant Database URI is not configured in RagConfig.");
            }

            string collection = GetCollectionName();
            string apiKey = GetApiKey(apiKeyOverride);

            // Attempt 1: POST to /collections/{collection}/points/delete?wait=true with empty filter
            string deletePointsUrl = $"{baseUri}/collections/{collection}/points/delete?wait=true";
            var deletePayload = new JObject
            {
                ["filter"] = new JObject()
            };
            byte[] bodyRaw = Encoding.UTF8.GetBytes(deletePayload.ToString(Formatting.None));

            using (var req = new UnityWebRequest(deletePointsUrl, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                ApplyHeaders(req, apiKey);
                req.timeout = 30;

                try
                {
                    await SendWebRequestAsync(req);
                    if (req.result == UnityWebRequest.Result.Success && (req.responseCode == 200 || req.responseCode == 202))
                    {
                        return (true, null);
                    }
                }
                catch
                {
                    // Fall back to collection drop and recreate
                }
            }

            // Fallback: Drop collection and recreate it
            string dropUrl = $"{baseUri}/collections/{collection}";
            using (var dropReq = UnityWebRequest.Delete(dropUrl))
            {
                dropReq.downloadHandler = new DownloadHandlerBuffer();
                ApplyHeaders(dropReq, apiKey);
                dropReq.timeout = 30;

                try
                {
                    await SendWebRequestAsync(dropReq);
                }
                catch (Exception ex)
                {
                    return (false, $"Network error while dropping collection '{collection}': {ex.Message}");
                }

                if (dropReq.result != UnityWebRequest.Result.Success && dropReq.responseCode != 200 && dropReq.responseCode != 404)
                {
                    string resp = dropReq.downloadHandler != null ? dropReq.downloadHandler.text : string.Empty;
                    return (false, FormatHttpError(dropReq, dropReq.responseCode, resp));
                }
            }

            // Recreate the collection
            var (createOk, createErr) = await CreateCollectionAsync(apiKeyOverride);
            if (!createOk)
            {
                return (false, $"Collection dropped but failed to re-create: {createErr}");
            }

            return (true, null);
        }

        /// <summary>
        /// Tests connectivity and credentials against the Qdrant instance.
        /// If the configured collection does not exist, it is automatically created.
        /// Endpoint: GET /collections/{collection}
        /// </summary>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>A tuple indicating success and a status or diagnostic message.</returns>
        public async Task<(bool ok, string message)> TestConnectionAsync(string apiKeyOverride = null)
        {
            string baseUri = GetBaseUri();
            if (string.IsNullOrEmpty(baseUri))
            {
                return (false, "Qdrant Database URI is not configured in RagConfig.");
            }

            string collection = GetCollectionName();
            string apiKey = GetApiKey(apiKeyOverride);
            string checkUrl = $"{baseUri}/collections/{collection}";

            using (var request = UnityWebRequest.Get(checkUrl))
            {
                ApplyHeaders(request, apiKey);
                request.timeout = 15;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Could not reach Qdrant server at '{baseUri}': {ex.Message}");
                }

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                if (request.responseCode == 401 || request.responseCode == 403)
                {
                    return (false, $"Authentication failed (HTTP {request.responseCode}). Please verify your Qdrant api-key.");
                }

                if (request.responseCode == 404)
                {
                    // Collection does not exist yet; automatically attempt creation
                    var (createOk, createErr) = await CreateCollectionAsync(apiKeyOverride);
                    if (createOk)
                    {
                        return (true, $"Connected to Qdrant at '{baseUri}'. Collection '{collection}' did not exist and was successfully created (dimensions: {GetDimensions()}, distance: Cosine).");
                    }

                    return (false, $"Connected to Qdrant host, but failed to create collection '{collection}': {createErr}");
                }

                if (request.result != UnityWebRequest.Result.Success && request.responseCode != 200)
                {
                    return (false, FormatHttpError(request, request.responseCode, responseText));
                }

                // Parse collection statistics if present
                try
                {
                    var root = JObject.Parse(responseText);
                    var res = root["result"];
                    string status = res?["status"]?.ToString() ?? "ok";
                    long pointsCount = res?["points_count"]?.Value<long>() ?? 0;
                    long vectorsCount = res?["vectors_count"]?.Value<long>() ?? pointsCount;

                    return (true, $"Successfully connected to Qdrant collection '{collection}'. Status: {status}, Total Points: {pointsCount}, Vectors: {vectorsCount}.");
                }
                catch
                {
                    return (true, $"Successfully connected to Qdrant collection '{collection}' (HTTP 200 OK).");
                }
            }
        }

        /// <summary>
        /// Generates a deterministic RFC 4122 Guid from an arbitrary input string using MD5 hashing.
        /// If the input string is already a valid Guid, returns the parsed Guid.
        /// </summary>
        /// <param name="input">The raw chunk identifier string.</param>
        /// <returns>A deterministic <see cref="Guid"/>.</returns>
        public static Guid GenerateDeterministicGuid(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return Guid.NewGuid();
            }

            if (Guid.TryParse(input, out Guid parsed))
            {
                return parsed;
            }

            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                return new Guid(hash);
            }
        }

        /// <summary>
        /// Ensures that the target Qdrant collection exists. If not found (HTTP 404),
        /// dispatches a PUT request to create it with the configured vector dimensionality and Cosine metric.
        /// </summary>
        private async Task<(bool ok, string error)> EnsureCollectionExistsAsync(string apiKeyOverride = null)
        {
            string baseUri = GetBaseUri();
            if (string.IsNullOrEmpty(baseUri))
            {
                return (false, "Qdrant Database URI is not configured in RagConfig.");
            }

            string collection = GetCollectionName();
            string apiKey = GetApiKey(apiKeyOverride);
            string checkUrl = $"{baseUri}/collections/{collection}";

            using (var checkReq = UnityWebRequest.Get(checkUrl))
            {
                ApplyHeaders(checkReq, apiKey);
                checkReq.timeout = 30;

                try
                {
                    await SendWebRequestAsync(checkReq);
                }
                catch (Exception ex)
                {
                    return (false, $"Network error while checking Qdrant collection '{collection}': {ex.Message}");
                }

                if (checkReq.result == UnityWebRequest.Result.Success && checkReq.responseCode == 200)
                {
                    return (true, null);
                }

                if (checkReq.responseCode != 404 && checkReq.responseCode > 0)
                {
                    string responseText = checkReq.downloadHandler != null ? checkReq.downloadHandler.text : string.Empty;
                    return (false, FormatHttpError(checkReq, checkReq.responseCode, responseText));
                }
            }

            // Collection was 404, create it
            return await CreateCollectionAsync(apiKeyOverride);
        }

        /// <summary>
        /// Creates a new Qdrant collection configured for cosine similarity and the configured dimensionality.
        /// Endpoint: PUT /collections/{collection}
        /// </summary>
        private async Task<(bool ok, string error)> CreateCollectionAsync(string apiKeyOverride = null)
        {
            string baseUri = GetBaseUri();
            string collection = GetCollectionName();
            string apiKey = GetApiKey(apiKeyOverride);
            int dimensions = GetDimensions();
            string putUrl = $"{baseUri}/collections/{collection}";

            var createPayload = new JObject
            {
                ["vectors"] = new JObject
                {
                    ["size"] = dimensions,
                    ["distance"] = "Cosine"
                }
            };

            byte[] bodyRaw = Encoding.UTF8.GetBytes(createPayload.ToString(Formatting.None));
            using (var putReq = new UnityWebRequest(putUrl, UnityWebRequest.kHttpVerbPUT))
            {
                putReq.uploadHandler = new UploadHandlerRaw(bodyRaw);
                putReq.downloadHandler = new DownloadHandlerBuffer();
                ApplyHeaders(putReq, apiKey);
                putReq.timeout = 30;

                try
                {
                    await SendWebRequestAsync(putReq);
                }
                catch (Exception ex)
                {
                    return (false, $"Network error while creating Qdrant collection '{collection}': {ex.Message}");
                }

                string responseText = putReq.downloadHandler != null ? putReq.downloadHandler.text : string.Empty;
                if (putReq.result != UnityWebRequest.Result.Success && putReq.responseCode != 200 && putReq.responseCode != 201)
                {
                    return (false, FormatHttpError(putReq, putReq.responseCode, responseText));
                }

                return (true, null);
            }
        }

        private string GetBaseUri()
        {
            return (_config?.databaseUri ?? string.Empty).Trim().TrimEnd('/');
        }

        private string GetCollectionName()
        {
            return string.IsNullOrWhiteSpace(_config?.collectionOrTableName)
                ? "documents"
                : _config.collectionOrTableName.Trim();
        }

        private int GetDimensions()
        {
            return _config != null && _config.dimensions > 0 ? _config.dimensions : 1536;
        }

        private string GetApiKey(string apiKeyOverride)
        {
            if (!string.IsNullOrWhiteSpace(apiKeyOverride))
            {
                return apiKeyOverride.Trim();
            }

            return _config != null ? _config.GetEffectiveBackendApiKey().Trim() : string.Empty;
        }

        private static void ApplyHeaders(UnityWebRequest request, string apiKey)
        {
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.SetRequestHeader("api-key", apiKey);
            }
        }

        private static string FormatHttpError(UnityWebRequest request, long responseCode, string responseBody)
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                try
                {
                    var parsed = JObject.Parse(responseBody);
                    var statusObj = parsed["status"];
                    if (statusObj is JObject statusDetails && statusDetails["error"] != null)
                    {
                        return $"Qdrant Error ({responseCode}): {statusDetails["error"]}";
                    }

                    if (parsed["error"] != null)
                    {
                        return $"Qdrant Error ({responseCode}): {parsed["error"]}";
                    }

                    if (statusObj != null && statusObj.Type == JTokenType.String)
                    {
                        return $"Qdrant Error ({responseCode}): {statusObj}";
                    }
                }
                catch
                {
                    // Non-JSON fallback
                }
            }

            if (responseCode == 401 || responseCode == 403)
            {
                return $"Qdrant Authentication Failed ({responseCode}). Verify your api-key.";
            }

            string details = !string.IsNullOrWhiteSpace(responseBody) ? responseBody : request.error;
            return $"Qdrant HTTP Error ({responseCode}): {details}";
        }

        /// <summary>
        /// Wraps UnityWebRequestAsyncOperation using TaskCompletionSource so it completes cleanly
        /// both in Editor outside Play Mode and in runtime without blocking the calling thread.
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
