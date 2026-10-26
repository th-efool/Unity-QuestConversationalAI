# Changelog

## 1.0.0 - Initial release

## 1.1.0 - 2025.11.13

- Adding assembly definitions for open ai library.
- Fixing samples for open ai conv ai
- Fixing tools sample

## 1.2.0 - 2026.10.26

- Support OpenAI Realtime GA schema (`gpt-realtime`, `gpt-realtime-mini`):
  - Added required `session.type = "realtime"` parameter.
  - Omitted legacy top-level `modalities` and `voice` fields on GA sessions.
  - Replaced flat audio properties with structured `session.audio.input` (PCM 24kHz, server VAD, whisper-1) and `session.audio.output` (PCM 24kHz, voice).
- Added backward-compatible dual schema support for legacy beta/preview models (`gpt-4o-realtime-preview`).
- Conditionally apply `OpenAI-Beta: realtime=v1` handshake header only for preview models.
- Added event listeners for GA server events: `response.output_audio.delta`, `response.output_audio.done`, `response.output_audio_transcript.delta`, `response.output_audio_transcript.done`, and `conversation.item.added`.

## 1.3.0 - 2026.10.26

- Native RAG (Retrieval-Augmented Generation) Architecture:
  - Vector database provider abstraction (`IVectorStoreProvider`) supporting:
    - **Local Baked Asset**: In-memory cosine search via `LocalVectorDatabaseSO` ScriptableObject (zero external network calls, runs offline on Quest/Android).
    - **Supabase pgvector**: PostgREST upsert and `match_documents` RPC vector similarity search.
    - **Pinecone Serverless**: Dual support for standard vector endpoints and 2026-07 Document-Schema endpoints.
    - **Qdrant Cloud & Self-Hosted**: REST API with automatic collection creation and vector payload search.
- Unity Editor Knowledge Base Tooling:
  - Dedicated Drag & Drop Ingestion Window (`Window > OpenAI > RAG Knowledge Base`).
  - Native document parsing for `.txt`, `.md`, `.json`, `.csv`, `.docx`, and `.pdf` without third-party binary dependencies.
  - Hierarchical `RecursiveTextSplitter` with configurable chunk size and overlap.
  - Batch embedding generation via OpenAI `text-embedding-3-small`.
  - In-Editor live query tester with latency diagnostics and similarity score inspectability.
  - 1-click PostgreSQL / pgvector SQL setup generator for Supabase.
- Realtime Conversation Integration:
  - `RagConversationBridge` component supporting Agentic Tool Calling (registered into `AgentToolRegistry`) and Proactive Context Injection.
  - Added `SendContextMessage` to `RealtimeConversationManager`.
