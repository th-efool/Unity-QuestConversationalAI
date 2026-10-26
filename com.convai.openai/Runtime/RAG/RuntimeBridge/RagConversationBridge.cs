using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using OpenAI;

namespace OpenAI.RAG
{
    /// <summary>
    /// Bridges the RAG vector retrieval system with OpenAI Realtime conversations (<see cref="RealtimeConversationManager"/>).
    /// Supports two operating modes:
    /// 1. <see cref="RagRetrievalMode.AgenticToolCall"/>: Registers a tool in <see cref="AgentToolRegistry"/> so the LLM
    ///    autonomously triggers semantic search during conversation when technical context is needed.
    /// 2. <see cref="RagRetrievalMode.ProactiveContextInjection"/>: Listens to user speech transcription and automatically
    ///    injects relevant domain knowledge context into the Realtime conversation stream before model generation.
    /// Also provides <see cref="SearchAsync(string)"/> for direct programmatic vector queries from custom AR/VR gameplay scripts.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class RagConversationBridge : MonoBehaviour
    {
        #region Inspector Fields

        [Header("Configuration")]
        [Tooltip("The RAG configuration asset defining embeddings, backend provider, similarity threshold, and tool metadata.")]
        public RagConfig ragConfig;

        [Tooltip("Reference to the RealtimeConversationManager. Automatically located on this GameObject or scene if unassigned.")]
        public RealtimeConversationManager conversationManager;

        [Tooltip("Whether to automatically register the tool or subscribe proactive injection during Awake().")]
        public bool registerOnAwake = true;

        [Header("Events")]
        [Tooltip("Fired when a RAG search query starts, passing the search query string.")]
        public UnityEvent<string> onRagQueryStarted = new UnityEvent<string>();

        [Tooltip("Fired when relevant context is retrieved, passing the formatted citation context string.")]
        public UnityEvent<string> onRagContextRetrieved = new UnityEvent<string>();

        [Tooltip("Fired when vector similarity search completes, passing the list of raw search results.")]
        public UnityEvent<List<RagSearchResult>> onRagResultsReceived = new UnityEvent<List<RagSearchResult>>();

        #endregion

        #region Internal State

        private OpenAIEmbeddingClient _embeddingClient;
        private IVectorStoreProvider _provider;
        private bool _isRegistered;
        private string _lastUserTranscript = string.Empty;
        private Coroutine _proactiveDebounceCoroutine;

        /// <summary>
        /// Indicates whether the bridge has completed registration into <see cref="AgentToolRegistry"/> or events.
        /// </summary>
        public bool IsRegistered => _isRegistered;

        /// <summary>
        /// The active vector store provider initialized from <see cref="ragConfig"/>.
        /// </summary>
        public IVectorStoreProvider ActiveProvider => _provider;

        /// <summary>
        /// The active embedding client initialized from <see cref="ragConfig"/>.
        /// </summary>
        public OpenAIEmbeddingClient ActiveEmbeddingClient => _embeddingClient;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ResolveConversationManager();

            if (registerOnAwake && ragConfig != null)
            {
                Register();
            }
        }

        private void OnDestroy()
        {
            Unregister();
        }

        #endregion

        #region Registration & Setup

        /// <summary>
        /// Automatically resolves the <see cref="RealtimeConversationManager"/> if not assigned in Inspector.
        /// </summary>
        public void ResolveConversationManager()
        {
            if (conversationManager != null) return;

            conversationManager = GetComponent<RealtimeConversationManager>();
            if (conversationManager == null)
            {
#if UNITY_2023_1_OR_NEWER
                conversationManager = FindAnyObjectByType<RealtimeConversationManager>();
#else
                conversationManager = FindObjectOfType<RealtimeConversationManager>();
#endif
            }
        }

        /// <summary>
        /// Registers the RAG bridge with the conversation system according to the configured <see cref="RagRetrievalMode"/>.
        /// Safe to call multiple times (idempotent).
        /// </summary>
        public void Register()
        {
            if (ragConfig == null)
            {
                Debug.LogWarning("[OpenAI.RAG] Cannot register RagConversationBridge: ragConfig is null.");
                return;
            }

            if (_isRegistered) return;
            _isRegistered = true;

            EnsureInitialized();

            switch (ragConfig.retrievalMode)
            {
                case RagRetrievalMode.AgenticToolCall:
                    RegisterAgenticTool();
                    break;

                case RagRetrievalMode.ProactiveContextInjection:
                    RegisterProactiveInjection();
                    break;
            }
        }

        /// <summary>
        /// Unregisters event listeners and halts any active debounce coroutines.
        /// </summary>
        public void Unregister()
        {
            if (conversationManager != null)
            {
                conversationManager.onUserTranscript.RemoveListener(OnUserTranscriptReceived);
            }

            if (_proactiveDebounceCoroutine != null)
            {
                StopCoroutine(_proactiveDebounceCoroutine);
                _proactiveDebounceCoroutine = null;
            }

            _isRegistered = false;
        }

        /// <summary>
        /// Forces re-initialization of the embedding client and vector provider from the current <see cref="ragConfig"/>.
        /// Useful if configuration settings or credentials were modified at runtime.
        /// </summary>
        public void RefreshProvider()
        {
            if (ragConfig != null)
            {
                _embeddingClient = new OpenAIEmbeddingClient(ragConfig);
                _provider = VectorProviderFactory.Create(ragConfig);
            }
            else
            {
                _embeddingClient = null;
                _provider = null;
            }
        }

        private void EnsureInitialized()
        {
            if (ragConfig == null) return;

            if (_embeddingClient == null)
            {
                _embeddingClient = new OpenAIEmbeddingClient(ragConfig);
            }

            if (_provider == null)
            {
                _provider = VectorProviderFactory.Create(ragConfig);
            }
        }

        #endregion

        #region Agentic Tool Calling Mode

        private void RegisterAgenticTool()
        {
            string toolName = !string.IsNullOrWhiteSpace(ragConfig.toolName)
                ? ragConfig.toolName.Trim()
                : "search_knowledge_base";

            string toolDesc = !string.IsNullOrWhiteSpace(ragConfig.toolDescription)
                ? ragConfig.toolDescription.Trim()
                : "Searches the domain knowledge base for technical documentation, troubleshooting procedures, manuals, and factual reference context.";

            var toolSpec = new JObject
            {
                ["type"] = "function",
                ["name"] = toolName,
                ["description"] = toolDesc,
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["query"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "Search query to look up relevant facts, manuals, or technical context in the knowledge base."
                        }
                    },
                    ["required"] = new JArray("query")
                }
            };

            AgentToolRegistry.Register(toolName, HandleAgentToolCallAsync, toolSpec);
            Debug.Log($"[OpenAI.RAG] Registered agentic RAG tool '{toolName}' into AgentToolRegistry.");
        }

        private async Task<JObject> HandleAgentToolCallAsync(JObject args)
        {
            string query = args?.Value<string>("query") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return new JObject
                {
                    ["ok"] = false,
                    ["results_count"] = 0,
                    ["context"] = "Empty or missing search query parameter."
                };
            }

            try
            {
                var results = await SearchAsync(query);
                string formattedContext = FormatContext(results);

                return new JObject
                {
                    ["ok"] = true,
                    ["results_count"] = results.Count,
                    ["context"] = formattedContext
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpenAI.RAG] Error executing knowledge base tool call: {ex.Message}");
                return new JObject
                {
                    ["ok"] = false,
                    ["results_count"] = 0,
                    ["context"] = $"Error searching knowledge base: {ex.Message}"
                };
            }
        }

        #endregion

        #region Proactive Context Injection Mode

        private void RegisterProactiveInjection()
        {
            ResolveConversationManager();

            if (conversationManager != null)
            {
                conversationManager.onUserTranscript.RemoveListener(OnUserTranscriptReceived);
                conversationManager.onUserTranscript.AddListener(OnUserTranscriptReceived);
                Debug.Log("[OpenAI.RAG] Registered proactive context injection on conversationManager.onUserTranscript.");
            }
            else
            {
                Debug.LogWarning("[OpenAI.RAG] ProactiveContextInjection mode requires a valid RealtimeConversationManager reference.");
            }
        }

        private void OnUserTranscriptReceived(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return;
            string trimmed = transcript.Trim();
            if (trimmed.Length < 3) return;

            _lastUserTranscript = trimmed;

            if (_proactiveDebounceCoroutine != null)
            {
                StopCoroutine(_proactiveDebounceCoroutine);
            }
            _proactiveDebounceCoroutine = StartCoroutine(DebouncedProactiveQueryCoroutine());
        }

        private System.Collections.IEnumerator DebouncedProactiveQueryCoroutine()
        {
            // Debounce to allow user speech to finish or pause before executing vector query
            yield return new WaitForSeconds(0.45f);
            _proactiveDebounceCoroutine = null;

            string query = _lastUserTranscript;
            if (string.IsNullOrWhiteSpace(query)) yield break;

            _ = ExecuteProactiveSearchAndInjectAsync(query);
        }

        private async Task ExecuteProactiveSearchAndInjectAsync(string query)
        {
            try
            {
                var results = await SearchAsync(query);
                if (results != null && results.Count > 0)
                {
                    string context = FormatContext(results);
                    if (conversationManager != null && !string.IsNullOrWhiteSpace(context))
                    {
                        conversationManager.SendContextMessage(context);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenAI.RAG] Proactive context injection search error: {ex.Message}");
            }
        }

        #endregion

        #region Public Search API

        /// <summary>
        /// Executes an asynchronous semantic vector search against the configured vector database.
        /// Embeds the query string, queries the provider, triggers Unity events, and returns the list of matched results.
        /// </summary>
        /// <param name="query">The text query to embed and search for.</param>
        /// <returns>A list of ranked <see cref="RagSearchResult"/> objects matching the query above the similarity threshold.</returns>
        public async Task<List<RagSearchResult>> SearchAsync(string query)
        {
            if (ragConfig == null)
            {
                Debug.LogWarning("[OpenAI.RAG] SearchAsync called but ragConfig is null.");
                return new List<RagSearchResult>();
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<RagSearchResult>();
            }

            EnsureInitialized();

            onRagQueryStarted?.Invoke(query);

            if (_embeddingClient == null)
            {
                Debug.LogError("[OpenAI.RAG] OpenAIEmbeddingClient could not be initialized.");
                return new List<RagSearchResult>();
            }

            var (embedding, embedErr) = await _embeddingClient.GetEmbeddingAsync(query);
            if (!string.IsNullOrEmpty(embedErr) || embedding == null)
            {
                Debug.LogWarning($"[OpenAI.RAG] Failed to generate embedding for query '{query}': {embedErr}");
                return new List<RagSearchResult>();
            }

            if (_provider == null)
            {
                Debug.LogError("[OpenAI.RAG] Vector store provider could not be created.");
                return new List<RagSearchResult>();
            }

            var (results, queryErr) = await _provider.QueryAsync(
                embedding,
                ragConfig.topK,
                ragConfig.similarityThreshold
            );

            if (!string.IsNullOrEmpty(queryErr))
            {
                Debug.LogWarning($"[OpenAI.RAG] Vector store query returned error: {queryErr}");
                return new List<RagSearchResult>();
            }

            if (results == null)
            {
                results = new List<RagSearchResult>();
            }

            onRagResultsReceived?.Invoke(results);

            string formatted = FormatContext(results);
            onRagContextRetrieved?.Invoke(formatted);

            return results;
        }

        /// <summary>
        /// Formats a list of search results into a clean, structured context string with source citations
        /// and relevance scores, suitable for injection into LLM prompts or realtime context items.
        /// </summary>
        /// <param name="results">List of search results.</param>
        /// <returns>Formatted knowledge context text.</returns>
        public static string FormatContext(List<RagSearchResult> results)
        {
            if (results == null || results.Count == 0)
            {
                return "No relevant documentation or knowledge passages were found in the knowledge base.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Relevant Knowledge Base Context:");
            for (int i = 0; i < results.Count; i++)
            {
                var res = results[i];
                var chunk = res.chunk;
                var source = !string.IsNullOrEmpty(chunk?.sourceName) ? chunk.sourceName : "Unknown Source";
                var index = chunk != null ? chunk.chunkIndex : i;
                var text = chunk?.text?.Trim() ?? string.Empty;

                sb.AppendLine($"[Reference {i + 1}: {source} (Chunk #{index}) | Score: {res.score:F2}]");
                sb.AppendLine(text);
                if (i < results.Count - 1)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        #endregion
    }
}
