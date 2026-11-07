# AI Agent Operating Manual (`AGENTS.md` — `ConversationalAIOpenSource`)

> **MANDATORY FIRST STEP FOR ALL AI AGENTS:**
> Before exploring files, running searches, or writing code in this repository, **read [`Memory/00-META-OVERVIEW.md`](./Memory/00-META-OVERVIEW.md)** first, then read the specific `Memory/0X-*.md` chunk(s) relevant to your task.

---

## 1. Self-Contained Modular Agent Memory (`./Memory/`)

All architectural knowledge for this repository (`Unity-QuestConversationalAI` / `com.convai.openai` & `com.convai.elevenlabs`) is stored locally in [`./Memory/`](./Memory/):

| Memory Chunk | Scope & When to Read / Update |
| :--- | :--- |
| [`Memory/00-META-OVERVIEW.md`](./Memory/00-META-OVERVIEW.md) | **Read First Always.** Repository layout (`com.convai.openai/`, `com.convai.elevenlabs/`, `Docs/`, `Assets/`), hard invariants, anti-staleness boundary rules, and the Agent Memory Maintenance Protocol. |
| [`Memory/01-REALTIME-VOICE-AND-AUDIO-PIPELINE.md`](./Memory/01-REALTIME-VOICE-AND-AUDIO-PIPELINE.md) | `com.convai.openai/Runtime/Scripts/**` & `Runtime/Common/**`: `RealtimeConversationManager`, `PcmAudioPlayer`, `MicrophoneStreamer`, `AgentToolRegistry`, `OpenAIConfig`, `ArtureRelaySettings`, 24kHz PCM16 streaming, queue-aware `IsPlaying`, and `1.15x` voice speed. |
| [`Memory/02-RAG-VECTOR-ENGINE-AND-PROVIDERS.md`](./Memory/02-RAG-VECTOR-ENGINE-AND-PROVIDERS.md) | `com.convai.openai/Runtime/RAG/**`: `RagConfig`, `RagConversationBridge`, `RagLocalCache`, `RagLocalIndex` (`<0.5 ms` BM25 + `<2 ms` cosine similarity), `IBulkExportProvider`, and the 4 vector providers (`Qdrant`, `Pinecone`, `Supabase`, `LocalAsset`). |
| [`Memory/03-EDITOR-INGESTION-AND-ELEVENLABS.md`](./Memory/03-EDITOR-INGESTION-AND-ELEVENLABS.md) | `com.convai.openai/Editor/**` (`RagKnowledgeBaseWindow`, `RagIngestionPipeline`, `DocumentTextExtractor`, `RecursiveTextSplitter`), `com.convai.elevenlabs/`, and `Docs/`. |

---

## 2. Mandatory Memory Maintenance Rule (Update When You Change Code)

Whenever you make **any architectural, behavioral, schema, tool, provider, or file-level change** in this repository, you **MUST update the corresponding `./Memory/0X-*.md` file(s)** before finishing your task:

1. **Added, renamed, or deleted a script or Editor window?**
   - Update the **"Key Files & Exact Responsibilities"** table in the matching `./Memory/0X-*.md` chunk.
2. **Changed system behavior, thresholds, protocol events, or invariants?**
   - Update the **"Architectural Overview"** and **"Hard Invariants"** sections in the matching `./Memory/0X-*.md` chunk.
3. **Added a new `AgentTool`, vector store provider, or extension hook?**
   - Document it in the **"Where to Butt In (Extension Points)"** section of the matching `./Memory/0X-*.md` chunk.
4. **Anti-Staleness Rule**:
   - Do **not** add internal file lists or routes of external projects into `./Memory/` so that independent changes in other repositories never leave this repository's memory outdated.
