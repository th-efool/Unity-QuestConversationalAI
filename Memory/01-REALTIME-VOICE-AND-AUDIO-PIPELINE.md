# 01 — Realtime Voice, WebSocket Transport, Tools & 24kHz Audio Playback

> **Scope**: `com.convai.openai/Runtime/Scripts/**`, `com.convai.openai/Runtime/Common/**`.
> **Update Rule**: If you modify WebSocket connection logic, event handlers, tool execution, `PcmAudioPlayer`, `MicrophoneStreamer`, `OpenAIConfig`, `ArtureRelaySettings`, or voice speed, update this file before completing your turn.

---

## 1. Architectural Flow (`RealtimeConversationManager`)

```text
RealtimeConversationManager.Awake()
   ├── PrewarmCloudRelay() (skipped when ArtureRelaySettings.isDirectOpenAI == true)
   └── EnsureRagBridge() -> attaches & registers RagConversationBridge + starts RagLocalCache

RealtimeConversationManager.StartAgent()
   ├── Resolves connection mode via ArtureRelaySettings.LoadOrDefault():
   │    ├── Mode A (Direct OpenAI): wss://api.openai.com/v1/realtime?model=<model>
   │    │   Headers: Authorization: Bearer <openAIApiKey>
   │    └── Mode B (WebSocket Relay): wss://<relay-host>/realtime?assetCode=<assetCode>
   │        Headers: Authorization: Bearer <relayAccessToken>
   │        (Auto-falls back to Mode A after 5 relay connection failures if openAIApiKey is present)
   │
   ├── WebSocket Open -> OpenAI sends `session.created`
   ├── SendSessionUpdate() -> sends GA Realtime `session.update`:
   │    ├── type: "realtime", output_modalities: ["audio"]
   │    ├── audio.input: 24kHz PCM16, gpt-4o-mini-transcribe, near_field noise reduction, server_vad (350ms silence)
   │    ├── audio.output: 24kHz PCM16, voice, speed: voiceSpeed (default 1.15f)
   │    └── tools: merged Inspector ToolBindings + AgentToolRegistry (RAG `search_knowledge_base`, etc.)
   │
   └── Runtime Loop (Update -> websocket.DispatchMessageQueue()):
        ├── Incoming Audio (`response.output_audio.delta`) -> PcmAudioPlayer.EnqueueBase64Audio()
        ├── Incoming Tool Call (`response.function_call_arguments.done` / `response.output_item.done`) ->
        │     ExecuteToolCallIfReady() -> sends `conversation.item.create` (function_call_output) + `response.create`
        └── Incoming Transcript (`conversation.item.input_audio_transcription.completed`) -> fires `onUserTranscript`
```

---

## 2. Core Files & Exact Responsibilities

| File Path (under `com.convai.openai/`) | Class | Key Responsibilities & Invariants |
| :--- | :--- | :--- |
| `Runtime/Scripts/RealtimeConversationManager.cs` | `RealtimeConversationManager` | Central WebSocket client (`NativeWebSocket`), event router (`OnSocketMessage`), tool execution coordinator (`ExecuteToolCallIfReady`), text message sender (`SendUserTextMessage`), barge-in canceller (`InterruptNow` / `TryCancelAssistantResponse`), `voiceSpeed = 1.15f`, and `[Latency]` first-audio telemetry logger. |
| `Runtime/Scripts/ArtureRelaySettings.cs` | `ArtureRelaySettings` | Resolves runtime connection config from `OpenAIConfig.asset`, `ArtureRelayConnection.json`, `RagConfig.asset`, and `.env` files. Exposes `isDirectOpenAI`, `directOpenAIUrl` (`wss://api.openai.com/v1/realtime`), and `openAIApiKey`. |
| `Runtime/Scripts/OpenAIConfig.cs` | `OpenAIConfig : ScriptableObject` | Stores `useDirectOpenAI`, `directOpenAIUrl`, `apiKey`, `relayUrl`, `deviceRelayUrl`, `relayAccessToken`, `assetCode`, and `workerPhone`. |
| `Runtime/Scripts/AgentToolRegistry.cs` | `AgentToolRegistry` | Static registry mapping tool names to async `Func<JObject, Task<object>>` handlers and `JArray` tool schemas for `session.update`. Used by `RagConversationBridge` (`search_knowledge_base`) and custom runtime tools. |
| `Runtime/Scripts/ToolBindings.cs` | `ToolBindings`, `ToolSpec` | Inspector-configurable UnityEvent tool bindings merged with `AgentToolRegistry` during `BuildAllToolsSpec()`. |
| `Runtime/Scripts/ToolCallState.cs` | `ToolCallState` | Tracks in-flight function calls (`callId`, `name`, streamed `arguments` buffer) and prevents duplicate execution across `arguments.done` and `output_item.done`. |
| `Runtime/Scripts/OpenAIWebSocketEvents.cs` | `OpenAIWebSocketEvents` | Strongly-typed JSON payload builders for OpenAI Realtime API client events (`session.update`, `input_audio_buffer.append`, `conversation.item.create`, `response.create`). |
| `Runtime/Scripts/RealtimeTextChatProp.cs` | `RealtimeTextChatProp` | World-space / Canvas text chat UI helper bound to `RealtimeConversationManager`. |
| `Runtime/Common/PcmAudioPlayer.cs` | `PcmAudioPlayer` | Decodes base64 24kHz PCM16 chunks into `AudioClip`s, queues them, plays sequentially, `Destroy()`s finished clips to prevent native memory leaks, and exposes **`IsPlaying`** (`audioSource.isPlaying \|\| _clipQueue.Count > 0`). |
| `Runtime/Common/MicrophoneStreamer.cs` | `MicrophoneStreamer` | Basic microphone capture component for standalone sample scenes. |
| `Runtime/Common/ImageEncodingUtil.cs` | `ImageEncodingUtil` | Texture2D / RenderTexture to base64 JPEG/PNG encoding helper for multimodal vision turns. |

---

## 3. Hard Audio, Latency & Anti-Spam Invariants (DO NOT BREAK)

1. **Queue-Aware Playback State (`PcmAudioPlayer.IsPlaying`)**:
   - OpenAI sends `response.output_audio.done` as soon as the last audio packet finishes *downloading*, which is 1–4 seconds before `PcmAudioPlayer` finishes playing queued clips.
   - `audioSource.isPlaying` briefly turns `false` on the 1-frame boundary between two queued `AudioClip`s.
   - Therefore, `PcmAudioPlayer.IsPlaying` MUST return `(audioSource != null && audioSource.isPlaying) || _clipQueue.Count > 0` so downstream echo gates never open between clips.
2. **Native `AudioClip` Memory Cleanup**:
   - `PcmAudioPlayer.Update()` must call `Destroy(_activeClip)` once a dynamically created `AudioClip.Create(...)` finishes playing, and `StopImmediately()` must `Destroy()` all queued clips before clearing `_clipQueue`.
3. **No Per-Delta `onUserTranscript` or Per-Item `Debug.Log` Spam**:
   - `HandleInputTranscriptDelta` in `RealtimeConversationManager.cs` is intentionally a no-op; only `HandleInputTranscriptCompleted` invokes `onUserTranscript` once per finished user utterance (prevents triggering RAG proactive search on every syllable).
   - `conversation.item.added` and `conversation.item.done` route to `HandleNoop()`. Never place `Debug.Log` inside high-frequency WebSocket message handlers.
4. **Guarded `response.cancel`**:
   - `TryCancelAssistantResponse()` only sends `{"type":"response.cancel"}` when `_responseActive == true`, and `HandleError` suppresses benign `response_cancel_not_active` and `input_audio_buffer_commit_empty` messages.
5. **Voice Output Speed (`1.15x`)**:
   - `RealtimeConversationManager.voiceSpeed` defaults to `1.15f` (`[Range(0.5f, 1.5f)]`) and is sent in `session.audio.output.speed` inside `SendSessionUpdate()`.

---

## 4. Where to Butt In (Extension Points for Agents)

- **Registering a New Async C# Function Tool at Runtime**:
  - Call `AgentToolRegistry.Register(new AgentTool { Name = "my_tool", Description = "...", ParametersSchema = JObject.Parse(...), ExecuteAsync = async args => { ... } })` before or during a session, then call `RealtimeConversationManager.SendSessionUpdate()` if the socket is already open.
- **Handling Custom Relay WebSocket Events**:
  - Add a new `case "your.custom.event":` inside `RealtimeConversationManager.OnSocketMessage(byte[] bytes)` (around lines 450–480 where `arture.knowledge` and `arture.worker` are handled).
- **Adjusting Voice Speed or Turn Detection**:
  - Modify `voiceSpeed`, `vadThreshold`, or `silenceDurationMs` on `RealtimeConversationManager` and call `SendSessionUpdate()`.


### Conversation and walkthrough behavior

RealtimeConversationManager publishes assistant item-keyed preview deltas and final assistant message content for ConvoAIDebugOverlay history. Agent speaking is an audio status only. Typed submissions and completed voice transcripts publish onUserTranscript once; input deltas do not. Direct session instructions route walkthroughs to the tour tools and require explicit source/absence reporting.
