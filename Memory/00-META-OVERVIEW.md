# 00 — Package Overview & Architecture Map (`ConversationalAIOpenSource`)

> **MANDATORY FIRST STEP FOR ALL AI AGENTS**: Read this file FIRST before exploring or modifying code in this repository. It explains the standalone architecture of this repository, its modular `./Memory/` chunks, and the **mandatory rule to update `./Memory/0X-*.md` whenever you make changes**.

---

## 1. Repository Identity & Purpose

- **Repository Name**: `Unity-QuestConversationalAI` (`ConversationalAIOpenSource`)
- **Git Remotes**:
  - `origin`: `https://github.com/th-efool/Unity-QuestConversationalAI.git` (Active Branch: `feat/native-rag-knowledge-base`)
  - `upstream`: `https://github.com/danieloquelis/Unity-QuestConversationalAI.git`
- **Role**: Standalone Unity Package Manager (UPM) distribution repository containing:
  1. **`com.convai.openai/`**: Real-Time OpenAI Voice (`gpt-realtime-2.1-mini` / `gpt-realtime`), Function Calling (`AgentToolRegistry`), Direct OpenAI & Relay WebSocket transport (`RealtimeConversationManager`), 24kHz PCM16 streaming playback (`PcmAudioPlayer`), and a zero-dependency Multi-Backend Native C# RAG Engine (`Qdrant`, `Pinecone`, `Supabase pgvector`, `LocalAsset`) with a `<0.5 ms` BM25 + `<2 ms` cosine similarity local in-memory/disk cache (`RagLocalCache` + `RagLocalIndex`).
  2. **`com.convai.elevenlabs/`**: ElevenLabs Conversational AI UPM package.
  3. **`Assets/` & `Docs/`**: Sample scenes for Meta Quest / PC and package documentation.

> **Anti-Staleness & Boundary Rule**:
> - This repository is **100% self-contained**. Do **not** document internal scripts, scenes, or routes of external consumer projects inside `./Memory/`, because external projects evolve independently and would leave such references outdated.
> - If a sibling Unity project (such as `../Arture-AR-6000.4.0f1/Packages/com.convai.openai/`) is present on the same machine and embeds `com.convai.openai/`, keep `./com.convai.openai/` and that embedded package in sync when editing package files.

---

## 2. Repository Directory Layout

```text
ConversationalAIOpenSource/
├── AGENTS.md                          # AI Agent entrypoint
├── CLAUDE.md                          # Claude Code entrypoint
├── Memory/                            # <-- SELF-CONTAINED AGENT MEMORY
│   ├── 00-META-OVERVIEW.md
│   ├── 01-REALTIME-VOICE-AND-AUDIO-PIPELINE.md
│   ├── 02-RAG-VECTOR-ENGINE-AND-PROVIDERS.md
│   └── 03-EDITOR-INGESTION-AND-ELEVENLABS.md
├── com.convai.openai/                 # Core OpenAI Realtime + RAG UPM Package
│   ├── package.json
│   ├── Documentation~/                (README.md, rag.md)
│   ├── Editor/
│   │   ├── com.convai.openai.editor.asmdef
│   │   ├── RealtimeConversationManagerEditor.cs
│   │   └── RAG/                       (DocumentTextExtractor.cs, RagConfigEditor.cs,
│   │                                   RagIngestionPipeline.cs, RagKnowledgeBaseWindow.cs,
│   │                                   RecursiveTextSplitter.cs)
│   └── Runtime/
│       ├── com.convai.openai.asmdef
│       ├── Common/                    (ImageEncodingUtil.cs, MicrophoneStreamer.cs, PcmAudioPlayer.cs)
│       ├── RAG/
│       │   ├── Core/                  (IVectorStoreProvider.cs, OpenAIEmbeddingClient.cs, VectorMath.cs)
│       │   ├── Interfaces/            (IBulkExportProvider.cs)
│       │   ├── Models/                (LocalVectorDatabaseSO.cs, RagConfig.cs, RagModels.cs)
│       │   ├── Providers/             (LocalAssetVectorProvider.cs, PineconeVectorProvider.cs,
│       │   │                           QdrantVectorProvider.cs, RagLocalIndex.cs,
│       │   │                           SupabaseVectorProvider.cs, VectorProviderFactory.cs)
│       │   └── RuntimeBridge/         (RagConversationBridge.cs, RagLocalCache.cs)
│       └── Scripts/                   (AgentToolRegistry.cs, ArtureRelaySettings.cs, OpenAIConfig.cs,
│                                       OpenAIWebSocketEvents.cs, RealtimeConversationManager.cs,
│                                       RealtimeTextChatProp.cs, ToolBindings.cs, ToolCallState.cs)
├── com.convai.elevenlabs/             # ElevenLabs Conversational AI UPM Package
├── Docs/                              # User-facing Markdown guides (OpenAI/ & ElevenLabs/)
├── Assets/                            # Sample scenes & demo scripts
└── MeshTool/                          # Mesh utility scripts
```

---

## 3. Directory of `./Memory/` Chunks

Always read the specific `./Memory/0X-*.md` file matching your task before editing code:

| Memory File | Scope & Systems Covered | When to Read & Update |
| :--- | :--- | :--- |
| **[`00-META-OVERVIEW.md`](./00-META-OVERVIEW.md)** | Repository structure, package boundaries, memory index, maintenance rules. | Read on **every** session. Update if package structure or repo layout changes. |
| **[`01-REALTIME-VOICE-AND-AUDIO-PIPELINE.md`](./01-REALTIME-VOICE-AND-AUDIO-PIPELINE.md)** | `com.convai.openai/Runtime/Scripts/**` and `Runtime/Common/**` (`RealtimeConversationManager`, `PcmAudioPlayer`, `MicrophoneStreamer`, `AgentToolRegistry`, `OpenAIConfig`, `ArtureRelaySettings`). | Read/update when touching WebSocket events, voice speed (`1.15x`), audio playback, tool registration, or Direct vs. Relay connection logic. |
| **[`02-RAG-VECTOR-ENGINE-AND-PROVIDERS.md`](./02-RAG-VECTOR-ENGINE-AND-PROVIDERS.md)** | `com.convai.openai/Runtime/RAG/**` (`RagConfig`, `RagConversationBridge`, `RagLocalCache`, `RagLocalIndex`, `IBulkExportProvider`, `Qdrant`, `Pinecone`, `Supabase`, `LocalAsset`). | Read/update when touching vector DB providers, embeddings, local BM25/cosine cache, or runtime RAG tool calls. |
| **[`03-EDITOR-INGESTION-AND-ELEVENLABS.md`](./03-EDITOR-INGESTION-AND-ELEVENLABS.md)** | `com.convai.openai/Editor/**` (`RagKnowledgeBaseWindow`, `RagIngestionPipeline`, `DocumentTextExtractor`, `RecursiveTextSplitter`), `com.convai.elevenlabs/`, and `Docs/`. | Read/update when touching Unity Editor windows, document parsing/chunking, Inspector editors, or ElevenLabs integration. |

---

## 4. Mandatory Agent Rules & Memory Maintenance Protocol

1. **Update `./Memory/` Whenever You Change Code**:
   - Whenever you add, rename, or remove a class, change runtime behavior, add a vector provider, or alter configuration in this repository, **you MUST update the corresponding `./Memory/0X-*.md` chunk** before finishing your task.
2. **Path Structure Rule**:
   - In this repository, the OpenAI package lives at the root `./com.convai.openai/` (NOT under `./Packages/com.convai.openai/`). Never create a duplicate `Packages/com.convai.openai/` folder inside this repo.
3. **Preserve Hard Audio & Performance Invariants**:
   - Keep `PcmAudioPlayer.IsPlaying` true across queued clips (`audioSource.isPlaying || _clipQueue.Count > 0`) so echo gates never open on the 1-frame gap between clips.
   - Never fire `onUserTranscript` on partial `conversation.item.input_audio_transcription.delta` events—only fire on `.completed`.
   - Never mutate `LocalVectorDatabaseSO` embedding arrays in place—always `.Clone()` before passing to `RagLocalIndex` (which L2-normalizes vectors in place).
   - Never add per-chunk or per-item `Debug.Log` calls on the WebSocket receive/send hot path.
