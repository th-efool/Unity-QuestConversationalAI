using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using OpenAI;
using OpenAI.RAG;
using Debug = UnityEngine.Debug;

namespace OpenAI.RAG.Editor
{
    /// <summary>
    /// Interactive Editor Window providing end-to-end management of the RAG Knowledge Base:
    /// - Database Connection & Backend Setup
    /// - Drag & Drop Document Ingestion (PDF, DOCX, TXT, MD, CSV, JSON, XML, HTML, CS)
    /// - Live In-Editor Vector Search Tester (no Play Mode required)
    /// </summary>
    public class RagKnowledgeBaseWindow : EditorWindow
    {
        [SerializeField]
        public RagConfig activeConfig;

        private int _selectedTab = 0;
        private readonly string[] _tabTitles = new[]
        {
            "1. Connection & Setup",
            "2. Drag & Drop Ingestion",
            "3. Live Query Tester"
        };

        // Tab 0: Connection & Setup State
        private Vector2 _setupScroll;
        private bool _isTestingConnection;
        private string _connectionStatusMsg;
        private MessageType _connectionStatusType;

        // Tab 1: Ingestion State
        private Vector2 _ingestionScroll;
        private Vector2 _stagedFilesScroll;
        private readonly List<string> _stagedFiles = new List<string>();
        private bool _isIngesting;

        // Tab 2: Query Tester State
        private Vector2 _testerScroll;
        private Vector2 _resultsScroll;
        private string _searchQuery = "";
        private int _queryTopK = 3;
        private float _queryMinScore = 0.35f;
        private bool _isSearching;
        private long _lastSearchLatencyMs;
        private List<RagSearchResult> _searchResults;
        private string _searchError;

        [MenuItem("Window/OpenAI/RAG Knowledge Base", priority = 2050)]
        public static void ShowWindow()
        {
            var window = GetWindow<RagKnowledgeBaseWindow>("RAG Knowledge Base");
            window.minSize = new Vector2(620, 520);
            window.Show();
        }

        /// <summary>
        /// Opens the RAG Knowledge Base Window with the specified RagConfig asset preselected.
        /// </summary>
        public static void Open(RagConfig config)
        {
            var window = GetWindow<RagKnowledgeBaseWindow>("RAG Knowledge Base");
            window.minSize = new Vector2(620, 520);
            window.activeConfig = config;
            window.Show();
        }

        private void OnEnable()
        {
            // Auto-locate active RagConfig if not assigned
            if (activeConfig == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:RagConfig");
                if (guids.Length > 0)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    activeConfig = AssetDatabase.LoadAssetAtPath<RagConfig>(path);
                }
            }

            if (activeConfig != null)
            {
                _queryTopK = activeConfig.topK;
                _queryMinScore = activeConfig.similarityThreshold;
            }
        }

        private void OnGUI()
        {
            DrawHeader();

            if (activeConfig == null)
            {
                EditorGUILayout.Space(12);
                EditorGUILayout.HelpBox(
                    "Please assign or create a RAG Configuration asset above to manage database connection, ingest documents, or run queries.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(6);
            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabTitles, GUILayout.Height(30));
            EditorGUILayout.Space(8);

            switch (_selectedTab)
            {
                case 0:
                    DrawConnectionAndSetupTab();
                    break;
                case 1:
                    DrawIngestionTab();
                    break;
                case 2:
                    DrawQueryTesterTab();
                    break;
            }
        }

        #region Header & Config Management

        private void DrawHeader()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("OpenAI Native RAG Knowledge Base", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                activeConfig = (RagConfig)EditorGUILayout.ObjectField(
                    "Active RAG Config",
                    activeConfig,
                    typeof(RagConfig),
                    false);

                if (EditorGUI.EndChangeCheck() && activeConfig != null)
                {
                    _queryTopK = activeConfig.topK;
                    _queryMinScore = activeConfig.similarityThreshold;
                    _connectionStatusMsg = null;
                }

                if (GUILayout.Button("New Config", GUILayout.Width(90)))
                {
                    CreateNewRagConfigAsset();
                }
            }
        }

        private void CreateNewRagConfigAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create New RAG Configuration",
                "NewRagConfig",
                "asset",
                "Select a destination folder in your project.");

            if (!string.IsNullOrEmpty(path))
            {
                var newConfig = CreateInstance<RagConfig>();
                AssetDatabase.CreateAsset(newConfig, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                activeConfig = newConfig;
                Selection.activeObject = newConfig;
            }
        }

        #endregion

        #region Tab 1: Connection & Setup

        private void DrawConnectionAndSetupTab()
        {
            _setupScroll = EditorGUILayout.BeginScrollView(_setupScroll);

            // OpenAI Authentication Section
            EditorGUILayout.LabelField("1. OpenAI Authentication & Model Settings", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                activeConfig.openAIConfig = (OpenAIConfig)EditorGUILayout.ObjectField(
                    "OpenAI Config Asset",
                    activeConfig.openAIConfig,
                    typeof(OpenAIConfig),
                    false);

                if (activeConfig.openAIConfig == null)
                {
                    EditorGUILayout.HelpBox(
                        "OpenAIConfig is not assigned. An API key is required for embedding generation.",
                        MessageType.Error);
                }
                else if (string.IsNullOrWhiteSpace(activeConfig.openAIConfig.apiKey))
                {
                    EditorGUILayout.HelpBox(
                        "Referenced OpenAIConfig has an empty API key.",
                        MessageType.Warning);
                }
                else
                {
                    string maskedKey = MaskApiKey(activeConfig.openAIConfig.apiKey);
                    EditorGUILayout.LabelField("OpenAI API Key", maskedKey);
                }

                activeConfig.embeddingEndpoint = EditorGUILayout.TextField("Embedding Endpoint", activeConfig.embeddingEndpoint);
                activeConfig.embeddingModel = EditorGUILayout.TextField("Embedding Model", activeConfig.embeddingModel);
                activeConfig.dimensions = EditorGUILayout.IntField("Vector Dimensions", activeConfig.dimensions);
            }

            EditorGUILayout.Space(10);

            // Vector Backend Configuration Section
            EditorGUILayout.LabelField("2. Vector Database Backend Configuration", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                activeConfig.backendType = (VectorBackendType)EditorGUILayout.EnumPopup("Backend Type", activeConfig.backendType);

                if (activeConfig.backendType == VectorBackendType.LocalBakedAsset)
                {
                    DrawLocalBackendSettings();
                }
                else
                {
                    DrawRemoteBackendSettings();
                }
            }

            EditorGUILayout.Space(10);

            // Retrieval & Tool Settings
            EditorGUILayout.LabelField("3. Runtime Retrieval & Tool Calling Settings", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                activeConfig.retrievalMode = (RagRetrievalMode)EditorGUILayout.EnumPopup("Retrieval Mode", activeConfig.retrievalMode);
                activeConfig.topK = EditorGUILayout.IntSlider("Top K Documents", activeConfig.topK, 1, 20);
                activeConfig.similarityThreshold = EditorGUILayout.Slider("Similarity Threshold", activeConfig.similarityThreshold, 0f, 1f);
                activeConfig.toolName = EditorGUILayout.TextField("Tool Name", activeConfig.toolName);
                activeConfig.toolDescription = EditorGUILayout.TextField("Tool Description", activeConfig.toolDescription);
            }

            if (GUI.changed)
            {
                EditorUtility.SetDirty(activeConfig);
            }

            EditorGUILayout.Space(12);

            // Action Buttons Row
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(_isTestingConnection);
                if (GUILayout.Button(_isTestingConnection ? "Testing Connection..." : "Test Vector DB Connection", GUILayout.Height(30)))
                {
                    string adminKeyPref = $"ConvAI_RAG_AdminKey_{activeConfig.name}";
                    string adminKey = EditorPrefs.GetString(adminKeyPref, null);
                    string effectiveKey = !string.IsNullOrEmpty(adminKey) ? adminKey : activeConfig.runtimeApiKey;
                    RunTestConnectionAsync(effectiveKey);
                }
                EditorGUI.EndDisabledGroup();

                if (activeConfig.backendType == VectorBackendType.SupabasePgVector)
                {
                    if (GUILayout.Button("Copy Supabase SQL Setup", GUILayout.Height(30), GUILayout.Width(190)))
                    {
                        string sql = SupabaseVectorProvider.GetSqlSetupScript(
                            activeConfig.collectionOrTableName,
                            activeConfig.supabaseRpcFunctionName,
                            activeConfig.dimensions);
                        GUIUtility.systemCopyBuffer = sql;
                        EditorUtility.DisplayDialog(
                            "Supabase SQL Copied",
                            $"PostgreSQL pgvector schema SQL has been copied to clipboard for table '{activeConfig.collectionOrTableName}'.\n\nPaste into Supabase SQL Editor and execute.",
                            "OK");
                    }
                }
            }

            if (!string.IsNullOrEmpty(_connectionStatusMsg))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_connectionStatusMsg, _connectionStatusType);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawLocalBackendSettings()
        {
            activeConfig.localDatabase = (LocalVectorDatabaseSO)EditorGUILayout.ObjectField(
                "Local Vector Database",
                activeConfig.localDatabase,
                typeof(LocalVectorDatabaseSO),
                false);

            if (activeConfig.localDatabase == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a LocalVectorDatabaseSO asset or click below to create one.",
                    MessageType.Info);

                if (GUILayout.Button("Create Local Vector Database Asset", GUILayout.Height(24)))
                {
                    CreateLocalDatabaseAsset();
                }
            }
            else
            {
                int chunkCount = activeConfig.localDatabase.chunks != null ? activeConfig.localDatabase.chunks.Count : 0;
                EditorGUILayout.LabelField("Stored Chunks Count", chunkCount.ToString());
                EditorGUILayout.LabelField("Asset Model / Dims", $"{activeConfig.localDatabase.embeddingModel} ({activeConfig.localDatabase.dimensions}d)");
            }
        }

        private void CreateLocalDatabaseAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Local Vector Database Asset",
                "LocalVectorDatabase",
                "asset",
                "Select a destination in Assets/");

            if (!string.IsNullOrEmpty(path))
            {
                var localDb = CreateInstance<LocalVectorDatabaseSO>();
                localDb.embeddingModel = activeConfig.embeddingModel;
                localDb.dimensions = activeConfig.dimensions;
                AssetDatabase.CreateAsset(localDb, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                activeConfig.localDatabase = localDb;
                EditorUtility.SetDirty(activeConfig);
            }
        }

        private void DrawRemoteBackendSettings()
        {
            activeConfig.databaseUri = EditorGUILayout.TextField("Database REST URI", activeConfig.databaseUri);

            string collectionLabel = activeConfig.backendType == VectorBackendType.SupabasePgVector
                ? "Table Name"
                : (activeConfig.backendType == VectorBackendType.Pinecone ? "Index Namespace" : "Collection Name");

            activeConfig.collectionOrTableName = EditorGUILayout.TextField(collectionLabel, activeConfig.collectionOrTableName);

            if (activeConfig.backendType == VectorBackendType.SupabasePgVector)
            {
                activeConfig.supabaseRpcFunctionName = EditorGUILayout.TextField("RPC Match Function Name", activeConfig.supabaseRpcFunctionName);
            }

            activeConfig.runtimeApiKey = EditorGUILayout.PasswordField("Runtime Query API Key (Read-Only)", activeConfig.runtimeApiKey);

            // Editor-Only Admin Key (stored in EditorPrefs)
            string adminKeyPref = $"ConvAI_RAG_AdminKey_{activeConfig.name}";
            string currentAdminKey = EditorPrefs.GetString(adminKeyPref, "");
            EditorGUI.BeginChangeCheck();
            string newAdminKey = EditorGUILayout.PasswordField("Editor Admin Key (Write/Upsert)", currentAdminKey);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(adminKeyPref, newAdminKey);
            }

            EditorGUILayout.HelpBox(
                "Editor Admin Key is saved in local EditorPrefs. It enables document ingestion while keeping production client builds secure and read-only.",
                MessageType.None);
        }

        private async void RunTestConnectionAsync(string effectiveKey)
        {
            _isTestingConnection = true;
            _connectionStatusMsg = $"Connecting to {activeConfig.backendType}...";
            _connectionStatusType = MessageType.Info;
            Repaint();

            try
            {
                var provider = VectorProviderFactory.Create(activeConfig);
                var (ok, message) = await provider.TestConnectionAsync(effectiveKey);

                if (ok)
                {
                    _connectionStatusMsg = $"[Success] {message}";
                    _connectionStatusType = MessageType.Info;
                }
                else
                {
                    _connectionStatusMsg = $"[Failure] {message}";
                    _connectionStatusType = MessageType.Error;
                }
            }
            catch (Exception ex)
            {
                _connectionStatusMsg = $"[Exception] {ex.Message}";
                _connectionStatusType = MessageType.Error;
            }
            finally
            {
                _isTestingConnection = false;
                Repaint();
            }
        }

        #endregion

        #region Tab 2: Drag & Drop Ingestion

        private void DrawIngestionTab()
        {
            _ingestionScroll = EditorGUILayout.BeginScrollView(_ingestionScroll);

            EditorGUILayout.LabelField("Document Ingestion & Chunking Suite", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Drag files or folders into the drop zone below, or stage them manually. Text will be extracted, split into semantically coherent chunks, embedded via OpenAI, and stored into the vector database.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(8);

            // Drag and Drop Zone
            DrawDropZone();

            EditorGUILayout.Space(8);

            // Add File / Folder Buttons Row
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Files...", GUILayout.Height(24)))
                {
                    string path = EditorUtility.OpenFilePanel(
                        "Select Document to Ingest",
                        "",
                        "txt,md,pdf,docx,json,csv,xml,html,yml,cs");
                    if (!string.IsNullOrEmpty(path))
                    {
                        AddPathToStaged(path);
                    }
                }

                if (GUILayout.Button("Add Folder...", GUILayout.Height(24)))
                {
                    string folder = EditorUtility.OpenFolderPanel("Select Folder with Documents", "", "");
                    if (!string.IsNullOrEmpty(folder))
                    {
                        AddPathToStaged(folder);
                    }
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Clear Staged List", GUILayout.Height(24), GUILayout.Width(130)))
                {
                    _stagedFiles.Clear();
                }
            }

            EditorGUILayout.Space(8);

            // Staged Files List Box
            EditorGUILayout.LabelField($"Staged Documents ({_stagedFiles.Count})", EditorStyles.boldLabel);
            DrawStagedFilesTable();

            EditorGUILayout.Space(8);

            // Ingestion Parameters Preview
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("Chunking Parameters (Configured in RagConfig)", EditorStyles.miniBoldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    activeConfig.chunkSize = EditorGUILayout.IntSlider("Chunk Size", activeConfig.chunkSize, 100, 3000);
                    activeConfig.chunkOverlap = EditorGUILayout.IntSlider("Overlap", activeConfig.chunkOverlap, 0, 500);
                    activeConfig.embeddingBatchSize = EditorGUILayout.IntSlider("Batch Size", activeConfig.embeddingBatchSize, 1, 100);
                }
                if (GUI.changed) EditorUtility.SetDirty(activeConfig);
            }

            EditorGUILayout.Space(10);

            // Main Ingestion Action Buttons
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(_stagedFiles.Count == 0 || _isIngesting);
                GUI.backgroundColor = new Color(0.2f, 0.75f, 0.35f, 1f);
                if (GUILayout.Button(
                    _isIngesting ? "Ingesting Documents..." : $"Ingest & Upload Staged Documents ({_stagedFiles.Count})",
                    GUILayout.Height(36)))
                {
                    RunIngestionAsync();
                }
                GUI.backgroundColor = Color.white;
                EditorGUI.EndDisabledGroup();

                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f, 1f);
                if (GUILayout.Button("Clear Vector Store", GUILayout.Height(36), GUILayout.Width(150)))
                {
                    RunClearVectorStoreAsync();
                }
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawDropZone()
        {
            Rect dropRect = GUILayoutUtility.GetRect(0, 85, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, GUIContent.none, GUI.skin.box);

            var centeredStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            GUI.Label(
                dropRect,
                "Drag & Drop Files or Folders Here\n\n(Supported: PDF, DOCX, TXT, MD, CSV, JSON, XML, HTML, YML, CS)",
                centeredStyle);

            Event evt = Event.current;
            if (dropRect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.Use();
                }
                else if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (string path in DragAndDrop.paths)
                    {
                        AddPathToStaged(path);
                    }
                    evt.Use();
                }
            }
        }

        private void DrawStagedFilesTable()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box, GUILayout.Height(150)))
            {
                if (_stagedFiles.Count == 0)
                {
                    EditorGUILayout.Space(20);
                    EditorGUILayout.LabelField(
                        "No documents staged. Drag files into the drop zone or click 'Add Files...'",
                        EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    _stagedFilesScroll = EditorGUILayout.BeginScrollView(_stagedFilesScroll);
                    for (int i = _stagedFiles.Count - 1; i >= 0; i--)
                    {
                        string path = _stagedFiles[i];
                        string fileName = Path.GetFileName(path);
                        string sizeStr = "Unknown";
                        try
                        {
                            if (File.Exists(path))
                            {
                                long bytes = new FileInfo(path).Length;
                                sizeStr = FormatFileSize(bytes);
                            }
                        }
                        catch { }

                        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                        {
                            GUILayout.Label(fileName, EditorStyles.boldLabel, GUILayout.Width(240));
                            GUILayout.Label(Path.GetExtension(path).ToUpperInvariant(), EditorStyles.miniLabel, GUILayout.Width(50));
                            GUILayout.Label(sizeStr, EditorStyles.miniLabel, GUILayout.Width(80));
                            GUILayout.Label(path, EditorStyles.miniLabel);

                            if (GUILayout.Button("X", GUILayout.Width(22), GUILayout.Height(18)))
                            {
                                _stagedFiles.RemoveAt(i);
                            }
                        }
                    }
                    EditorGUILayout.EndScrollView();
                }
            }
        }

        private void AddPathToStaged(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            if (Directory.Exists(path))
            {
                try
                {
                    var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        if (DocumentTextExtractor.IsSupportedExtension(f) && !_stagedFiles.Contains(f))
                        {
                            _stagedFiles.Add(f);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RagKnowledgeBaseWindow] Failed to scan folder '{path}': {ex.Message}");
                }
            }
            else if (File.Exists(path))
            {
                if (DocumentTextExtractor.IsSupportedExtension(path))
                {
                    if (!_stagedFiles.Contains(path))
                    {
                        _stagedFiles.Add(path);
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Unsupported File Format",
                        $"The file '{Path.GetFileName(path)}' does not have a supported extension (.txt, .md, .pdf, .docx, .json, .csv, .xml, .html, .yml, .cs).",
                        "OK");
                }
            }
        }

        private async void RunIngestionAsync()
        {
            if (_stagedFiles.Count == 0) return;

            _isIngesting = true;
            string adminKeyPref = $"ConvAI_RAG_AdminKey_{activeConfig.name}";
            string adminKey = EditorPrefs.GetString(adminKeyPref, null);
            string effectiveAdminKey = !string.IsNullOrEmpty(adminKey) ? adminKey : activeConfig.runtimeApiKey;

            try
            {
                var (filesProcessed, chunksUploaded, error) = await RagIngestionPipeline.IngestFilesAsync(
                    activeConfig,
                    _stagedFiles.ToArray(),
                    effectiveAdminKey,
                    (progress, status) =>
                    {
                        EditorUtility.DisplayProgressBar(
                            $"Ingesting Documents ({filesProcessed} processed)",
                            status,
                            progress);
                    });

                EditorUtility.ClearProgressBar();

                if (string.IsNullOrEmpty(error))
                {
                    EditorUtility.DisplayDialog(
                        "Ingestion Complete",
                        $"Successfully processed {filesProcessed} document(s) and uploaded {chunksUploaded} chunk(s) into {activeConfig.backendType}!",
                        "OK");
                    _stagedFiles.Clear();
                }
                else
                {
                    EditorUtility.DisplayDialog("Ingestion Error", $"Ingestion encountered an issue:\n\n{error}", "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Ingestion Exception", $"An unexpected exception occurred:\n\n{ex.Message}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _isIngesting = false;
                Repaint();
            }
        }

        private async void RunClearVectorStoreAsync()
        {
            string targetName = activeConfig.backendType == VectorBackendType.LocalBakedAsset
                ? (activeConfig.localDatabase != null ? activeConfig.localDatabase.name : "Local DB")
                : activeConfig.collectionOrTableName;

            bool confirm = EditorUtility.DisplayDialog(
                "Clear Vector Store Confirmation",
                $"Are you sure you want to permanently delete all chunks stored in '{targetName}' ({activeConfig.backendType})?\n\nThis action cannot be undone.",
                "Yes, Delete All",
                "Cancel");

            if (!confirm) return;

            string adminKeyPref = $"ConvAI_RAG_AdminKey_{activeConfig.name}";
            string adminKey = EditorPrefs.GetString(adminKeyPref, null);
            string effectiveAdminKey = !string.IsNullOrEmpty(adminKey) ? adminKey : activeConfig.runtimeApiKey;

            try
            {
                var provider = VectorProviderFactory.Create(activeConfig);
                var (ok, error) = await provider.ClearAllAsync(effectiveAdminKey);

                if (ok)
                {
                    EditorUtility.DisplayDialog(
                        "Vector Store Cleared",
                        $"Successfully deleted all chunks in '{targetName}'.",
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Clear Failed", $"Failed to clear vector store:\n\n{error}", "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Clear Exception", $"Exception while clearing vector store:\n\n{ex.Message}", "OK");
            }
            finally
            {
                Repaint();
            }
        }

        #endregion

        #region Tab 3: Live Query Tester

        private void DrawQueryTesterTab()
        {
            _testerScroll = EditorGUILayout.BeginScrollView(_testerScroll);

            EditorGUILayout.LabelField("Live Query & Retrieval Tester (No Play Mode Required)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Verify your vector database retrieval, inspect similarity scores, and review extracted document chunks in real time.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(8);

            // Query Input Box
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("Search Query", EditorStyles.boldLabel);
                _searchQuery = EditorGUILayout.TextArea(_searchQuery, GUILayout.Height(45));

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _queryTopK = EditorGUILayout.IntSlider("Top K", _queryTopK, 1, 20);
                    _queryMinScore = EditorGUILayout.Slider("Min Similarity", _queryMinScore, 0f, 1f);
                }

                EditorGUILayout.Space(6);
                EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(_searchQuery) || _isSearching);
                GUI.backgroundColor = new Color(0.25f, 0.55f, 0.95f, 1f);
                if (GUILayout.Button(_isSearching ? "Searching Vector Store..." : "Run Vector Search", GUILayout.Height(30)))
                {
                    RunSearchQueryAsync();
                }
                GUI.backgroundColor = Color.white;
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.Space(10);

            // Display Query Status / Errors
            if (_isSearching)
            {
                EditorGUILayout.HelpBox("Embedding search query and executing similarity search...", MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(_searchError))
            {
                EditorGUILayout.HelpBox(_searchError, MessageType.Error);
            }
            else if (_searchResults != null)
            {
                EditorGUILayout.LabelField(
                    $"Query Results ({_searchResults.Count} matches in {_lastSearchLatencyMs} ms)",
                    EditorStyles.boldLabel);

                if (_searchResults.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        $"No document chunks matched the query with cosine similarity >= {_queryMinScore:F2}.",
                        MessageType.Warning);
                }
                else
                {
                    DrawSearchResultsList();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSearchResultsList()
        {
            _resultsScroll = EditorGUILayout.BeginScrollView(_resultsScroll, GUILayout.Height(320));

            for (int i = 0; i < _searchResults.Count; i++)
            {
                var result = _searchResults[i];
                var chunk = result.chunk;

                using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // Score badge with color coding
                        Color scoreColor = result.score >= 0.70f
                            ? new Color(0.1f, 0.75f, 0.2f)
                            : (result.score >= 0.40f ? new Color(0.9f, 0.65f, 0.1f) : new Color(0.6f, 0.6f, 0.6f));

                        var scoreStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = scoreColor } };
                        GUILayout.Label($"Score: {result.score:F3}", scoreStyle, GUILayout.Width(90));

                        string sourceName = chunk != null ? chunk.sourceName : "Unknown";
                        int chunkIdx = chunk != null ? chunk.chunkIndex : 0;
                        GUILayout.Label($"Source: {sourceName} (Chunk #{chunkIdx})", EditorStyles.boldLabel);

                        GUILayout.FlexibleSpace();

                        if (chunk != null && GUILayout.Button("Copy Chunk Text", GUILayout.Width(115)))
                        {
                            GUIUtility.systemCopyBuffer = chunk.text;
                        }
                    }

                    EditorGUILayout.Space(4);
                    string textContent = chunk != null ? chunk.text : "(null chunk)";
                    EditorGUILayout.TextArea(textContent, EditorStyles.wordWrappedLabel);

                    if (chunk != null && !string.IsNullOrEmpty(chunk.metadataJson))
                    {
                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField($"Metadata: {chunk.metadataJson}", EditorStyles.miniLabel);
                    }
                }

                EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
        }

        private async void RunSearchQueryAsync()
        {
            if (string.IsNullOrWhiteSpace(_searchQuery)) return;

            _isSearching = true;
            _searchError = null;
            _searchResults = null;
            Repaint();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var embeddingClient = new OpenAIEmbeddingClient(activeConfig);
                var (queryEmbedding, embError) = await embeddingClient.GetEmbeddingAsync(_searchQuery);

                if (!string.IsNullOrEmpty(embError))
                {
                    _searchError = $"OpenAI Embeddings Error: {embError}";
                    return;
                }

                if (queryEmbedding == null)
                {
                    _searchError = "OpenAI API returned null query embedding vector.";
                    return;
                }

                string adminKeyPref = $"ConvAI_RAG_AdminKey_{activeConfig.name}";
                string adminKey = EditorPrefs.GetString(adminKeyPref, null);
                string effectiveKey = !string.IsNullOrEmpty(adminKey) ? adminKey : activeConfig.runtimeApiKey;

                var provider = VectorProviderFactory.Create(activeConfig);
                var (results, queryError) = await provider.QueryAsync(
                    queryEmbedding,
                    _queryTopK,
                    _queryMinScore,
                    effectiveKey);

                stopwatch.Stop();
                _lastSearchLatencyMs = stopwatch.ElapsedMilliseconds;

                if (!string.IsNullOrEmpty(queryError))
                {
                    _searchError = $"Vector DB Query Error: {queryError}";
                }
                else
                {
                    _searchResults = results;
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _searchError = $"Query Exception: {ex.Message}";
            }
            finally
            {
                _isSearching = false;
                Repaint();
            }
        }

        #endregion

        #region Helpers

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024f):F1} KB";
            return $"{(bytes / (1024f * 1024f)):F2} MB";
        }

        private static string MaskApiKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 8) return "********";
            return $"{key.Substring(0, 4)}...{key.Substring(key.Length - 4)}";
        }

        #endregion
    }
}
