using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using OpenAI.RAG;

namespace OpenAI.RAG.Editor
{
    /// <summary>
    /// Pipeline for extracting text from supported document files, recursively splitting them into
    /// semantically coherent chunks, generating OpenAI embeddings in batches, and uploading them
    /// into the configured vector store provider (Local, Supabase, Pinecone, or Qdrant).
    /// </summary>
    public static class RagIngestionPipeline
    {
        /// <summary>
        /// Ingests a collection of document files or directory paths into the vector database.
        /// </summary>
        /// <param name="config">The active RAG configuration asset.</param>
        /// <param name="filePaths">List of file or directory paths to ingest.</param>
        /// <param name="adminApiKeyOverride">Optional admin/write API key for cloud databases.</param>
        /// <param name="onProgress">Progress callback reporting normalized progress (0.0 to 1.0) and status message.</param>
        /// <returns>A tuple of (files processed count, chunks uploaded count, error message if any).</returns>
        public static async Task<(int filesProcessed, int chunksUploaded, string error)> IngestFilesAsync(
            RagConfig config,
            IReadOnlyList<string> filePaths,
            string adminApiKeyOverride = null,
            Action<float, string> onProgress = null)
        {
            if (config == null)
            {
                return (0, 0, "RAG Configuration asset cannot be null.");
            }

            if (filePaths == null || filePaths.Count == 0)
            {
                return (0, 0, "No file paths provided for ingestion.");
            }

            // Validate provider creation
            IVectorStoreProvider provider;
            try
            {
                provider = VectorProviderFactory.Create(config);
            }
            catch (Exception ex)
            {
                return (0, 0, $"Failed to initialize vector provider for {config.backendType}: {ex.Message}");
            }

            if (config.backendType == VectorBackendType.LocalBakedAsset && config.localDatabase == null)
            {
                return (0, 0, "Local vector database ScriptableObject (localDatabase) is not assigned in RagConfig.");
            }

            // Validate OpenAI API Key for embeddings
            string openAIApiKey = config.EffectiveOpenAIApiKey;
            if (string.IsNullOrWhiteSpace(openAIApiKey))
            {
                return (0, 0, "OpenAI API key is missing. Assign a valid OpenAIConfig in RagConfig.");
            }

            onProgress?.Invoke(0.02f, "Discovering supported document files...");

            // 1. Discover all valid files (recursively scanning directories)
            var targetFiles = new List<string>();
            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (Directory.Exists(path))
                {
                    try
                    {
                        var dirFiles = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                        foreach (var f in dirFiles)
                        {
                            if (DocumentTextExtractor.IsSupportedExtension(f) && !targetFiles.Contains(f))
                            {
                                targetFiles.Add(f);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[RagIngestionPipeline] Failed to enumerate directory '{path}': {ex.Message}");
                    }
                }
                else if (File.Exists(path))
                {
                    if (DocumentTextExtractor.IsSupportedExtension(path) && !targetFiles.Contains(path))
                    {
                        targetFiles.Add(path);
                    }
                    else if (!DocumentTextExtractor.IsSupportedExtension(path))
                    {
                        Debug.LogWarning($"[RagIngestionPipeline] Skipping unsupported file extension: '{path}'");
                    }
                }
            }

            if (targetFiles.Count == 0)
            {
                return (0, 0, "No supported document files found to ingest (.txt, .md, .pdf, .docx, .json, .csv, .xml, .html, .yml, .cs).");
            }

            int filesProcessed = 0;
            var allChunks = new List<DocumentChunk>();

            // 2. Extract text and split each document into chunks
            for (int i = 0; i < targetFiles.Count; i++)
            {
                string filePath = targetFiles[i];
                string fileName = Path.GetFileName(filePath);
                float parseProgress = 0.05f + ((float)i / targetFiles.Count) * 0.25f;
                onProgress?.Invoke(parseProgress, $"Extracting text: {fileName} ({i + 1}/{targetFiles.Count})...");

                string rawText;
                try
                {
                    rawText = DocumentTextExtractor.ExtractText(filePath);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RagIngestionPipeline] Error extracting text from '{filePath}': {ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rawText))
                {
                    Debug.LogWarning($"[RagIngestionPipeline] Extracted text from '{fileName}' is empty or whitespace. Skipping.");
                    continue;
                }

                int chunkSize = config.chunkSize > 0 ? config.chunkSize : 600;
                int chunkOverlap = config.chunkOverlap >= 0 ? config.chunkOverlap : 100;

                List<DocumentChunk> chunks;
                try
                {
                    chunks = RecursiveTextSplitter.SplitDocument(filePath, rawText, chunkSize, chunkOverlap);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RagIngestionPipeline] Error splitting text for '{fileName}': {ex.Message}");
                    continue;
                }

                if (chunks != null && chunks.Count > 0)
                {
                    allChunks.AddRange(chunks);
                    filesProcessed++;
                }
            }

            if (allChunks.Count == 0)
            {
                return (filesProcessed, 0, "No text chunks generated from the extracted documents.");
            }

            onProgress?.Invoke(0.30f, $"Prepared {allChunks.Count} chunks from {filesProcessed} files. Initializing embeddings client...");

            // 3. Batch embeddings generation and vector store upsertion
            var embeddingClient = new OpenAIEmbeddingClient(config);
            int batchSize = config.embeddingBatchSize > 0 ? config.embeddingBatchSize : 50;
            int totalChunks = allChunks.Count;
            int chunksUploaded = 0;

            for (int i = 0; i < totalChunks; i += batchSize)
            {
                int currentBatchSize = Math.Min(batchSize, totalChunks - i);
                var batch = allChunks.GetRange(i, currentBatchSize);

                float batchStartProgress = 0.30f + ((float)i / totalChunks) * 0.65f;
                string statusMsg = $"Generating embeddings & uploading chunks {i + 1}-{i + currentBatchSize} / {totalChunks}...";
                onProgress?.Invoke(batchStartProgress, statusMsg);

                // Collect texts for this batch
                var texts = new List<string>(currentBatchSize);
                for (int b = 0; b < currentBatchSize; b++)
                {
                    texts.Add(batch[b].text);
                }

                // Call OpenAI Embedding API
                var (embeddings, embError) = await embeddingClient.GetEmbeddingsBatchAsync(texts);
                if (!string.IsNullOrEmpty(embError))
                {
                    return (filesProcessed, chunksUploaded, $"OpenAI Embeddings API failed at chunk batch [{i + 1}-{i + currentBatchSize}]: {embError}");
                }

                if (embeddings == null || embeddings.Count != currentBatchSize)
                {
                    return (filesProcessed, chunksUploaded, $"OpenAI Embeddings returned {embeddings?.Count ?? 0} vectors but expected {currentBatchSize}.");
                }

                // Assign embedding vectors to chunks
                for (int b = 0; b < currentBatchSize; b++)
                {
                    batch[b].embedding = embeddings[b];
                }

                // Upsert to the configured Vector Store provider
                var (upsertOk, upsertError) = await provider.UpsertChunksAsync(batch, adminApiKeyOverride);
                if (!upsertOk)
                {
                    return (filesProcessed, chunksUploaded, $"Vector store ({config.backendType}) upsert failed at chunks [{i + 1}-{i + currentBatchSize}]: {upsertError}");
                }

                chunksUploaded += currentBatchSize;
            }

            onProgress?.Invoke(1.0f, $"Ingestion completed: {filesProcessed} files, {chunksUploaded} chunks stored.");
            return (filesProcessed, chunksUploaded, null);
        }

        /// <summary>
        /// Ingests a single document file into the vector database.
        /// </summary>
        public static Task<(int filesProcessed, int chunksUploaded, string error)> IngestFileAsync(
            RagConfig config,
            string filePath,
            string adminApiKeyOverride = null,
            Action<float, string> onProgress = null)
        {
            return IngestFilesAsync(config, new[] { filePath }, adminApiKeyOverride, onProgress);
        }

        /// <summary>
        /// Ingests an entire directory of documents into the vector database.
        /// </summary>
        public static Task<(int filesProcessed, int chunksUploaded, string error)> IngestFolderAsync(
            RagConfig config,
            string folderPath,
            string adminApiKeyOverride = null,
            Action<float, string> onProgress = null)
        {
            return IngestFilesAsync(config, new[] { folderPath }, adminApiKeyOverride, onProgress);
        }
    }
}
