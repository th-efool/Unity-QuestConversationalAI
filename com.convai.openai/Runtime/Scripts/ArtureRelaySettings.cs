using System;
using UnityEngine;

namespace OpenAI
{
    [Serializable]
    public sealed class ArtureRelaySettings
    {
        public string url;
        public string deviceUrl;
        public string cloudUrl = "wss://arture-voice-relay.onrender.com/realtime";
        public string accessToken;
        public string assetCode;
        public string workerPhone;
        public string openAIApiKey;
        public string directOpenAIUrl = "wss://api.openai.com/v1/realtime";
        public bool isDirectOpenAI;

        public static ArtureRelaySettings Load(OpenAIConfig config)
        {
            if (config == null)
            {
                config = Resources.Load<OpenAIConfig>("OpenAIConfig");
            }
            var settings = new ArtureRelaySettings
            {
                url = config != null && !string.IsNullOrWhiteSpace(config.relayWebsocketUrl)
                    ? config.relayWebsocketUrl
                    : "wss://arture-voice-relay.onrender.com/realtime",
                accessToken = config != null ? config.relayAccessToken : "",
                assetCode = config != null && !string.IsNullOrWhiteSpace(config.assetCode)
                    ? config.assetCode
                    : "ATR-DEMO-PUMP-001",
                openAIApiKey = config != null ? config.fallbackOpenAIApiKey : "",
                directOpenAIUrl = config != null && !string.IsNullOrWhiteSpace(config.directOpenAIWebsocketUrl)
                    ? config.directOpenAIWebsocketUrl
                    : "wss://api.openai.com/v1/realtime"
            };

            var local = Resources.Load<TextAsset>("ArtureRelayConnection");
            if (local != null)
            {
                try
                {
                    var supplied = JsonUtility.FromJson<ArtureRelaySettings>(local.text);
                    if (!string.IsNullOrWhiteSpace(supplied.url)) settings.url = supplied.url;
                    if (!string.IsNullOrWhiteSpace(supplied.deviceUrl)) settings.deviceUrl = supplied.deviceUrl;
                    if (!string.IsNullOrWhiteSpace(supplied.cloudUrl)) settings.cloudUrl = supplied.cloudUrl;
                    if (!string.IsNullOrWhiteSpace(supplied.accessToken)) settings.accessToken = supplied.accessToken;
                    if (!string.IsNullOrWhiteSpace(supplied.assetCode)) settings.assetCode = supplied.assetCode;
                    if (!string.IsNullOrWhiteSpace(supplied.workerPhone)) settings.workerPhone = supplied.workerPhone;
                    if (!string.IsNullOrWhiteSpace(supplied.openAIApiKey)) settings.openAIApiKey = supplied.openAIApiKey;
                    if (!string.IsNullOrWhiteSpace(supplied.directOpenAIUrl)) settings.directOpenAIUrl = supplied.directOpenAIUrl;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Arture] Failed parsing ArtureRelayConnection.json: {ex.Message}");
                }
            }

            if (Application.isMobilePlatform && !Application.isEditor && !string.IsNullOrWhiteSpace(settings.deviceUrl))
                settings.url = settings.deviceUrl;

#if UNITY_EDITOR
            var editorUrl = Environment.GetEnvironmentVariable("ARTURE_RELAY_URL");
            var editorToken = Environment.GetEnvironmentVariable("ARTURE_RELAY_ACCESS_KEY");
            var editorOpenAiKey = Environment.GetEnvironmentVariable("ARTURE_OPENAI_API_KEY") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(editorUrl)) settings.url = editorUrl;
            if (!string.IsNullOrWhiteSpace(editorToken)) settings.accessToken = editorToken;
            if (!string.IsNullOrWhiteSpace(editorOpenAiKey)) settings.openAIApiKey = editorOpenAiKey;
#endif

            // Check if user pointed primary URL directly to OpenAI
            if (Uri.TryCreate(settings.url, UriKind.Absolute, out var uri) && uri.Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase))
            {
                settings.isDirectOpenAI = true;
                if (string.IsNullOrWhiteSpace(settings.openAIApiKey) && !string.IsNullOrWhiteSpace(settings.accessToken) && settings.accessToken.StartsWith("sk-"))
                {
                    settings.openAIApiKey = settings.accessToken;
                }
            }

            // If accessToken is an OpenAI key and openAIApiKey was not explicitly supplied, assign it
            if (string.IsNullOrWhiteSpace(settings.openAIApiKey) && !string.IsNullOrWhiteSpace(settings.accessToken) && settings.accessToken.StartsWith("sk-"))
            {
                settings.openAIApiKey = settings.accessToken;
            }

            if (config != null && config.preferDirectOpenAI && !string.IsNullOrWhiteSpace(settings.openAIApiKey))
            {
                settings.isDirectOpenAI = true;
            }

            return settings;
        }
    }
}
