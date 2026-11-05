# 03 — Editor Ingestion Pipeline, Custom Inspectors & ElevenLabs Package

> **Scope**: `com.convai.openai/Editor/**`, `com.convai.elevenlabs/**`, `Docs/**`, `Assets/**`.
> **Update Rule**: If you modify the RAG Editor window, document extraction/chunking, custom Inspectors, sample scenes, or the ElevenLabs package, update this file before completing your turn.

---

## 1. RAG Editor Ingestion Pipeline (`com.convai.openai/Editor/RAG/`)

The package includes a zero-dependency Unity Editor ingestion suite accessible via **`Tools > OpenAI > RAG Knowledge Base`**:

```text
User selects files / folder in RagKnowledgeBaseWindow
   │
   ├──► 1. DocumentTextExtractor.ExtractText(filePath)
   │       ├── .txt, .md, .csv, .json -> UTF-8 text read
   │       ├── .docx -> ZIP archive stream -> parses word/document.xml (<w:t>, <w:p>, <w:tab>)
   │       └── .pdf  -> FlateDecode stream decompression + PDF text operator parser (Tj, TJ, ', ")
   │
   ├──► 2. RecursiveTextSplitter.SplitText(text, chunkSizeTokens, chunkOverlapTokens)
   │       └── Splits along semantic boundaries ("\n\n", "\n", ". ", " ") while respecting token budget
   │
   ├──► 3. RagIngestionPipeline.IngestFilesAsync(...)
   │       ├── Batches chunks into OpenAIEmbeddingClient.GetEmbeddingsBatchAsync() (1536d vectors)
   │       └── Calls provider.UpsertChunksAsync(chunks) -> Qdrant / Pinecone / Supabase / LocalVectorDatabaseSO
   │
   └──► 4. Live Query & Connection Test Tab
           └── Tests provider connectivity and previews top-K cosine similarity scores inside the Editor.
```

---

## 2. Key Files & Exact Responsibilities

| File Path | Class | Exact Responsibility |
| :--- | :--- | :--- |
| `com.convai.openai/Editor/RAG/RagKnowledgeBaseWindow.cs` | `RagKnowledgeBaseWindow : EditorWindow` | Multi-tab Unity Editor window (`Setup & Connection`, `Ingest Documents`, `Search & Test Playground`) for managing `RagConfig` and vector databases without leaving Unity. |
| `com.convai.openai/Editor/RAG/RagConfigEditor.cs` | `RagConfigEditor : Editor` | Custom Inspector for `RagConfig.asset` that dynamically shows/hides backend-specific credential fields (`Qdrant`, `Pinecone`, `Supabase`, `LocalBakedAsset`) and switches credential slots cleanly. |
| `com.convai.openai/Editor/RAG/RagIngestionPipeline.cs` | `RagIngestionPipeline` | Orchestrates multi-file text extraction, chunking, batched OpenAI embedding calls, and provider upserts with Editor progress bar reporting. |
| `com.convai.openai/Editor/RAG/DocumentTextExtractor.cs` | `DocumentTextExtractor` | Pure C# text extractor for `.pdf`, `.docx`, `.md`, `.txt`, `.csv`, and `.json` files (zero external native DLL dependencies). |
| `com.convai.openai/Editor/RAG/RecursiveTextSplitter.cs` | `RecursiveTextSplitter` | Token-aware recursive text chunker with configurable chunk size and overlap. |
| `com.convai.openai/Editor/RealtimeConversationManagerEditor.cs` | `RealtimeConversationManagerEditor : Editor` | Custom Inspector for `RealtimeConversationManager` providing quick buttons to open `RagConfig` and test sessions. |
| `com.convai.elevenlabs/` | ElevenLabs Package | Alternative Conversational AI UPM package for ElevenLabs WebSocket agents (`Runtime/`, `Samples~/`). |
| `Docs/OpenAI/` | `setup.md`, `tools.md`, `rag.md`, `samples.md` | User-facing Markdown documentation for configuring OpenAI Realtime and RAG in Unity. |

---

## 3. Where to Butt In (Extension Points for Agents)

- **Adding Support for a New Document Format (e.g., `.html`, `.xlsx`, `.pptx`)**:
  1. Add the extension to `DocumentTextExtractor.SupportedExtensions` in `com.convai.openai/Editor/RAG/DocumentTextExtractor.cs`.
  2. Implement the parser method inside `DocumentTextExtractor.ExtractText(string filePath)` using `System.IO.Compression.ZipArchive` (for Office OpenXML formats) or regex/XML parsing.
- **Adding Custom Chunk Metadata**:
  - Populate `DocumentChunk.metadata` (a `Dictionary<string, string>`) in `RagIngestionPipeline.cs` before calling `UpsertChunksAsync`. All 4 providers serialize and return `metadata` during both live queries and `ExportAllAsync`.
