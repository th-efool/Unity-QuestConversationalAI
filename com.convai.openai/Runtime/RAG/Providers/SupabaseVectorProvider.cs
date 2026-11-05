using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenAI.RAG
{
    /// <summary>
    /// Vector store provider implementation for PostgreSQL with the pgvector extension,
    /// hosted on Supabase and accessed via PostgREST and Remote Procedure Calls (RPC).
    /// </summary>
    public class SupabaseVectorProvider : IVectorStoreProvider, IBulkExportProvider
    {
        private readonly RagConfig _config;

        /// <summary>
        /// Gets the vector backend type for Supabase pgvector.
        /// </summary>
        public VectorBackendType BackendType => VectorBackendType.SupabasePgVector;

        /// <summary>
        /// Initializes a new instance of the SupabaseVectorProvider with the specified RAG configuration.
        /// </summary>
        /// <param name="config">Configuration asset supplying database URI, table name, RPC function name, and API keys.</param>
        public SupabaseVectorProvider(RagConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Upserts a batch of document chunks and their embedding vectors into the configured Supabase table.
        /// Merges duplicates on primary key conflict using the PostgREST 'Prefer: resolution=merge-duplicates' header.
        /// </summary>
        /// <param name="chunks">List of document chunks to store.</param>
        /// <param name="apiKeyOverride">Optional admin/service-role API key override.</param>
        /// <returns>A tuple indicating success and error description if failed.</returns>
        public async Task<(bool ok, string error)> UpsertChunksAsync(IReadOnlyList<DocumentChunk> chunks, string apiKeyOverride = null)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return (true, null);
            }

            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, "Supabase API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseRestUrl = GetBaseRestUrl();
            if (string.IsNullOrWhiteSpace(baseRestUrl))
            {
                return (false, "Supabase database URI is missing. Set databaseUri in RagConfig.");
            }

            string tableName = GetTableName();
            string endpoint = $"{baseRestUrl}/{tableName}";

            var rootArray = new JArray();
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (chunk == null) continue;

                var chunkObj = new JObject
                {
                    ["id"] = !string.IsNullOrEmpty(chunk.id) ? chunk.id : Guid.NewGuid().ToString(),
                    ["document_id"] = chunk.documentId ?? string.Empty,
                    ["source_name"] = chunk.sourceName ?? string.Empty,
                    ["chunk_index"] = chunk.chunkIndex,
                    ["content"] = chunk.text ?? string.Empty
                };

                // Metadata JSON handling
                if (!string.IsNullOrWhiteSpace(chunk.metadataJson))
                {
                    try
                    {
                        chunkObj["metadata"] = JToken.Parse(chunk.metadataJson);
                    }
                    catch
                    {
                        chunkObj["metadata"] = new JObject { ["raw"] = chunk.metadataJson };
                    }
                }
                else
                {
                    chunkObj["metadata"] = new JObject();
                }

                // Embedding vector handling
                var embArray = new JArray();
                if (chunk.embedding != null)
                {
                    for (int j = 0; j < chunk.embedding.Length; j++)
                    {
                        embArray.Add(chunk.embedding[j]);
                    }
                }
                chunkObj["embedding"] = embArray;

                rootArray.Add(chunkObj);
            }

            string jsonString = rootArray.ToString(Newtonsoft.Json.Formatting.None);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonString);

            using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("apikey", apiKey);
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.SetRequestHeader("Prefer", "resolution=merge-duplicates");
                request.timeout = 60;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Network request failed: {ex.Message}");
                }

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                long responseCode = request.responseCode;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return (false, FormatHttpError(request, responseCode, responseText));
                }

                return (true, null);
            }
        }

        /// <summary>
        /// Queries the Supabase vector store for nearest document chunks matching the query embedding
        /// by calling the PostgreSQL stored function (RPC) specified in the configuration.
        /// </summary>
        /// <param name="queryEmbedding">The embedding vector of the search query.</param>
        /// <param name="topK">Maximum number of results to return.</param>
        /// <param name="minScore">Minimum cosine similarity threshold (0.0 to 1.0).</param>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>Ranked search results and error description if failed.</returns>
        public async Task<(List<RagSearchResult> results, string error)> QueryAsync(float[] queryEmbedding, int topK, float minScore, string apiKeyOverride = null)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
            {
                return (new List<RagSearchResult>(), "Query embedding is null or empty.");
            }

            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (null, "Supabase API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseRestUrl = GetBaseRestUrl();
            if (string.IsNullOrWhiteSpace(baseRestUrl))
            {
                return (null, "Supabase database URI is missing. Set databaseUri in RagConfig.");
            }

            string rpcName = GetRpcFunctionName();
            string endpoint = $"{baseRestUrl}/rpc/{rpcName}";

            var queryArray = new JArray();
            for (int i = 0; i < queryEmbedding.Length; i++)
            {
                queryArray.Add(queryEmbedding[i]);
            }

            var requestBody = new JObject
            {
                ["query_embedding"] = queryArray,
                ["match_threshold"] = minScore,
                ["match_count"] = topK
            };

            string jsonString = requestBody.ToString(Newtonsoft.Json.Formatting.None);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonString);

            using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("apikey", apiKey);
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = 30;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (null, $"Network request failed: {ex.Message}");
                }

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                long responseCode = request.responseCode;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return (null, FormatHttpError(request, responseCode, responseText));
                }

                return ParseQueryResults(responseText, minScore, topK);
            }
        }

        /// <summary>
        /// Deletes all indexed chunks from the configured Supabase table.
        /// </summary>
        /// <param name="apiKeyOverride">Optional admin/service-role API key override.</param>
        /// <returns>A tuple indicating success and error description if failed.</returns>
        public async Task<(bool ok, string error)> ClearAllAsync(string apiKeyOverride = null)
        {
            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, "Supabase API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseRestUrl = GetBaseRestUrl();
            if (string.IsNullOrWhiteSpace(baseRestUrl))
            {
                return (false, "Supabase database URI is missing. Set databaseUri in RagConfig.");
            }

            string tableName = GetTableName();
            // PostgREST requires an explicit query filter for DELETE operations to avoid accidental table wipes.
            // Using id=not.is.null targets all rows where primary key id is non-null.
            string endpoint = $"{baseRestUrl}/{tableName}?id=not.is.null";

            using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbDELETE))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("apikey", apiKey);
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = 30;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Network request failed: {ex.Message}");
                }

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                long responseCode = request.responseCode;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return (false, FormatHttpError(request, responseCode, responseText));
                }

                return (true, null);
            }
        }

        /// <summary>
        /// Tests connectivity and credentials against the Supabase table endpoint.
        /// </summary>
        /// <param name="apiKeyOverride">Optional API key override.</param>
        /// <returns>A tuple indicating success and a descriptive diagnostic message.</returns>
        public async Task<(bool ok, string message)> TestConnectionAsync(string apiKeyOverride = null)
        {
            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, "Supabase API key is missing. Please configure runtimeApiKey in RagConfig or enter an Admin/Write Key in the window.");
            }

            string baseRestUrl = GetBaseRestUrl();
            if (string.IsNullOrWhiteSpace(baseRestUrl))
            {
                return (false, "Supabase database URI is missing. Set databaseUri in RagConfig (e.g. 'https://xyzcompany.supabase.co').");
            }

            string tableName = GetTableName();
            string endpoint = $"{baseRestUrl}/{tableName}?select=id&limit=1";

            using (var request = UnityWebRequest.Get(endpoint))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("apikey", apiKey);
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = 15;

                try
                {
                    await SendWebRequestAsync(request);
                }
                catch (Exception ex)
                {
                    return (false, $"Connection test failed: {ex.Message}");
                }

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                long responseCode = request.responseCode;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return (false, FormatHttpError(request, responseCode, responseText));
                }

                return (true, $"Successfully connected to Supabase table '{tableName}' (HTTP {responseCode}).");
            }
        }

        /// <summary>
        /// Generates a ready-to-execute PostgreSQL setup script for Supabase SQL Editor.
        /// Enables pgvector extension, creates the documents table with HNSW index,
        /// sets up Row Level Security (RLS) policies, and creates the match_documents RPC function.
        /// </summary>
        /// <param name="tableName">Name of the documents table to create.</param>
        /// <param name="rpcName">Name of the RPC function for vector similarity matching.</param>
        /// <param name="dimensions">Vector dimensionality (1536 for text-embedding-3-small).</param>
        /// <returns>Complete SQL script string.</returns>
        public static string GetSqlSetupScript(string tableName = "documents", string rpcName = "match_documents", int dimensions = 1536)
        {
            string safeTable = !string.IsNullOrWhiteSpace(tableName) ? tableName.Trim() : "documents";
            string safeRpc = !string.IsNullOrWhiteSpace(rpcName) ? rpcName.Trim() : "match_documents";
            int safeDim = dimensions > 0 ? dimensions : 1536;

            var sb = new StringBuilder();
            sb.AppendLine("-- ============================================================================");
            sb.AppendLine($"-- Supabase pgvector Setup Script for OpenAI RAG ({safeTable} / {safeRpc})");
            sb.AppendLine("-- Run this entire script in Supabase Dashboard > SQL Editor > New query");
            sb.AppendLine("-- ============================================================================");
            sb.AppendLine();
            sb.AppendLine("-- 1. Enable the pgvector extension to work with embedding vectors");
            sb.AppendLine("CREATE EXTENSION IF NOT EXISTS vector;");
            sb.AppendLine();
            sb.AppendLine($"-- 2. Create the table for storing RAG chunks");
            sb.AppendLine($"CREATE TABLE IF NOT EXISTS {safeTable} (");
            sb.AppendLine("    id TEXT PRIMARY KEY,");
            sb.AppendLine("    document_id TEXT NOT NULL,");
            sb.AppendLine("    source_name TEXT NOT NULL,");
            sb.AppendLine("    chunk_index INTEGER NOT NULL,");
            sb.AppendLine("    content TEXT NOT NULL,");
            sb.AppendLine("    metadata JSONB DEFAULT '{}'::jsonb,");
            sb.AppendLine($"    embedding VECTOR({safeDim}) NOT NULL,");
            sb.AppendLine("    created_at TIMESTAMP WITH TIME ZONE DEFAULT TIMEZONE('utc'::text, NOW()) NOT NULL");
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine($"-- 3. Create HNSW index for high-performance cosine distance search (<=>)");
            sb.AppendLine($"CREATE INDEX IF NOT EXISTS {safeTable}_embedding_hnsw_idx");
            sb.AppendLine($"ON {safeTable}");
            sb.AppendLine("USING hnsw (embedding vector_cosine_ops);");
            sb.AppendLine();
            sb.AppendLine($"-- 4. Enable Row Level Security (RLS)");
            sb.AppendLine($"ALTER TABLE {safeTable} ENABLE ROW LEVEL SECURITY;");
            sb.AppendLine();
            sb.AppendLine($"-- Allow public read access (anon / authenticated) for runtime queries");
            sb.AppendLine("DO $$");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    IF NOT EXISTS (");
            sb.AppendLine($"        SELECT 1 FROM pg_policies WHERE tablename = '{safeTable}' AND policyname = 'Allow public read access'");
            sb.AppendLine("    ) THEN");
            sb.AppendLine($"        CREATE POLICY \"Allow public read access\" ON {safeTable}");
            sb.AppendLine("        FOR SELECT TO anon, authenticated USING (true);");
            sb.AppendLine("    END IF;");
            sb.AppendLine("END $$;");
            sb.AppendLine();
            sb.AppendLine($"-- Allow full access for service_role (Admin / Editor ingestion)");
            sb.AppendLine("DO $$");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    IF NOT EXISTS (");
            sb.AppendLine($"        SELECT 1 FROM pg_policies WHERE tablename = '{safeTable}' AND policyname = 'Allow service_role full access'");
            sb.AppendLine("    ) THEN");
            sb.AppendLine($"        CREATE POLICY \"Allow service_role full access\" ON {safeTable}");
            sb.AppendLine("        FOR ALL TO service_role USING (true);");
            sb.AppendLine("    END IF;");
            sb.AppendLine("END $$;");
            sb.AppendLine();
            sb.AppendLine($"-- 5. Stored function (RPC) for similarity search");
            sb.AppendLine($"CREATE OR REPLACE FUNCTION {safeRpc} (");
            sb.AppendLine($"    query_embedding VECTOR({safeDim}),");
            sb.AppendLine("    match_threshold FLOAT DEFAULT 0.35,");
            sb.AppendLine("    match_count INT DEFAULT 5");
            sb.AppendLine(")");
            sb.AppendLine("RETURNS TABLE (");
            sb.AppendLine("    id TEXT,");
            sb.AppendLine("    document_id TEXT,");
            sb.AppendLine("    source_name TEXT,");
            sb.AppendLine("    chunk_index INT,");
            sb.AppendLine("    content TEXT,");
            sb.AppendLine("    metadata JSONB,");
            sb.AppendLine("    similarity FLOAT");
            sb.AppendLine(")");
            sb.AppendLine("LANGUAGE plpgsql");
            sb.AppendLine("STABLE");
            sb.AppendLine("AS $$");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    RETURN QUERY");
            sb.AppendLine("    SELECT");
            sb.AppendLine($"        {safeTable}.id,");
            sb.AppendLine($"        {safeTable}.document_id,");
            sb.AppendLine($"        {safeTable}.source_name,");
            sb.AppendLine($"        {safeTable}.chunk_index,");
            sb.AppendLine($"        {safeTable}.content,");
            sb.AppendLine($"        {safeTable}.metadata,");
            sb.AppendLine($"        1 - ({safeTable}.embedding <=> query_embedding) AS similarity");
            sb.AppendLine($"    FROM {safeTable}");
            sb.AppendLine($"    WHERE 1 - ({safeTable}.embedding <=> query_embedding) >= match_threshold");
            sb.AppendLine("    ORDER BY similarity DESC");
            sb.AppendLine("    LIMIT match_count;");
            sb.AppendLine("END;");
            sb.AppendLine("$$;");

            return sb.ToString();
        }

        /// <summary>
        /// Downloads every row (content, metadata, and pgvector embedding) from the configured Supabase table
        /// via PostgREST pagination so the knowledge base can be mirrored into <see cref="RagLocalIndex"/>.
        /// Endpoint: GET {baseRestUrl}/{tableName}?select=id,document_id,source_name,chunk_index,content,metadata,embedding&amp;order=id.asc&amp;limit=500&amp;offset={offset}
        /// </summary>
        public async Task<(List<DocumentChunk> chunks, string error)> ExportAllAsync(string apiKeyOverride = null)
        {
            var all = new List<DocumentChunk>();
            string apiKey = ResolveApiKey(apiKeyOverride);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (all, "Supabase API key is missing. Set runtimeApiKey in RagConfig or provide apiKeyOverride.");
            }

            string baseRestUrl = GetBaseRestUrl();
            if (string.IsNullOrWhiteSpace(baseRestUrl))
            {
                return (all, "Supabase database URI is missing. Set databaseUri in RagConfig.");
            }

            string tableName = GetTableName();
            const int pageSize = 500;

            for (int page = 0; page < 100; page++)
            {
                int offset = page * pageSize;
                string endpoint = $"{baseRestUrl}/{tableName}?select=id,document_id,source_name,chunk_index,content,metadata,embedding&order=id.asc&limit={pageSize}&offset={offset}";

                string responseText;
                using (var request = UnityWebRequest.Get(endpoint))
                {
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("apikey", apiKey);
                    request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                    request.timeout = 60;

                    try
                    {
                        await SendWebRequestAsync(request);
                    }
                    catch (Exception ex)
                    {
                        return (all, $"Network error during Supabase export: {ex.Message}");
                    }

                    responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        return (all, FormatHttpError(request, request.responseCode, responseText));
                    }
                }

                List<DocumentChunk> pageChunks;
                try
                {
                    pageChunks = await Task.Run(() => ParseExportRows(responseText));
                }
                catch (Exception ex)
                {
                    return (all, $"Failed to parse Supabase export response: {ex.Message}");
                }

                if (pageChunks == null || pageChunks.Count == 0)
                {
                    break;
                }

                all.AddRange(pageChunks);
                if (pageChunks.Count < pageSize)
                {
                    break;
                }
            }

            return (all, null);
        }

        private static List<DocumentChunk> ParseExportRows(string json)
        {
            var list = new List<DocumentChunk>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return list;
            }

            var rows = JToken.Parse(json) as JArray;
            if (rows == null)
            {
                return list;
            }

            foreach (var item in rows)
            {
                if (item == null) continue;

                string content = item["content"]?.ToString() ?? item["text"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(content)) continue;

                string id = item["id"]?.ToString() ?? string.Empty;
                string docId = item["document_id"]?.ToString() ?? item["documentId"]?.ToString() ?? string.Empty;
                string sourceName = item["source_name"]?.ToString() ?? item["sourceName"]?.ToString() ?? string.Empty;
                int chunkIndex = (int?)item["chunk_index"] ?? (int?)item["chunkIndex"] ?? 0;
                string metadataJson = item["metadata"] != null
                    ? item["metadata"].ToString(Newtonsoft.Json.Formatting.None)
                    : string.Empty;
                float[] embedding = ParseEmbeddingToken(item["embedding"]);

                list.Add(new DocumentChunk(id, docId, sourceName, chunkIndex, content, embedding, metadataJson));
            }

            return list;
        }

        private static float[] ParseEmbeddingToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token is JArray arr)
            {
                if (arr.Count == 0) return null;
                var vec = new float[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                {
                    vec[i] = (float)arr[i];
                }
                return vec;
            }

            if (token.Type == JTokenType.String)
            {
                string s = token.ToString().Trim();
                if (s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal))
                {
                    s = s.Substring(1, s.Length - 2);
                }
                if (string.IsNullOrWhiteSpace(s)) return null;

                string[] parts = s.Split(',');
                var vec = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        return null;
                    }
                    vec[i] = val;
                }
                return vec;
            }

            return null;
        }

        private string ResolveApiKey(string overrideKey)
        {
            if (!string.IsNullOrWhiteSpace(overrideKey))
            {
                return overrideKey.Trim();
            }

            if (_config != null)
            {
                return _config.GetEffectiveBackendApiKey();
            }

            return string.Empty;
        }

        private string GetBaseRestUrl()
        {
            string dbUri = _config != null ? _config.GetEffectiveDatabaseUri() : null;
            if (string.IsNullOrWhiteSpace(dbUri))
            {
                return string.Empty;
            }

            string uri = dbUri.Trim().TrimEnd('/');
            if (!uri.EndsWith("/rest/v1", StringComparison.OrdinalIgnoreCase))
            {
                uri += "/rest/v1";
            }

            return uri;
        }

        private string GetTableName()
        {
            string tableName = _config != null ? _config.GetEffectiveCollectionOrTable() : null;
            return !string.IsNullOrWhiteSpace(tableName)
                ? tableName.Trim()
                : "documents";
        }

        private string GetRpcFunctionName()
        {
            string rpc = _config != null ? _config.GetEffectiveRpcFunction() : null;
            return !string.IsNullOrWhiteSpace(rpc)
                ? rpc.Trim()
                : "match_documents";
        }

        private static (List<RagSearchResult> results, string error) ParseQueryResults(string json, float minScore, int topK)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    return (new List<RagSearchResult>(), null);
                }

                var token = JToken.Parse(json);
                if (token is JObject obj && obj["error"] != null)
                {
                    string message = obj["error"]?["message"]?.ToString() ?? obj["message"]?.ToString() ?? "Unknown error";
                    return (null, $"Supabase RPC Error: {message}");
                }

                if (!(token is JArray rows))
                {
                    return (new List<RagSearchResult>(), null);
                }

                var results = new List<RagSearchResult>();
                foreach (var item in rows)
                {
                    if (item == null) continue;

                    string content = item["content"]?.ToString() ?? item["text"]?.ToString() ?? string.Empty;
                    float similarity = (float?)item["similarity"] ?? (float?)item["score"] ?? 0f;

                    if (similarity < minScore)
                    {
                        continue;
                    }

                    var chunk = new DocumentChunk
                    {
                        id = item["id"]?.ToString() ?? string.Empty,
                        documentId = item["document_id"]?.ToString() ?? item["documentId"]?.ToString() ?? string.Empty,
                        sourceName = item["source_name"]?.ToString() ?? item["sourceName"]?.ToString() ?? string.Empty,
                        chunkIndex = (int?)item["chunk_index"] ?? (int?)item["chunkIndex"] ?? 0,
                        text = content,
                        metadataJson = item["metadata"] != null ? item["metadata"].ToString(Newtonsoft.Json.Formatting.None) : string.Empty,
                        embedding = null
                    };

                    results.Add(new RagSearchResult(chunk, similarity));
                }

                results.Sort((a, b) => b.score.CompareTo(a.score));
                if (topK > 0 && results.Count > topK)
                {
                    results = results.GetRange(0, topK);
                }

                return (results, null);
            }
            catch (Exception ex)
            {
                return (null, $"Failed to parse Supabase RPC JSON response: {ex.Message}");
            }
        }

        private static string FormatHttpError(UnityWebRequest request, long responseCode, string responseBody)
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                try
                {
                    var json = JObject.Parse(responseBody);
                    string message = json["message"]?.ToString();
                    string hint = json["hint"]?.ToString();
                    string details = json["details"]?.ToString();
                    string code = json["code"]?.ToString();

                    var sb = new StringBuilder();
                    sb.Append($"Supabase error ({responseCode})");
                    if (!string.IsNullOrEmpty(code)) sb.Append($" [{code}]");
                    if (!string.IsNullOrEmpty(message)) sb.Append($": {message}");
                    if (!string.IsNullOrEmpty(details)) sb.Append($" | Details: {details}");
                    if (!string.IsNullOrEmpty(hint)) sb.Append($" | Hint: {hint}");

                    if (responseCode == 404 && responseBody.Contains("relation"))
                    {
                        sb.Append(" (Did you run the pgvector setup SQL script to create the table and RPC function?)");
                    }

                    return sb.ToString();
                }
                catch
                {
                    // Fall back to plain text
                }
            }

            string err = !string.IsNullOrWhiteSpace(responseBody) ? responseBody : request.error;
            return $"Supabase HTTP error ({responseCode}): {err}";
        }

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
