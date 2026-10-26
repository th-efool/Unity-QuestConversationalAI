# OpenAI Real-time Conversational AI - Setup

Unity package for integrating OpenAI's real-time conversation API into your Unity projects.

## Installation

### Prerequisites

Before installing this package, add **NativeWebSocket**:

1. Open Unity Package Manager (Window → Package Manager)
2. Click **+** → **Add package from git URL**
3. Enter: `https://github.com/endel/NativeWebSocket.git#upm`
4. Click **Add**

### Install Package

1. Open Unity Package Manager (Window → Package Manager)
2. Click **+** → **Add package from git URL**
3. Enter: `https://github.com/danieloquelis/Unity-QuestConversationalAI.git?path=/com.convai.openai`
4. Click **Add**

## Setup

### 1. Get OpenAI API Key

1. Create an account at [OpenAI Platform](https://platform.openai.com)
2. Navigate to [API Keys](https://platform.openai.com/api-keys)
3. Generate a new API key

### 2. Configure Unity

1. In Unity, go to `Assets/Resources/`
2. Right-click → Create → OpenAI → Config
3. Paste your API key in the configuration asset
4. Save the configuration

### 3. (Optional) Configure Vector Database (RAG)

To give your AI agent access to custom documentation, manuals, lore, or internal databases:

1. In Unity, go to `Assets/Resources/`
2. Right-click -> **Create -> OpenAI -> VectorDB Config**
3. Select your target vector database backend:
   - **LocalBakedAsset**: 100% offline, zero network dependencies, runs directly on Meta Quest / Android.
   - **SupabasePgVector**: PostgreSQL with `pgvector` extension via PostgREST.
   - **Pinecone**: Serverless vector index (supports both vector and 2026-07 document schemas).
   - **Qdrant**: Cloud or self-hosted vector database via REST.
4. Paste your database URI and API Key (or leave as `LocalBakedAsset` for instant zero-configuration testing).
5. Open **Window -> OpenAI -> RAG Knowledge Base** to drag-and-drop documents (`.pdf`, `.docx`, `.md`, `.txt`, `.json`, `.csv`) and click **Ingest**.
6. Attach the `RagConversationBridge` component to your `RealtimeConversationManager` GameObject.

> [!NOTE]
> For in-depth architecture, backend setup guides (including complete Supabase SQL scripts), ingestion pipelines, and C# search API examples, see the [Complete RAG & Vector Knowledge Base Guide](rag.md).

## Core Prefabs

### RealtimeConversationManager

The main prefab for handling OpenAI real-time conversations.

**Setup:**

1. Drag `RealtimeConversationManager` prefab into your scene
2. Assign your OpenAI config asset to the `config` field
3. Configure the `systemPrompt` for AI behavior
4. Run the scene and start speaking

**Key Properties:**
| Property | Type | Description |
| -------------- | -------------- | -------------------------------- |
| `config` | `OpenAIConfig` | Configuration asset with API key |
| `systemPrompt` | `string` | AI behavior instructions |
| `autoStart` | `bool` | Start conversation automatically |

### AgentToolsBinding

Prefab for managing custom tools that the AI can invoke during conversations.

**Setup:**

1. Drag `AgentToolsBinding` prefab into your scene
2. Assign your tool scripts to the binding component
3. Configure tool schema JSON files

### RagConversationBridge

Component for connecting OpenAI Realtime conversations with vector databases for RAG knowledge retrieval.

**Setup:**

1. Add `RagConversationBridge` component to your `RealtimeConversationManager` GameObject
2. Assign your `RagConfig` asset to `Rag Config`
3. Choose retrieval mode: `AgenticToolCall` (model autonomously searches) or `ProactiveContextInjection` (auto-searches after user speaks)

See the [Complete RAG & Vector Knowledge Base Guide](rag.md) for architecture, supported backends, and C# search API examples.

## Custom Tools

Custom tools let the AI perform actions in your scene. See the [Complete Tools Guide](tools.md) for detailed examples.

### Quick Setup

**1. Create a C# script with tool methods:**
```csharp
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class MyTools : MonoBehaviour
{
    // Method signature: public async Task<JObject> ToolName(JObject args)
    public async Task<JObject> Light_TurnOn(JObject args)
    {
        GetComponent<Light>().enabled = true;
        await Task.Yield();
        return new JObject { ["ok"] = true };
    }
}
```

**2. Create a JSON schema:**
```json
{
  "tools": [{
    "type": "function",
    "name": "Light_TurnOn",
    "description": "Turn on the light",
    "parameters": { "type": "object", "properties": {} }
  }]
}
```

**3. Wire up in Unity:**
- Attach `MyTools` script to a GameObject
- In `ToolBindings` component:
  - Set `Tools Json` → your JSON file
  - Set `Target` → GameObject with your script

**See [tools.md](tools.md) for complete guide with patterns and troubleshooting.**

## Events

### RealtimeConversationManager Events

- `onAgentTranscript`: Fired when AI speaks (provides transcript text)
- `onUserTranscript`: Fired when user speaks (provides transcript text)
- `onUserSpeaking`: Fired when user speaking state changes (bool)
- `onAgentSpeaking`: Fired when AI speaking state changes (bool)
