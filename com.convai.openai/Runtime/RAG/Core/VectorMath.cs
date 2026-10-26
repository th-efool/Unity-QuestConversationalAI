using System;

namespace OpenAI.RAG
{
    /// <summary>
    /// High-performance vector math operations for embeddings and similarity computation.
    /// Thread-safe, garbage-free in hot loops, and IL2CPP compatible.
    /// </summary>
    public static class VectorMath
    {
        private const double Epsilon = 1e-12;

        /// <summary>
        /// Calculates the cosine similarity between two float vectors: (a · b) / (||a|| * ||b||).
        /// Returns a value between -1.0f and 1.0f.
        /// Returns 0.0f safely if either vector is null, empty, lengths mismatch, or magnitude is near zero.
        /// </summary>
        /// <param name="a">First embedding vector.</param>
        /// <param name="b">Second embedding vector.</param>
        /// <returns>Cosine similarity in range [-1.0f, 1.0f], or 0.0f on error.</returns>
        public static float CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            {
                return 0f;
            }

            int length = a.Length;
            double dot = 0.0;
            double normA = 0.0;
            double normB = 0.0;

            for (int i = 0; i < length; i++)
            {
                double va = a[i];
                double vb = b[i];
                dot += va * vb;
                normA += va * va;
                normB += vb * vb;
            }

            double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
            if (denom <= Epsilon)
            {
                return 0f;
            }

            double similarity = dot / denom;

            // Clamp floating-point inaccuracies to valid cosine range
            if (similarity > 1.0)
            {
                similarity = 1.0;
            }
            else if (similarity < -1.0)
            {
                similarity = -1.0;
            }

            return (float)similarity;
        }

        /// <summary>
        /// Normalizes a vector in-place to unit length (L2 norm = 1.0).
        /// If the vector is null, empty, or has near-zero magnitude, it remains unchanged.
        /// Once normalized, cosine similarity between vectors is equivalent to their dot product.
        /// </summary>
        /// <param name="v">Vector to normalize in place.</param>
        public static void NormalizeInPlace(float[] v)
        {
            if (v == null || v.Length == 0)
            {
                return;
            }

            int length = v.Length;
            double sumSq = 0.0;

            for (int i = 0; i < length; i++)
            {
                double val = v[i];
                sumSq += val * val;
            }

            double norm = Math.Sqrt(sumSq);
            if (norm <= Epsilon)
            {
                return;
            }

            float invNorm = (float)(1.0 / norm);
            for (int i = 0; i < length; i++)
            {
                v[i] *= invNorm;
            }
        }

        /// <summary>
        /// Computes the dot product of two vectors of equal length: sum(a[i] * b[i]).
        /// Returns 0.0f safely if either vector is null, empty, or lengths mismatch.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <returns>Dot product, or 0.0f on error.</returns>
        public static float DotProduct(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            {
                return 0f;
            }

            int length = a.Length;
            double dot = 0.0;

            for (int i = 0; i < length; i++)
            {
                dot += (double)a[i] * b[i];
            }

            return (float)dot;
        }

        /// <summary>
        /// Computes the Euclidean magnitude (L2 norm) of a vector: sqrt(sum(v[i]^2)).
        /// Returns 0.0f if the vector is null or empty.
        /// </summary>
        /// <param name="v">Vector.</param>
        /// <returns>Euclidean length (magnitude).</returns>
        public static float Magnitude(float[] v)
        {
            if (v == null || v.Length == 0)
            {
                return 0f;
            }

            int length = v.Length;
            double sumSq = 0.0;

            for (int i = 0; i < length; i++)
            {
                double val = v[i];
                sumSq += val * val;
            }

            return (float)Math.Sqrt(sumSq);
        }
    }
}
