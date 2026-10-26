using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using OpenAI.RAG;

namespace OpenAI.RAG.Editor
{
    /// <summary>
    /// Implements recursive hierarchical character text chunking with sliding-window overlap
    /// (equivalent to LangChain's RecursiveCharacterTextSplitter) for RAG vector indexing.
    /// </summary>
    public static class RecursiveTextSplitter
    {
        private static readonly string[] DefaultSeparators =
        {
            "\n\n",
            "\n",
            ". ",
            "! ",
            "? ",
            "; ",
            " ",
            ""
        };

        /// <summary>
        /// Splits a document's raw text into a sequence of structured DocumentChunk objects
        /// ready for embedding generation and vector database upsertion.
        /// </summary>
        /// <param name="filePath">Source document path (used to derive sourceName and documentId).</param>
        /// <param name="rawText">Full extracted text content of the document.</param>
        /// <param name="chunkSize">Maximum character count per chunk (default 600).</param>
        /// <param name="chunkOverlap">Character overlap between consecutive chunks (default 100).</param>
        /// <returns>A list of DocumentChunk instances with populated identifiers, source metadata, and text.</returns>
        public static List<DocumentChunk> SplitDocument(string filePath, string rawText, int chunkSize = 600, int chunkOverlap = 100)
        {
            var result = new List<DocumentChunk>();
            if (string.IsNullOrWhiteSpace(rawText))
                return result;

            if (chunkSize <= 0) chunkSize = 600;
            chunkOverlap = Math.Max(0, Math.Min(chunkOverlap, chunkSize - 1));

            string fileName = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : "document.txt";
            string baseName = !string.IsNullOrEmpty(filePath) ? Path.GetFileNameWithoutExtension(filePath) : "document";
            string sanitizedName = SanitizeIdentifier(baseName);

            string normalizedText = rawText.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            List<string> textChunks = RecursiveSplit(normalizedText, DefaultSeparators, 0, chunkSize, chunkOverlap);

            for (int i = 0; i < textChunks.Count; i++)
            {
                string chunkText = textChunks[i].Trim();
                if (string.IsNullOrEmpty(chunkText))
                    continue;

                var chunk = new DocumentChunk(
                    id: $"{sanitizedName}_{i}",
                    documentId: sanitizedName,
                    sourceName: fileName,
                    chunkIndex: i,
                    text: chunkText,
                    embedding: null,
                    metadataJson: string.Empty
                );

                result.Add(chunk);
            }

            return result;
        }

        /// <summary>
        /// Splits arbitrary text into a list of strings hierarchically respecting chunkSize and chunkOverlap.
        /// </summary>
        /// <param name="rawText">Raw text to split.</param>
        /// <param name="chunkSize">Maximum length of each text chunk.</param>
        /// <param name="chunkOverlap">Target character overlap between consecutive chunks.</param>
        /// <returns>List of chunk strings.</returns>
        public static List<string> SplitText(string rawText, int chunkSize = 600, int chunkOverlap = 100)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return new List<string>();

            if (chunkSize <= 0) chunkSize = 600;
            chunkOverlap = Math.Max(0, Math.Min(chunkOverlap, chunkSize - 1));

            string normalized = rawText.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            return RecursiveSplit(normalized, DefaultSeparators, 0, chunkSize, chunkOverlap);
        }

        /// <summary>
        /// Sanitizes a document or file name into a clean, database-safe identifier containing only letters, numbers, and underscores.
        /// </summary>
        /// <param name="name">Raw document or file name.</param>
        /// <returns>Sanitized identifier (e.g. "User_Manual_v2").</returns>
        public static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "document";

            string clean = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
            clean = Regex.Replace(clean, @"_+", "_").Trim('_');
            return string.IsNullOrEmpty(clean) ? "document" : clean;
        }

        private static List<string> RecursiveSplit(string text, string[] separators, int sepIndex, int chunkSize, int chunkOverlap)
        {
            var finalChunks = new List<string>();
            if (string.IsNullOrEmpty(text))
                return finalChunks;

            if (text.Length <= chunkSize)
            {
                finalChunks.Add(text);
                return finalChunks;
            }

            // Find the highest-priority separator present in text
            string separator = string.Empty;
            int nextSepIndex = separators.Length;
            for (int i = sepIndex; i < separators.Length; i++)
            {
                if (separators[i] == string.Empty || text.Contains(separators[i]))
                {
                    separator = separators[i];
                    nextSepIndex = i + 1;
                    break;
                }
            }

            // Leaf case: split character-by-character
            if (string.IsNullOrEmpty(separator))
            {
                int step = Math.Max(1, chunkSize - chunkOverlap);
                for (int i = 0; i < text.Length; i += step)
                {
                    int len = Math.Min(chunkSize, text.Length - i);
                    finalChunks.Add(text.Substring(i, len));
                    if (i + len >= text.Length) break;
                }
                return finalChunks;
            }

            List<string> splits = SplitKeepingSeparator(text, separator);
            var goodSplits = new List<string>();

            foreach (var s in splits)
            {
                if (s.Length <= chunkSize)
                {
                    goodSplits.Add(s);
                }
                else
                {
                    // Flush accumulated good splits before recursing on oversized piece
                    if (goodSplits.Count > 0)
                    {
                        finalChunks.AddRange(MergeSplits(goodSplits, chunkSize, chunkOverlap));
                        goodSplits.Clear();
                    }

                    var subChunks = RecursiveSplit(s, separators, nextSepIndex, chunkSize, chunkOverlap);
                    finalChunks.AddRange(subChunks);
                }
            }

            if (goodSplits.Count > 0)
            {
                finalChunks.AddRange(MergeSplits(goodSplits, chunkSize, chunkOverlap));
            }

            return finalChunks;
        }

        private static List<string> SplitKeepingSeparator(string text, string separator)
        {
            var result = new List<string>();
            int start = 0;
            while (start < text.Length)
            {
                int idx = text.IndexOf(separator, start, StringComparison.Ordinal);
                if (idx == -1)
                {
                    string remainder = text.Substring(start);
                    if (!string.IsNullOrEmpty(remainder))
                        result.Add(remainder);
                    break;
                }

                int length = (idx - start) + separator.Length;
                string piece = text.Substring(start, length);
                if (!string.IsNullOrEmpty(piece))
                    result.Add(piece);

                start = idx + separator.Length;
            }
            return result;
        }

        private static List<string> MergeSplits(List<string> splits, int chunkSize, int chunkOverlap)
        {
            var docs = new List<string>();
            var currentDoc = new List<string>();
            int currentLen = 0;

            for (int i = 0; i < splits.Count; i++)
            {
                string piece = splits[i];
                int pieceLen = piece.Length;

                if (currentLen + pieceLen > chunkSize && currentDoc.Count > 0)
                {
                    string joined = string.Concat(currentDoc).Trim();
                    if (!string.IsNullOrEmpty(joined))
                    {
                        docs.Add(joined);
                    }

                    // Slide backwards for overlap ensuring next chunk won't exceed chunkSize
                    while (currentDoc.Count > 0)
                    {
                        int overlapLen = 0;
                        for (int k = 0; k < currentDoc.Count; k++)
                            overlapLen += currentDoc[k].Length;

                        if (overlapLen <= chunkOverlap && overlapLen + pieceLen <= chunkSize)
                            break;

                        currentDoc.RemoveAt(0);
                    }

                    currentLen = 0;
                    for (int k = 0; k < currentDoc.Count; k++)
                        currentLen += currentDoc[k].Length;
                }

                currentDoc.Add(piece);
                currentLen += pieceLen;
            }

            if (currentDoc.Count > 0)
            {
                string joined = string.Concat(currentDoc).Trim();
                if (!string.IsNullOrEmpty(joined))
                {
                    if (docs.Count == 0 || docs[docs.Count - 1] != joined)
                    {
                        docs.Add(joined);
                    }
                }
            }

            return docs;
        }
    }
}
