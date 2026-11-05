using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAI.RAG
{
    /// <summary>
    /// Optional capability for vector stores that can hand over their entire contents (chunks plus embeddings)
    /// so they can be mirrored into a <see cref="RagLocalIndex"/> for instant, network-free retrieval.
    /// </summary>
    public interface IBulkExportProvider
    {
        /// <summary>
        /// Downloads every chunk (text, metadata and embedding vector) from the remote store.
        /// </summary>
        /// <param name="apiKeyOverride">Optional backend API key override.</param>
        /// <returns>All chunks and an error description if the export failed.</returns>
        Task<(List<DocumentChunk> chunks, string error)> ExportAllAsync(string apiKeyOverride = null);
    }
}
