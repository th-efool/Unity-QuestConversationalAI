using System;

namespace OpenAI.RAG
{
    /// <summary>
    /// Factory for creating vector store provider instances (<see cref="IVectorStoreProvider"/>)
    /// based on the configured <see cref="VectorBackendType"/> in <see cref="RagConfig"/>.
    /// </summary>
    public static class VectorProviderFactory
    {
        /// <summary>
        /// Creates and returns a concrete <see cref="IVectorStoreProvider"/> instance configured for the specified RAG settings.
        /// </summary>
        /// <param name="config">RAG configuration asset defining backend type, URIs, and credentials.</param>
        /// <returns>An initialized <see cref="IVectorStoreProvider"/> implementation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="RagConfig.backendType"/> is not recognized.</exception>
        public static IVectorStoreProvider Create(RagConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config), "RagConfig cannot be null when instantiating a vector provider.");
            }

            switch (config.backendType)
            {
                case VectorBackendType.LocalBakedAsset:
                    return new LocalAssetVectorProvider(config);

                case VectorBackendType.SupabasePgVector:
                    return new SupabaseVectorProvider(config);

                case VectorBackendType.Pinecone:
                    return new PineconeVectorProvider(config);

                case VectorBackendType.Qdrant:
                    return new QdrantVectorProvider(config);

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(config.backendType),
                        config.backendType,
                        $"Unsupported vector backend type: {config.backendType}");
            }
        }

        /// <summary>
        /// Determines whether the specified backend type connects to an external remote cloud service
        /// (Supabase, Pinecone, Qdrant) rather than operating strictly offline in-memory.
        /// </summary>
        /// <param name="backendType">The backend type to evaluate.</param>
        /// <returns>True if remote cloud service; false if local offline asset.</returns>
        public static bool IsRemoteBackend(VectorBackendType backendType)
        {
            return backendType != VectorBackendType.LocalBakedAsset;
        }

        /// <summary>
        /// Determines whether the active backend configured in <paramref name="config"/> connects to a remote cloud service.
        /// </summary>
        /// <param name="config">The RAG configuration asset.</param>
        /// <returns>True if remote cloud service; false if local offline asset or config is null.</returns>
        public static bool IsRemoteBackend(RagConfig config)
        {
            return config != null && IsRemoteBackend(config.backendType);
        }
    }
}
