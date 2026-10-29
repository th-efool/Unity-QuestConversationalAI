using System;
using System.Collections.Generic;
using OpenAI;
using OpenAI.RAG;
using UnityEngine;

namespace OpenAI
{
    /// <summary>
    /// Interactive Play-Mode UI prop and text console for testing OpenAI Realtime conversations.
    /// Lets you type queries and receive full AI voice responses, tool calls, and RAG retrieval
    /// directly in the Unity Editor without speaking into a microphone or building an APK.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public class RealtimeTextChatProp : MonoBehaviour
    {
        public static RealtimeTextChatProp Instance { get; private set; }

        [Header("Prop Configuration")]
        [Tooltip("Whether to draw the on-screen text chat prop in Play Mode.")]
        [SerializeField] private bool showInPlayMode = true;

        [Tooltip("Keyboard shortcut to toggle hide/show the chat console window.")]
        [SerializeField] private KeyCode toggleKey = KeyCode.F2;

        [Tooltip("Whether the chat prop starts collapsed into a compact pill button.")]
        [SerializeField] private bool startMinimized = false;

        [Tooltip("Automatically mute the PC microphone in Editor to prevent keyboard noise or speaker feedback from triggering VAD.")]
        [SerializeField] private bool autoMuteMicInEditor = true;

        [Tooltip("Attach camera/vision frame (via RealtimeConversationManager.OnRequestImage) when sending text queries.")]
        [SerializeField] private bool attachCameraFrame = true;

        [Header("Preset Chips (1-Click Test Queries)")]
        [SerializeField] private string[] quickPrompts = new string[]
        {
            "Hello! Can you hear me clearly?",
            "Search the knowledge base for key information.",
            "Tell me a short 1-sentence joke."
        };

        [Header("Scene References (Auto-detected if null)")]
        [SerializeField] private RealtimeConversationManager conversationManager;
        [SerializeField] private MicrophoneStreamer micStreamer;
        [SerializeField] private RagConversationBridge ragBridge;

        private class ChatMessage
        {
            public bool IsUser;
            public string Text;
            public string Timestamp;
            public bool IsDeltaStreaming;
        }

        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private const int MaxMessages = 60;
        private Vector2 _scrollPos;
        private string _inputText = string.Empty;
        private bool _minimized;
        private bool _agentSpeaking;
        private bool _userSpeaking;
        private bool _eventsBound;
        private bool _shouldRefocusInput;
        private const string InputControlName = "RealtimeTextChatPropInput";

        // GUI Styles
        private GUIStyle _windowBoxStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _statusStyle;
        private GUIStyle _userBubbleStyle;
        private GUIStyle _aiBubbleStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _activeButtonStyle;
        private GUIStyle _chipButtonStyle;
        private GUIStyle _textFieldStyle;
        private Texture2D _bgTex;
        private Texture2D _activeBtnTex;
        private Texture2D _chipBtnTex;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _minimized = startMinimized;
        }

        private void Start()
        {
            BindComponents();

#if UNITY_EDITOR
            if (autoMuteMicInEditor && conversationManager != null)
            {
                conversationManager.SetMicrophoneMuted(true);
            }
#endif
        }

        private void Update()
        {
            if (conversationManager == null)
            {
                BindComponents();
            }

            if (Input.GetKeyDown(toggleKey))
            {
                _minimized = !_minimized;
            }
        }

        private void BindComponents()
        {
            if (conversationManager == null)
                conversationManager = FindAnyObjectByType<RealtimeConversationManager>();

            if (micStreamer == null)
                micStreamer = FindAnyObjectByType<MicrophoneStreamer>();

            if (ragBridge == null)
                ragBridge = FindAnyObjectByType<RagConversationBridge>();

            if (conversationManager != null && !_eventsBound)
            {
                _eventsBound = true;

                conversationManager.onAgentSpeaking.AddListener(speaking =>
                {
                    _agentSpeaking = speaking;
                    if (!speaking && _messages.Count > 0 && _messages[_messages.Count - 1].IsDeltaStreaming)
                    {
                        _messages[_messages.Count - 1].IsDeltaStreaming = false;
                    }
                });

                conversationManager.onUserSpeaking.AddListener(speaking =>
                {
                    _userSpeaking = speaking;
                });

                conversationManager.onUserTranscript.AddListener(text =>
                {
                    if (string.IsNullOrWhiteSpace(text)) return;
                    AddMessage(true, text.Trim());
                });

                conversationManager.onAgentTranscript.AddListener(delta =>
                {
                    if (string.IsNullOrEmpty(delta)) return;
                    AppendAgentDelta(delta);
                });
            }
        }

        private void AddMessage(bool isUser, string text)
        {
            _messages.Add(new ChatMessage
            {
                IsUser = isUser,
                Text = text,
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                IsDeltaStreaming = !isUser
            });

            if (_messages.Count > MaxMessages)
                _messages.RemoveAt(0);

            _scrollPos.y = float.MaxValue;
        }

        private void AppendAgentDelta(string delta)
        {
            if (_messages.Count > 0 && !_messages[_messages.Count - 1].IsUser && _messages[_messages.Count - 1].IsDeltaStreaming)
            {
                _messages[_messages.Count - 1].Text += delta;
            }
            else
            {
                AddMessage(false, delta);
            }
            _scrollPos.y = float.MaxValue;
        }

        public void SendText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            if (conversationManager == null)
            {
                conversationManager = FindAnyObjectByType<RealtimeConversationManager>();
                if (conversationManager == null)
                {
                    Debug.LogWarning("[RealtimeTextChatProp] RealtimeConversationManager not found in scene.");
                    return;
                }
            }

            conversationManager.SendUserTextMessage(text, attachCameraFrame);
            _inputText = string.Empty;
            _shouldRefocusInput = true;
        }

        #region GUI Rendering

        private void EnsureStyles(float scale)
        {
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0.06f, 0.08f, 0.12f, 0.94f));
                _bgTex.Apply();
            }

            if (_activeBtnTex == null)
            {
                _activeBtnTex = new Texture2D(1, 1);
                _activeBtnTex.SetPixel(0, 0, new Color(0.18f, 0.55f, 0.34f, 0.95f));
                _activeBtnTex.Apply();
            }

            if (_chipBtnTex == null)
            {
                _chipBtnTex = new Texture2D(1, 1);
                _chipBtnTex.SetPixel(0, 0, new Color(0.12f, 0.22f, 0.32f, 0.90f));
                _chipBtnTex.Apply();
            }

            int fontSize = Mathf.RoundToInt(12f * scale);
            int headerSize = Mathf.RoundToInt(13f * scale);

            if (_windowBoxStyle == null)
            {
                _windowBoxStyle = new GUIStyle(GUI.skin.box)
                {
                    normal = { background = _bgTex, textColor = Color.white },
                    padding = new RectOffset(8, 8, 8, 8)
                };
            }

            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    richText = true
                };
            }
            _headerStyle.fontSize = headerSize;

            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    wordWrap = true
                };
            }
            _statusStyle.fontSize = fontSize;

            if (_userBubbleStyle == null)
            {
                _userBubbleStyle = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    wordWrap = true,
                    margin = new RectOffset(0, 0, 2, 2)
                };
            }
            _userBubbleStyle.fontSize = fontSize;

            if (_aiBubbleStyle == null)
            {
                _aiBubbleStyle = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    wordWrap = true,
                    margin = new RectOffset(0, 0, 2, 2)
                };
            }
            _aiBubbleStyle.fontSize = fontSize;

            if (_buttonStyle == null)
            {
                _buttonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold
                };
            }
            _buttonStyle.fontSize = fontSize;

            if (_activeButtonStyle == null)
            {
                _activeButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold,
                    normal = { background = _activeBtnTex, textColor = Color.white }
                };
            }
            _activeButtonStyle.fontSize = fontSize;

            if (_chipButtonStyle == null)
            {
                _chipButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    normal = { background = _chipBtnTex, textColor = new Color(0.85f, 0.92f, 1.0f) },
                    margin = new RectOffset(2, 2, 2, 2),
                    padding = new RectOffset(6, 6, 3, 3)
                };
            }
            _chipButtonStyle.fontSize = Mathf.RoundToInt(11f * scale);

            if (_textFieldStyle == null)
            {
                _textFieldStyle = new GUIStyle(GUI.skin.textField)
                {
                    fontSize = fontSize
                };
            }
            _textFieldStyle.fontSize = fontSize;
        }

        private void OnGUI()
        {
            if (!showInPlayMode || !Application.isPlaying) return;

            float scale = Mathf.Clamp(Screen.dpi > 0 ? Screen.dpi / 160f : 1.3f, 1.0f, 2.2f);
            EnsureStyles(scale);

            Rect safe = Screen.safeArea;
            float panelWidth = Mathf.Min(Screen.width - 24f, 480f * scale);
            float x = Screen.width - panelWidth - 14f;
            float y = Screen.height - safe.yMin - 14f;

            bool isConnected = conversationManager != null && conversationManager.IsSessionReady;
            bool isMicMuted = conversationManager != null && conversationManager.IsMicrophoneMuted;

            if (_minimized)
            {
                float btnW = 200f * scale;
                float btnH = 38f * scale;
                Rect minRect = new Rect(Screen.width - btnW - 14f, Screen.height - btnH - 14f, btnW, btnH);

                string minLabel = _agentSpeaking
                    ? "🔊 AI Speaking... [Open]"
                    : (isConnected ? "💬 AI Chat Prop (F2)" : "💬 AI Chat (Offline)");

                GUIStyle minStyle = _agentSpeaking ? _activeButtonStyle : _buttonStyle;
                if (GUI.Button(minRect, minLabel, minStyle))
                {
                    _minimized = false;
                }
                return;
            }

            float panelHeight = Mathf.Min(Screen.height * 0.65f, 440f * scale);
            Rect windowRect = new Rect(x, Screen.height - panelHeight - 14f, panelWidth, panelHeight);
            GUI.Box(windowRect, GUIContent.none, _windowBoxStyle);

            GUILayout.BeginArea(new Rect(windowRect.x + 8f, windowRect.y + 6f, windowRect.width - 16f, windowRect.height - 12f));

            // 1. Title & Top Bar
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>💬 Realtime Text-to-Voice Prop</b>", _headerStyle);
            GUILayout.FlexibleSpace();

            string muteLabel = isMicMuted ? "🔇 Mic: Muted" : "🎤 Mic: Live";
            GUIStyle muteStyle = isMicMuted ? _buttonStyle : _activeButtonStyle;
            if (GUILayout.Button(muteLabel, muteStyle, GUILayout.Height(24f * scale)))
            {
                conversationManager?.SetMicrophoneMuted(!isMicMuted);
            }

            if (GUILayout.Button("Clear", _buttonStyle, GUILayout.Width(50f * scale), GUILayout.Height(24f * scale)))
            {
                _messages.Clear();
            }

            if (GUILayout.Button("Hide (F2)", _buttonStyle, GUILayout.Width(70f * scale), GUILayout.Height(24f * scale)))
            {
                _minimized = true;
            }
            GUILayout.EndHorizontal();

            // 2. Compact Status & Interrupt Bar
            GUILayout.BeginHorizontal();
            string connColor = isConnected ? "#66BB6A" : "#FF5252";
            string connText = isConnected ? "Session Ready" : "Disconnected / Connecting";
            string aiText = _agentSpeaking ? "<color=#FFA726>Speaking (Audio Out)</color>" : "<color=#B0BEC5>Idle</color>";
            string ragText = ragBridge != null && ragBridge.isActiveAndEnabled && ragBridge.ragConfig != null
                ? $" | <color=#81D4FA>RAG: {ragBridge.ragConfig.retrievalMode}</color>"
                : string.Empty;

            GUILayout.Label($"<b>Status:</b> <color={connColor}>{connText}</color> | <b>AI:</b> {aiText}{ragText}", _statusStyle);
            GUILayout.FlexibleSpace();

            if (_agentSpeaking)
            {
                if (GUILayout.Button("⏹️ Stop", _buttonStyle, GUILayout.Width(65f * scale), GUILayout.Height(22f * scale)))
                {
                    conversationManager?.InterruptNow();
                }
            }
            GUILayout.EndHorizontal();

            // 3. Quick-Prompt Chips
            if (quickPrompts != null && quickPrompts.Length > 0)
            {
                GUILayout.Space(2f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("<color=#90CAF9><b>Quick Test:</b></color>", _statusStyle, GUILayout.Width(75f * scale));
                for (int i = 0; i < quickPrompts.Length; i++)
                {
                    string prompt = quickPrompts[i];
                    if (string.IsNullOrWhiteSpace(prompt)) continue;

                    string display = prompt.Length > 24 ? prompt.Substring(0, 22) + ".." : prompt;
                    if (GUILayout.Button(display, _chipButtonStyle, GUILayout.Height(22f * scale)))
                    {
                        SendText(prompt);
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(2f);

            // 4. Scrollable Chat History
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);
            if (_messages.Count == 0)
            {
                GUILayout.Label("<color=#78909C><i>No messages yet. Type a query below or click a Quick Test chip to test AI voice response without a microphone!</i></color>", _statusStyle);
            }
            else
            {
                for (int i = 0; i < _messages.Count; i++)
                {
                    var msg = _messages[i];
                    if (msg.IsUser)
                    {
                        GUILayout.Label($"<color=#81C784><b>🧑 You ({msg.Timestamp}):</b></color> {msg.Text}", _userBubbleStyle);
                    }
                    else
                    {
                        string streamingSuffix = msg.IsDeltaStreaming ? " <color=#FFA726>●</color>" : "";
                        GUILayout.Label($"<color=#64B5F6><b>🤖 AI Voice ({msg.Timestamp}):</b></color> {msg.Text}{streamingSuffix}", _aiBubbleStyle);
                    }
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);

            // 5. Text Input & Send Bar
            Event currentEvent = Event.current;
            bool enterPressed = false;
            if (GUI.GetNameOfFocusedControl() == InputControlName && currentEvent.type == EventType.KeyDown)
            {
                if (currentEvent.keyCode == KeyCode.Return || currentEvent.keyCode == KeyCode.KeypadEnter)
                {
                    enterPressed = true;
                    currentEvent.Use();
                }
            }

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(InputControlName);
            _inputText = GUILayout.TextField(_inputText, _textFieldStyle, GUILayout.Height(28f * scale));

            GUI.enabled = isConnected && !string.IsNullOrWhiteSpace(_inputText);
            if (GUILayout.Button("Send 🚀", _buttonStyle, GUILayout.Width(75f * scale), GUILayout.Height(28f * scale)) || enterPressed)
            {
                SendText(_inputText);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_shouldRefocusInput)
            {
                _shouldRefocusInput = false;
                GUI.FocusControl(InputControlName);
            }

            GUILayout.EndArea();
        }

        #endregion
    }
}
