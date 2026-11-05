using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenAI.RAG
{
    /// <summary>
    /// One indexed knowledge chunk. When <see cref="vector"/> is present it is L2-normalised in place
    /// by <see cref="RagLocalIndex"/> so cosine similarity becomes a plain dot product.
    /// </summary>
    public sealed class RagIndexEntry
    {
        public string id;
        public string documentId;
        public string sourceName;
        public int chunkIndex;
        public string text;
        public string metadataJson;
        public float[] vector;
    }

    /// <summary>A single retrieval hit: index into the entry table plus its score.</summary>
    public struct RagIndexHit
    {
        public int index;
        public float score;
        /// <summary>Lexical search only: number of distinct query terms found in this chunk.</summary>
        public int matched;
        /// <summary>Lexical search only: number of distinct (non-stop-word) query terms.</summary>
        public int queryTerms;

        /// <summary>Fraction of query terms found in the chunk (0..1). Always 0 for semantic hits.</summary>
        public float Coverage
        {
            get { return queryTerms > 0 ? (float)matched / queryTerms : 0f; }
        }
    }

    /// <summary>
    /// Unity-free in-memory retrieval index: BM25 lexical search (no network, no embedding call)
    /// plus brute-force cosine search over pre-computed embeddings, with a compact binary on-disk format.
    /// Intended for knowledge bases of up to a few thousand chunks.
    /// </summary>
    public sealed class RagLocalIndex
    {
        private const float K1 = 1.5f;
        private const float B = 0.75f;
        private const int FileMagic = 0x43474152; // "RAGC"
        private const int FileVersion = 1;

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "the", "is", "are", "was", "were", "be", "been", "am", "to", "of", "in", "on", "for",
            "and", "or", "what", "which", "how", "do", "does", "did", "i", "me", "my", "you", "your", "it",
            "its", "this", "that", "these", "those", "with", "from", "by", "at", "as", "can", "could",
            "should", "would", "will", "please", "tell", "about", "give", "show", "need", "want", "get",
            "there", "if", "so", "than", "then", "when", "where", "who", "why", "our", "we", "us", "up",
            "out", "any", "some", "search", "find", "look", "manual", "knowledge", "base", "know", "let"
        };

        private readonly RagIndexEntry[] _entries;
        private readonly Dictionary<string, int>[] _termFreq;
        private readonly int[] _docLen;
        private readonly Dictionary<string, int> _docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _idf = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly float _avgDocLen;

        /// <summary>Number of indexed chunks.</summary>
        public int Count { get { return _entries.Length; } }

        /// <summary>All indexed entries (vectors already normalised).</summary>
        public IList<RagIndexEntry> Entries { get { return _entries; } }

        /// <summary>Returns the entry at <paramref name="index"/>.</summary>
        public RagIndexEntry GetEntry(int index)
        {
            return _entries[index];
        }

        /// <summary>Builds the lexical and vector index. Normalises each entry vector in place.</summary>
        public RagLocalIndex(IList<RagIndexEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException("entries");

            int n = entries.Count;
            _entries = new RagIndexEntry[n];
            _termFreq = new Dictionary<string, int>[n];
            _docLen = new int[n];

            long totalLen = 0;
            for (int i = 0; i < n; i++)
            {
                var e = entries[i];
                _entries[i] = e;

                if (e.vector != null && e.vector.Length > 0)
                {
                    Normalize(e.vector);
                }

                var tf = new Dictionary<string, int>(StringComparer.Ordinal);
                int len = 0;
                var tokens = Tokenize((e.sourceName ?? string.Empty) + " " + (e.text ?? string.Empty));
                for (int t = 0; t < tokens.Count; t++)
                {
                    int c;
                    tf.TryGetValue(tokens[t], out c);
                    tf[tokens[t]] = c + 1;
                    len++;
                }

                foreach (var kv in tf)
                {
                    int df;
                    _docFreq.TryGetValue(kv.Key, out df);
                    _docFreq[kv.Key] = df + 1;
                }

                _termFreq[i] = tf;
                _docLen[i] = len;
                totalLen += len;
            }

            _avgDocLen = n > 0 ? Math.Max(1f, (float)totalLen / n) : 1f;

            foreach (var kv in _docFreq)
            {
                _idf[kv.Key] = (float)Math.Log(1.0 + (n - kv.Value + 0.5) / (kv.Value + 0.5));
            }
        }

        /// <summary>
        /// BM25 keyword search. Needs no network and no embedding. Results are ordered best-first.
        /// </summary>
        public List<RagIndexHit> SearchLexical(string query, int topK)
        {
            var hits = new List<RagIndexHit>();
            if (topK <= 0 || _entries.Length == 0 || string.IsNullOrWhiteSpace(query)) return hits;

            var terms = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in Tokenize(query))
            {
                if (seen.Add(t)) terms.Add(t);
            }
            if (terms.Count == 0) return hits;

            for (int i = 0; i < _entries.Length; i++)
            {
                float score = 0f;
                int matched = 0;
                var tf = _termFreq[i];
                float norm = K1 * (1f - B + B * _docLen[i] / _avgDocLen);

                for (int t = 0; t < terms.Count; t++)
                {
                    int f;
                    if (!tf.TryGetValue(terms[t], out f)) continue;
                    matched++;
                    score += _idf[terms[t]] * (f * (K1 + 1f)) / (f + norm);
                }

                if (matched == 0) continue;
                InsertSorted(hits, new RagIndexHit { index = i, score = score, matched = matched, queryTerms = terms.Count }, topK);
            }

            return hits;
        }

        /// <summary>
        /// Brute-force cosine search over the stored embeddings (vectors are normalised, so this is a dot product).
        /// Entries without a vector or with a different dimension are skipped.
        /// </summary>
        public List<RagIndexHit> SearchSemantic(float[] queryVector, int topK, float minScore)
        {
            var hits = new List<RagIndexHit>();
            if (topK <= 0 || queryVector == null || queryVector.Length == 0) return hits;

            var q = (float[])queryVector.Clone();
            Normalize(q);

            for (int i = 0; i < _entries.Length; i++)
            {
                var v = _entries[i].vector;
                if (v == null || v.Length != q.Length) continue;

                float s = 0f;
                for (int d = 0; d < q.Length; d++) s += q[d] * v[d];
                if (s < minScore) continue;

                InsertSorted(hits, new RagIndexHit { index = i, score = s }, topK);
            }

            return hits;
        }

        /// <summary>Normalises <paramref name="v"/> to unit length in place (no-op for a zero vector).</summary>
        public static void Normalize(float[] v)
        {
            double sum = 0.0;
            for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
            double norm = Math.Sqrt(sum);
            if (norm < 1e-12) return;
            float inv = (float)(1.0 / norm);
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
        }

        /// <summary>Lower-cases, splits on non-alphanumerics, drops stop words and light-stems plurals.</summary>
        public static List<string> Tokenize(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            var sb = new StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                char c = i < text.Length ? text[i] : ' ';
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    continue;
                }

                if (sb.Length == 0) continue;
                string token = Stem(sb.ToString());
                sb.Length = 0;
                if (token.Length < 2 || StopWords.Contains(token)) continue;
                result.Add(token);
            }

            return result;
        }

        private static string Stem(string t)
        {
            if (t.Length > 4 && t.EndsWith("ies", StringComparison.Ordinal)) return t.Substring(0, t.Length - 3) + "y";
            if (t.Length > 3 && t.EndsWith("s", StringComparison.Ordinal)
                && !t.EndsWith("ss", StringComparison.Ordinal)
                && !t.EndsWith("us", StringComparison.Ordinal)
                && !t.EndsWith("is", StringComparison.Ordinal))
            {
                return t.Substring(0, t.Length - 1);
            }
            return t;
        }

        private static void InsertSorted(List<RagIndexHit> hits, RagIndexHit hit, int topK)
        {
            if (hits.Count >= topK && hit.score <= hits[hits.Count - 1].score) return;

            int pos = hits.Count;
            while (pos > 0 && hits[pos - 1].score < hit.score) pos--;
            hits.Insert(pos, hit);
            if (hits.Count > topK) hits.RemoveAt(hits.Count - 1);
        }

        /// <summary>Writes the entries to <paramref name="path"/> atomically (temp file then overwrite).</summary>
        public static void Save(string path, string fingerprint, IList<RagIndexEntry> entries)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(FileMagic);
                w.Write(FileVersion);
                w.Write(fingerprint ?? string.Empty);
                w.Write(entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    w.Write(e.id ?? string.Empty);
                    w.Write(e.documentId ?? string.Empty);
                    w.Write(e.sourceName ?? string.Empty);
                    w.Write(e.chunkIndex);
                    w.Write(e.text ?? string.Empty);
                    w.Write(e.metadataJson ?? string.Empty);
                    int vl = e.vector != null ? e.vector.Length : 0;
                    w.Write(vl);
                    for (int d = 0; d < vl; d++) w.Write(e.vector[d]);
                }
            }

            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        /// <summary>
        /// Reads entries written by <see cref="Save"/>. Returns null when the file is missing, corrupt,
        /// from another version, or its fingerprint does not match.
        /// </summary>
        public static List<RagIndexEntry> Load(string path, string fingerprint)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var r = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (r.ReadInt32() != FileMagic) return null;
                    if (r.ReadInt32() != FileVersion) return null;
                    if (r.ReadString() != (fingerprint ?? string.Empty)) return null;

                    int count = r.ReadInt32();
                    if (count < 0 || count > 1000000) return null;

                    var list = new List<RagIndexEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var e = new RagIndexEntry();
                        e.id = r.ReadString();
                        e.documentId = r.ReadString();
                        e.sourceName = r.ReadString();
                        e.chunkIndex = r.ReadInt32();
                        e.text = r.ReadString();
                        e.metadataJson = r.ReadString();
                        int vl = r.ReadInt32();
                        if (vl < 0 || vl > 65536) return null;
                        if (vl > 0)
                        {
                            e.vector = new float[vl];
                            for (int d = 0; d < vl; d++) e.vector[d] = r.ReadSingle();
                        }
                        list.Add(e);
                    }

                    return list;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
