using System;
using UnityEditor;
using UnityEngine;
using OpenAI.RAG;

namespace OpenAI.RAG.Editor
{
    /// <summary>
    /// Custom Inspector editor for RagConfig ScriptableObject.
    /// Provides shortcuts to open the RAG Knowledge Base Window, copy Supabase pgvector SQL setups,
    /// and test the vector database connection directly in the Inspector.
    /// </summary>
    [CustomEditor(typeof(RagConfig))]
    public class RagConfigEditor : UnityEditor.Editor
    {
        private bool _isTestingConnection;
        private string _testResultMsg;
        private MessageType _testResultType;

        public override void OnInspectorGUI()
        {
            RagConfig config = (RagConfig)target;

            // Draw standard serialized properties
            DrawDefaultInspector();

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("RAG Actions & Quick Tools", EditorStyles.boldLabel);

            // Open Window Button
            GUI.backgroundColor = new Color(0.25f, 0.55f, 0.95f, 1f);
            if (GUILayout.Button("Open RAG Knowledge Base Window", GUILayout.Height(32)))
            {
                RagKnowledgeBaseWindow.Open(config);
            }
            GUI.backgroundColor = Color.white;

            // Supabase SQL Helper Button
            if (config.backendType == VectorBackendType.SupabasePgVector)
            {
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Copy Supabase pgvector SQL Setup to Clipboard", GUILayout.Height(26)))
                {
                    string sql = SupabaseVectorProvider.GetSqlSetupScript(
                        config.collectionOrTableName,
                        config.supabaseRpcFunctionName,
                        config.dimensions);

                    GUIUtility.systemCopyBuffer = sql;
                    EditorUtility.DisplayDialog(
                        "Supabase SQL Setup Copied",
                        $"The PostgreSQL pgvector setup script for table '{config.collectionOrTableName}' and function '{config.supabaseRpcFunctionName}' (dimension: {config.dimensions}) has been copied to your clipboard.\n\nOpen your Supabase project dashboard -> SQL Editor -> paste and run.",
                        "OK");
                }
            }

            // Test Connection Button
            EditorGUILayout.Space(4);
            EditorGUI.BeginDisabledGroup(_isTestingConnection);
            if (GUILayout.Button(_isTestingConnection ? "Testing Connection..." : "Test Vector DB Connection", GUILayout.Height(26)))
            {
                string adminKey = EditorPrefs.GetString($"ConvAI_RAG_AdminKey_{config.name}", null);
                string effectiveKey = !string.IsNullOrEmpty(adminKey) ? adminKey : config.runtimeApiKey;
                RunTestConnection(config, effectiveKey);
            }
            EditorGUI.EndDisabledGroup();

            // Display Test Result
            if (!string.IsNullOrEmpty(_testResultMsg))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_testResultMsg, _testResultType);
            }
        }

        private async void RunTestConnection(RagConfig config, string apiKeyOverride)
        {
            _isTestingConnection = true;
            _testResultMsg = $"Testing connection to {config.backendType}...";
            _testResultType = MessageType.Info;
            Repaint();

            try
            {
                var provider = VectorProviderFactory.Create(config);
                var (ok, message) = await provider.TestConnectionAsync(apiKeyOverride);

                if (ok)
                {
                    _testResultMsg = $"[Connection OK] {message}";
                    _testResultType = MessageType.Info;
                }
                else
                {
                    _testResultMsg = $"[Connection Failed] {message}";
                    _testResultType = MessageType.Error;
                }
            }
            catch (Exception ex)
            {
                _testResultMsg = $"[Connection Exception] {ex.Message}";
                _testResultType = MessageType.Error;
            }
            finally
            {
                _isTestingConnection = false;
                Repaint();
            }
        }
    }
}
