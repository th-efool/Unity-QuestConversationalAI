using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenAI.RAG
{
    /// <summary>
    /// UnityWebRequest-based client for generating text embeddings using the OpenAI Embeddings API.
    /// Operates asynchronously using TaskCompletionSource, compatible with both Editor (outside Play Mode)
    /// and runtime (Quest, Android, Windows, iOS).
    /// </summary>
    public class OpenAIEmbeddingClient
    {
        private readonly RagConfig _config;

        /// <summary>
        /// Initializes a new instance of the OpenAIEmbeddingClient with the specified RAG configuration.
        /// </summary>
        /// <param name="config">RAG configuration asset supplying endpoint, model, and API keys.</param>
        public OpenAIEmbeddingClient(RagConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Generates a single embedding vector for the provided text.
        /// </summary>
        /// <param name="text">Text string to embed.</param>
        /// <param name="apiKeyOverride">Optional API key override (e.g. editor admin key).</param>
        /// <returns>A tuple of (embedding vector, error message if any).</returns>
        public async Task<(float[] embedding, string error)> GetEmbeddingAsync(string text, string apiKeyOverride = null)
        {
            if (text == null)
            {
                return (null, "Input text cannot be null.");
            }

            string sanitized = SanitizeInput(text);
            var (embeddings, error) = await ExecuteEmbeddingRequestAsync(new JValue(sanitized), apiKeyOverride);
            if (!string.IsNullOrEmpty(error))
            {
                return (null, error);
            }

            if (embeddings == null || embeddings.Count == 0)
            {
                return (null, "OpenAI API did not return any embeddings.");
            }

            return (embeddings[0], null);
        }

        /// <summary>
        /// Generates embedding vectors for a batch of input texts.
        /// Results are guaranteed to match the order of the input texts based on the OpenAI response 'index'.
        /// </summary>
        /// <param name="texts">List of text chunks to embed.</param>
        /// <param name="apiKeyOverride">Optional API key override.</param>
        /// <returns>A tuple of (list of embedding vectors, error message if any).</returns>
        public async Task<(List<float[]> embeddings, string error)> GetEmbeddingsBatchAsync(IReadOnlyList<string> texts, string apiKeyOverride = null)
        {
            if (texts == null)
            {
                return (null, "Texts list cannot be null.");
            }

            if (texts.Count == 0)
            {
                return (new List<float[]>(), null);
            }

            var inputArray = new JArray();
            for (int i = 0; i < texts.Count; i++)
            {
                inputArray.Add(SanitizeInput(texts[i]));
            }

            return await ExecuteEmbeddingRequestAsync(inputArray, apiKeyOverride);
        }

        /// <summary>
        /// Dispatches the HTTP POST request to OpenAI embeddings endpoint and parses the resulting vector(s).
        /// </summary>
        private async Task<(List<float[]> embeddings, string error)> ExecuteEmbeddingRequestAsync(JToken inputToken, string apiKeyOverride)
        {
            string apiKey = !string.IsNullOrWhiteSpace(apiKeyOverride)
                ? apiKeyOverride
                : _config != null ? _config.EffectiveOpenAIApiKey : null;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (null, "OpenAI API key is missing. Set it in RagConfig or provide apiKeyOverride.");
            }

            string endpoint = !string.IsNullOrWhiteSpace(_config?.embeddingEndpoint)
                ? _config.embeddingEndpoint
                : "https://api.openai.com/v1/embeddings";

            string model = !string.IsNullOrWhiteSpace(_config?.embeddingModel)
                ? _config.embeddingModel
                : "text-embedding-3-small";

            var requestBody = new JObject
            {
                ["model"] = model,
                ["input"] = inputToken,
                ["encoding_format"] = "float"
            };

            string jsonString = requestBody.ToString(Newtonsoft.Json.Formatting.None);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonString);

            using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = 60;

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

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return (null, "Empty response received from OpenAI embeddings API.");
                }

                return ParseEmbeddingsResponse(responseText);
            }
        }

        /// <summary>
        /// Formats error information from a failed UnityWebRequest, extracting API error details if available.
        /// </summary>
        private static string FormatHttpError(UnityWebRequest request, long responseCode, string responseBody)
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                try
                {
                    var errorJson = JObject.Parse(responseBody);
                    if (errorJson["error"] != null)
                    {
                        string message = errorJson["error"]["message"]?.ToString();
                        string type = errorJson["error"]["type"]?.ToString();
                        string code = errorJson["error"]["code"]?.ToString();

                        if (responseCode == 429)
                        {
                            return $"OpenAI Rate Limit Exceeded (429) [{(code ?? type ?? "rate_limit")}]: {message}";
                        }

                        return $"OpenAI API Error ({responseCode}) [{(code ?? type ?? "error")}]: {message}";
                    }
                }
                catch
                {
                    // Fall back to generic format if response wasn't JSON
                }
            }

            if (responseCode == 429)
            {
                return "OpenAI Rate Limit Exceeded (429). Please wait before retrying.";
            }

            string details = !string.IsNullOrWhiteSpace(responseBody) ? responseBody : request.error;
            return $"HTTP error ({responseCode}): {details}";
        }

        /// <summary>
        /// Parses the JSON response from OpenAI Embeddings endpoint and sorts results by index.
        /// </summary>
        private static (List<float[]> embeddings, string error) ParseEmbeddingsResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                if (root["error"] != null)
                {
                    string message = root["error"]["message"]?.ToString() ?? "Unknown error";
                    return (null, $"OpenAI API Error: {message}");
                }

                var data = root["data"] as JArray;
                if (data == null)
                {
                    return (null, "OpenAI response missing 'data' array.");
                }

                var sortedData = data
                    .OrderBy(item => (int?)item["index"] ?? 0)
                    .ToList();

                var results = new List<float[]>(sortedData.Count);
                foreach (var item in sortedData)
                {
                    var embArray = item["embedding"] as JArray;
                    if (embArray == null)
                    {
                        return (null, "Embedding item missing 'embedding' array.");
                    }

                    int length = embArray.Count;
                    float[] vector = new float[length];
                    for (int i = 0; i < length; i++)
                    {
                        vector[i] = (float)embArray[i];
                    }
                    results.Add(vector);
                }

                return (results, null);
            }
            catch (Exception ex)
            {
                return (null, $"Failed to parse embeddings JSON response: {ex.Message}");
            }
        }

        /// <summary>
        /// Normalizes text according to OpenAI embedding recommendations by replacing newlines with spaces.
        /// </summary>
        private static string SanitizeInput(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
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
