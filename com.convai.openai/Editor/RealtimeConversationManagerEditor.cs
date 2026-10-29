using System;
using OpenAI;
using UnityEditor;
using UnityEngine;

namespace OpenAI.Editor
{
    /// <summary>
    /// Custom Inspector for RealtimeConversationManager that embeds an interactive
    /// Text-to-Voice test console directly inside the Unity Inspector during Play Mode.
    /// </summary>
    [CustomEditor(typeof(RealtimeConversationManager))]
    public class RealtimeConversationManagerEditor : UnityEditor.Editor
    {
        private string _inspectorInputText = string.Empty;
        private static readonly string[] DefaultTestQueries = new[]
        {
            "Hello! Can you hear me clearly?",
            "Search the knowledge base for key information.",
            "Tell me a short 1-sentence joke."
        };

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var manager = (RealtimeConversationManager)target;
            if (manager == null) return;

            EditorGUILayout.Space(12);
            DrawTestConsoleSection(manager);
        }

        private void DrawTestConsoleSection(RealtimeConversationManager manager)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                richText = true
            };
            EditorGUILayout.LabelField("💬 <b>Realtime Text-to-Voice Console</b>", titleStyle);

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to type and send queries to the AI directly from the Editor. " +
                    "The AI will respond with full voice audio, RAG retrieval, and scene tools without requiring a microphone.",
                    MessageType.Info);

                var existingProp = FindAnyObjectByType<RealtimeTextChatProp>();
                if (existingProp == null)
                {
                    if (GUILayout.Button("➕ Add On-Screen RealtimeTextChatProp to Scene", GUILayout.Height(26)))
                    {
                        var propGo = new GameObject("RealtimeTextChatProp");
                        propGo.AddComponent<RealtimeTextChatProp>();
                        Undo.RegisterCreatedObjectUndo(propGo, "Create RealtimeTextChatProp");
                        Selection.activeGameObject = propGo;
                        Debug.Log("[OpenAI] Added RealtimeTextChatProp to scene.");
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("✓ RealtimeTextChatProp is present in active scene.", EditorStyles.miniLabel);
                }

                EditorGUILayout.EndVertical();
                return;
            }

            // --- PLAY MODE INTERACTIVE CONSOLE ---
            bool isReady = manager.IsSessionReady;
            bool isSpeaking = manager.IsAgentSpeaking;
            bool isMuted = manager.IsMicrophoneMuted;

            EditorGUILayout.BeginHorizontal();
            string statusBadge = isReady ? "<color=#2E7D32>● Connected</color>" : "<color=#C62828>● Disconnected</color>";
            string speakingBadge = isSpeaking ? " | <color=#EF6C00>🔊 AI Speaking</color>" : " | <color=#757575>Idle</color>";
            GUIStyle badgeStyle = new GUIStyle(EditorStyles.label) { richText = true };
            EditorGUILayout.LabelField($"Status: {statusBadge}{speakingBadge}", badgeStyle);

            string muteBtnText = isMuted ? "🔇 Unmute Mic" : "🎤 Mute Mic";
            if (GUILayout.Button(muteBtnText, EditorStyles.miniButton, GUILayout.Width(90)))
            {
                manager.SetMicrophoneMuted(!isMuted);
            }
            EditorGUILayout.EndHorizontal();

            if (isSpeaking)
            {
                if (GUILayout.Button("⏹️ Interrupt Assistant (Barge-In)", GUILayout.Height(24)))
                {
                    manager.InterruptNow();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Quick Test Queries:", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            foreach (var query in DefaultTestQueries)
            {
                string label = query.Length > 20 ? query.Substring(0, 18) + ".." : query;
                if (GUILayout.Button(new GUIContent(label, query), EditorStyles.miniButton))
                {
                    manager.SendUserTextMessage(query);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Type Query & Send (AI speaks back aloud):", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _inspectorInputText = EditorGUILayout.TextField(_inspectorInputText, GUILayout.Height(24));

            bool canSend = isReady && !string.IsNullOrWhiteSpace(_inspectorInputText);
            GUI.enabled = canSend;
            if (GUILayout.Button("Send 🚀", GUILayout.Width(75), GUILayout.Height(24)))
            {
                manager.SendUserTextMessage(_inspectorInputText);
                _inspectorInputText = string.Empty;
                GUI.FocusControl(null);
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // Detect Enter key inside the Inspector text field
            Event evt = Event.current;
            if (canSend && evt.type == EventType.KeyDown && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter))
            {
                manager.SendUserTextMessage(_inspectorInputText);
                _inspectorInputText = string.Empty;
                evt.Use();
                GUI.FocusControl(null);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
