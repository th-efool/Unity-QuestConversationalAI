using UnityEngine;

namespace OpenAI
{
    [CreateAssetMenu(menuName = "OpenAI/Config", fileName = "OpenAIConfig")]
    public class OpenAIConfig : ScriptableObject
    {
        [Tooltip("Arture backend address. Use wss:// for a deployed relay (Render or Railway).")]
        public string relayWebsocketUrl = "wss://arture-voice-relay.onrender.com/realtime";
        [Tooltip("Arture app credential.")]
        public string relayAccessToken;
        public string assetCode = "ATR-DEMO-PUMP-001";
        [Tooltip("Direct OpenAI realtime endpoint if fallback is triggered.")]
        public string directOpenAIWebsocketUrl = "wss://api.openai.com/v1/realtime";
        [Tooltip("Fallback OpenAI API key (sk-...) used if the backend relay cannot be reached.")]
        public string fallbackOpenAIApiKey;
        [Tooltip("When true and an OpenAI API key is available, audio connects straight to OpenAI Realtime and skips the Arture relay (lowest latency). Turn off to route through the relay again.")]
        public bool preferDirectOpenAI = true;

        public string apiKey
        {
            get => fallbackOpenAIApiKey;
            set => fallbackOpenAIApiKey = value;
        }

        [Header("Vector Database (RAG)")]
        [Tooltip("Optional Vector DB / Knowledge Base configuration for semantic RAG retrieval.")]
        public OpenAI.RAG.RagConfig vectorDbConfig;
    }
}
