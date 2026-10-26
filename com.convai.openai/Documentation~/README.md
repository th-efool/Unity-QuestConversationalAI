# Conversational AI for OpenAI (`com.convai.openai`)

A high-performance Unity package for integrating **OpenAI Realtime API** speech-to-speech conversations with native **Retrieval-Augmented Generation (RAG)** vector database capabilities.

Designed specifically for **Meta Quest (VR/MR)**, mobile AR, and PC/Standalone Unity projects.

---

## Table of Contents

1. [Features](#features)
2. [Prerequisites & Installation](#prerequisites--installation)
3. [Quick Start](#quick-start)
4. [RAG & Vector Knowledge Base](#rag--vector-knowledge-base)
   - [Overview & Architecture](#overview--architecture)
   - [Retrieval Modes](#retrieval-modes)
   - [Supported Vector Backends](#supported-vector-backends)
     - [1. Local Baked Asset (100% Offline, Quest Ready)](#1-local-baked-asset)
     - [2. Supabase pgvector](#2-supabase-pgvector)
     - [3. Pinecone Serverless](#3-pinecone-serverless)
     - [4. Qdrant (Cloud & Self-Hosted)](#4-qdrant)
   - [Knowledge Base Manager Editor Window](#knowledge-base-manager-editor-window)
   - [Runtime Integration (RagConversationBridge)](#runtime-integration)
   - [Direct C# Search API for AR/VR Gameplay](#direct-c-search-api-for-arvr-gameplay)
5. [Custom Tools & Function Calling](#custom-tools--function-calling)
6. [Component Reference](#component-reference)
7. [Meta Quest & XR Optimization](#meta-quest--xr-optimization)

---

## Features

- **True Speech-to-Speech Streaming**: Sub-second latency conversational AI using WebSocket streaming directly with OpenAI's Realtime models (`gpt-realtime`, `gpt-realtime-mini`, and preview models).
- **Native RAG Subsystem**:
  - Connect your AI to equipment manuals, game lore, technical documentation, or real-world databases.
  - Multi-backend architecture: **Local Offline ScriptableObject**, **Supabase pgvector**, **Pinecone Serverless**, and **Qdrant**.
  - Dual delivery modes: **Agentic Tool Calling** (AI searches autonomously) or **Proactive Context Injection** (auto-search on user speech).
- **Knowledge Base Manager**:
  - Drag-and-drop document ingestion in Unity Editor (`Window > OpenAI > RAG Knowledge Base`).
  - Native zero-dependency text extraction for `.pdf`, `.docx`, `.md`, `.txt`, `.json`, `.csv`, `.xml`, `.html`, `.cs`, `.yaml`.
  - In-Editor live query tester with latency diagnostics and similarity score inspectability.
- **Agentic Function Calling**: Register custom C# game tools that the AI model can autonomously invoke during dialogue.
- **Quest-Optimized Audio Pipeline**: High-performance PCM 24kHz microphone recording, server VAD (Voice Activity Detection), and audio streaming.

---

## Prerequisites & Installation

### 1. Install NativeWebSocket
This package requires `NativeWebSocket` for cross-platform WebSocket support (Windows, macOS, Linux, Android/Meta Quest, WebGL):

1. In Unity, open **Window → Package Manager**.
2. Click **+ → Add package from git URL...**.
3. Enter: `https://github.com/endel/NativeWebSocket.git#upm`
4. Click **Add**.

### 2. Install com.convai.openai
1. In Package Manager, click **+ → Add package from git URL...**.
2. Enter:
   ```
   https://github.com/danieloquelis/Unity-QuestConversationalAI.git?path=/com.convai.openai
   ```
3. Click **Add**.

---

## Quick Start

### 1. Create OpenAI Configuration
1. In the Project window, navigate to `Assets/Resources/`.
2. Right-click → **Create → OpenAI → Config**.
3. Enter your OpenAI API Key obtained from [platform.openai.com/api-keys](https://platform.openai.com/api-keys).

### 2. Add Prefab to Scene
1. Drag the `RealtimeConversationManager` prefab into your scene (found in `Packages/ConversationalAI [OpenAI]/Runtime/Prefabs/`).
2. Assign your `OpenAIConfig` asset to the **Config** field in the Inspector.
3. Configure the **System Prompt** with your character or assistant instructions.
4. Press **Play** and speak into your microphone!

---

## RAG & Vector Knowledge Base

Retrieval-Augmented Generation (RAG) grounds conversational responses in factual source documents, preventing hallucinations and giving agents specialized domain expertise.

### Overview & Architecture

```
   ┌──────────────────────┐         ┌─────────────────────────┐
   │ Knowledge Documents  │ ──────> │  Knowledge Base Manager │
   │ (.pdf, .docx, .md,   │         │ (Window > OpenAI > RAG) │
   │  .txt, .json, .csv)  │         └────────────┬────────────┘
   └──────────────────────┘                      │
                                                 ▼ Ingestion (Embeddings API)
                                    ┌─────────────────────────┐
                                    │  Vector Store Provider  │
                                    │  • Local ScriptableObj  │
                                    │  • Supabase pgvector    │
                                    │  • Pinecone Serverless  │
                                    │  • Qdrant (Cloud/Local) │
                                    └────────────┬────────────┘
                                                 │
                                                 ▼ Vector Search
┌─────────────────────────────────┐         ┌─────────────────────────┐
│ RealtimeConversationManager     │ <═════> │  RagConversationBridge  │
│ (OpenAI Realtime WebSocket API) │         │ (Agentic Tool / Inject) │
└─────────────────────────────────┘         └─────────────────────────┘
```

---

### Retrieval Modes

Set in `RagConfig.retrievalMode`:

- **AgenticToolCall** (*Recommended*):
  - Automatically registers `search_knowledge_base` with `AgentToolRegistry`.
  - When the user asks a factual or technical question, the LLM autonomously triggers the tool with a targeted search query.
  - Zero token overhead during casual conversation; vector search runs only when required.
- **ProactiveContextInjection**:
  - Listens to `RealtimeConversationManager.onUserTranscript`.
  - When the user finishes speaking (debounced by ~450ms), the system embeds the transcript, queries the vector store, and pushes matching reference chunks directly into the conversation stream using `SendContextMessage()`.
  - Best for specialized simulation tasks where every utterance must be cross-referenced with documentation.

---

### Supported Vector Backends

#### 1. Local Baked Asset
*100% Offline • Zero Network Setup • Runs directly on Meta Quest & Mobile*

- Chunks and pre-calculated 1536-dimensional embeddings are stored in a `LocalVectorDatabaseSO` ScriptableObject asset.
- Cosine similarity is computed locally in C# using optimized SIMD-friendly arrays.

**Setup**:
1. Right-click in Project → **Create → OpenAI → VectorDB Config**. Name it `RagConfig`.
2. Set **Backend Type** to `LocalBakedAsset`.
3. Right-click → **Create → OpenAI → RAG → Local Vector Database**. Name it `LocalDatabase`.
4. Assign `LocalDatabase` to the **Local Database** field in `RagConfig`.
5. Open **Window → OpenAI → RAG Knowledge Base**, drag your files into the **Drag & Drop Ingestion** tab, and click **Ingest Staged Documents**.
6. The embeddings are baked into your asset file and ready to ship on Quest!

---

#### 2. Supabase pgvector
*Enterprise PostgreSQL • Relational Joins • Row-Level Security (RLS)*

Communicates via Supabase PostgREST and Remote Procedure Calls (RPC).

**SQL Setup Script** (Run in Supabase Dashboard → SQL Editor):
```sql
-- 1. Enable vector extension
CREATE EXTENSION IF NOT EXISTS vector;

-- 2. Create documents table
CREATE TABLE IF NOT EXISTS documents (
    id TEXT PRIMARY KEY,
    document_id TEXT NOT NULL,
    source_name TEXT NOT NULL,
    chunk_index INTEGER NOT NULL,
    content TEXT NOT NULL,
    metadata JSONB DEFAULT '{}'::jsonb,
    embedding VECTOR(1536) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT TIMEZONE('utc'::text, NOW()) NOT NULL
);

-- 3. Create HNSW index for cosine similarity
CREATE INDEX IF NOT EXISTS documents_embedding_hnsw_idx
ON documents USING hnsw (embedding vector_cosine_ops);

-- 4. Enable Row Level Security (RLS)
ALTER TABLE documents ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Allow public read access" ON documents
FOR SELECT TO anon, authenticated USING (true);

CREATE POLICY "Allow service_role full access" ON documents
FOR ALL TO service_role USING (true);

-- 5. Stored function for similarity search
CREATE OR REPLACE FUNCTION match_documents (
    query_embedding VECTOR(1536),
    match_threshold FLOAT DEFAULT 0.35,
    match_count INT DEFAULT 5
)
RETURNS TABLE (
    id TEXT,
    document_id TEXT,
    source_name TEXT,
    chunk_index INT,
    content TEXT,
    metadata JSONB,
    similarity FLOAT
)
LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN QUERY
    SELECT
        documents.id,
        documents.document_id,
        documents.source_name,
        documents.chunk_index,
        documents.content,
        documents.metadata,
        1 - (documents.embedding <=> query_embedding) AS similarity
    FROM documents
    WHERE 1 - (documents.embedding <=> query_embedding) >= match_threshold
    ORDER BY similarity DESC
    LIMIT match_count;
END;
$$;
```

**Unity Configuration**:
- **Database Uri**: Your Supabase project URL (e.g. `https://your-ref.supabase.co`).
- **Collection Or Table Name**: `documents`.
- **Supabase Rpc Function Name**: `match_documents`.
- **Runtime Api Key**: Your `anon public` key.
- (In Editor Window) **Admin / Write Key**: Your `service_role secret` key (used only during ingestion in Editor).

---

#### 3. Pinecone Serverless
*Managed Serverless Index • High Scalability • Dual Vector & Document Schema*

**Setup**:
1. In Pinecone Console, create a Serverless index with **Dimensions**: `1536` and **Metric**: `cosine`.
2. Copy the Index Host URL (e.g. `https://my-index-xyz.svc.pinecone.io`).
3. In `RagConfig`:
   - Set **Backend Type** to `Pinecone`.
   - Paste Host URL into **Database Uri**.
   - Paste Pinecone API Key into **Runtime Api Key**.
   - (Optional) Set **Collection Or Table Name** to a namespace.

---

#### 4. Qdrant
*Cloud & Self-Hosted • Open Source • Automatic Collection Provisioning*

**Self-Hosted Setup (Docker)**:
```bash
docker run -p 6333:6333 -v $(pwd)/qdrant_storage:/qdrant/storage:z qdrant/qdrant
```

**Unity Configuration**:
- Set **Database Uri** to `http://localhost:6333` or your Qdrant Cloud URL.
- Set **Collection Or Table Name** to your desired collection (e.g. `knowledge`).
- The provider will automatically create the collection with 1536 dimensions and Cosine distance on first connection test or upsert!

---

### Knowledge Base Manager Editor Window

Access via **Window → OpenAI → RAG Knowledge Base**.

```
┌────────────────────────────────────────────────────────┐
│ [Window > OpenAI > RAG Knowledge Base]                 │
├────────────────────────────────────────────────────────┤
│ Active Config: [VectorDBConfig (RagConfig)       [O] ] │
├────────────────────────────────────────────────────────┤
│ [ 1. Connection & Setup ] [ 2. Ingestion ] [ 3. Tester]│
└────────────────────────────────────────────────────────┘
```

- **Tab 1: Connection & Setup**: Test backend connectivity, view roundtrip latency, copy custom Supabase SQL, or clear database chunks.
- **Tab 2: Drag & Drop Ingestion**:
  - Drag files (`.pdf`, `.docx`, `.md`, `.txt`, `.json`, `.csv`, `.xml`, `.html`, `.cs`, `.yaml`) or entire folders.
  - Native zero-dependency document text extraction.
  - Configurable **Chunk Size** (default `600`) and **Chunk Overlap** (default `100`).
  - Batch embedding generation via OpenAI `text-embedding-3-small` (50 chunks/batch).
- **Tab 3: Live Query Tester**:
  - Test semantic search directly in the Editor **without entering Play Mode**.
  - Inspect latency (ms), similarity scores, chunk text, and source document metadata.
  - One-click copy formatted LLM context to clipboard.

---

### Runtime Integration

1. Add `RagConversationBridge` component to your `RealtimeConversationManager` GameObject:
   ```
   [Inspector]
   Add Component -> OpenAI -> RAG -> Rag Conversation Bridge
   ```
2. Assign your `RagConfig` asset.
3. Configure events if desired (`onRagQueryStarted`, `onRagContextRetrieved`, `onRagResultsReceived`).

---

### Direct C# Search API for AR/VR Gameplay

Execute vector similarity queries directly from your AR/VR gameplay scripts:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using OpenAI.RAG;
using OpenAI;

public class VRObjectInspector : MonoBehaviour
{
    [SerializeField] private RagConversationBridge ragBridge;
    [SerializeField] private RealtimeConversationManager conversationManager;

    public async Task InspectPart(string partName)
    {
        // 1. Semantic search in knowledge base
        List<RagSearchResult> results = await ragBridge.SearchAsync($"Operating specifications for {partName}");

        if (results.Count > 0)
        {
            // 2. Format context with source citations
            string formattedContext = RagConversationBridge.FormatContext(results);

            // 3. Send into active conversation session
            conversationManager.SendContextMessage(
                $"[Player is inspecting {partName}. Reference manual:\n{formattedContext}]"
            );
        }
    }
}
```

---

## Custom Tools & Function Calling

The AI can perform actions in your scene by calling custom C# methods registered via `AgentToolRegistry`.

### 1. Implement Tool Method
```csharp
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class ShipControls : MonoBehaviour
{
    public async Task<JObject> SetThrusterPower(JObject args)
    {
        int level = args.Value<int>("level");
        Debug.Log($"Setting thruster power to: {level}");
        await Task.Yield();
        return new JObject { ["ok"] = true, ["current_level"] = level };
    }
}
```

### 2. Define JSON Schema
```json
{
  "tools": [{
    "type": "function",
    "name": "SetThrusterPower",
    "description": "Adjusts the ship thruster power level between 0 and 100",
    "parameters": {
      "type": "object",
      "properties": {
        "level": { "type": "integer", "description": "Power level 0-100" }
      },
      "required": ["level"]
    }
  }]
}
```

### 3. Bind in Scene
Attach `AgentToolsBinding` component, assign your JSON schema file, and link your GameObject target.

---

## Component Reference

| Component / Asset | Description |
| :--- | :--- |
| `RealtimeConversationManager` | Core manager handling microphone streaming, WebSocket connection to OpenAI Realtime API, audio playback, and transcripts. |
| `RagConfig` | ScriptableObject defining embeddings settings, backend type, database credentials, similarity thresholds, and retrieval modes. |
| `RagConversationBridge` | Connects Realtime conversation to vector search (registers agentic tool or listens to speech transcripts for proactive injection). |
| `LocalVectorDatabaseSO` | ScriptableObject holding baked document chunks and embedding vectors for 100% offline local cosine search. |
| `AgentToolRegistry` | Static registry for registering and routing function tools invoked by the OpenAI Realtime model. |
| `AgentToolsBinding` | Prefab and component for binding MonoBehaviour tool methods to JSON function schemas. |

---

## Meta Quest & XR Optimization

- **Zero-Network Offline Mode**: Use `LocalBakedAsset` for Quest standalone builds. Embeddings and text are packaged directly into the APK, ensuring instant search with zero network hops.
- **Microphone Permissions**: Ensure `android.permission.RECORD_AUDIO` is included in your `AndroidManifest.xml`.
- **Sample Rate**: The package automatically resamples audio to 24kHz PCM for optimal OpenAI Realtime processing.
- **Keep Context Concise**: Set `topK = 2` or `3` and `chunkSize = 500-600` in `RagConfig` to minimize token latency during real-time voice streaming.

---

*Licensed under the MIT License.*
