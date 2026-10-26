using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEngine;

namespace OpenAI.RAG.Editor
{
    /// <summary>
    /// Utility class for extracting raw text from various document formats (.txt, .md, .json, .csv, .xml, .html, .cs, .yaml, .yml, .docx, .pdf)
    /// without relying on external native binaries or heavy third-party dependencies.
    /// </summary>
    public static class DocumentTextExtractor
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt",
            ".md",
            ".json",
            ".csv",
            ".xml",
            ".html",
            ".htm",
            ".cs",
            ".yaml",
            ".yml",
            ".docx",
            ".pdf"
        };

        /// <summary>
        /// Checks whether the given file path or extension is supported by the text extractor.
        /// </summary>
        /// <param name="path">File path or extension (e.g. "document.pdf" or ".docx").</param>
        /// <returns>True if the file extension is supported; otherwise false.</returns>
        public static bool IsSupportedExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                // In case path was passed directly as an extension like ".md" or "md"
                ext = path.StartsWith(".") ? path : "." + path;
            }

            return SupportedExtensions.Contains(ext);
        }

        /// <summary>
        /// Extracts raw plain text from the specified document file.
        /// </summary>
        /// <param name="filePath">Absolute or relative path to the file.</param>
        /// <returns>Extracted plain text content, or empty string if extraction yielded no text.</returns>
        public static string ExtractText(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Debug.LogError("[DocumentTextExtractor] File path cannot be null or empty.");
                return string.Empty;
            }

            if (!File.Exists(filePath))
            {
                Debug.LogError($"[DocumentTextExtractor] File not found: {filePath}");
                return string.Empty;
            }

            string ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".docx":
                        return ExtractDocxText(filePath);
                    case ".pdf":
                        return ExtractPdfText(filePath);
                    default:
                        // Plain text formats: .txt, .md, .json, .csv, .xml, .html, .cs, .yaml, .yml
                        return File.ReadAllText(filePath, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DocumentTextExtractor] Error extracting text from '{Path.GetFileName(filePath)}': {ex.Message}");
                return string.Empty;
            }
        }

        #region DOCX Extraction

        /// <summary>
        /// Extracts text from a Microsoft Word .docx file by reading word/document.xml
        /// from the package ZIP archive.
        /// </summary>
        private static string ExtractDocxText(string filePath)
        {
            var sb = new StringBuilder();

            using (var stream = File.OpenRead(filePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry("word/document.xml");
                if (entry == null)
                {
                    Debug.LogWarning($"[DocumentTextExtractor] '{Path.GetFileName(filePath)}' contains no word/document.xml part.");
                    return string.Empty;
                }

                using (var entryStream = entry.Open())
                {
                    var doc = XDocument.Load(entryStream);
                    // Match paragraph elements (<w:p>) regardless of XML namespace
                    foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p"))
                    {
                        var pSb = new StringBuilder();
                        foreach (var child in p.Descendants())
                        {
                            if (child.Name.LocalName == "t")
                            {
                                pSb.Append(child.Value);
                            }
                            else if (child.Name.LocalName == "tab")
                            {
                                pSb.Append('\t');
                            }
                            else if (child.Name.LocalName == "br")
                            {
                                pSb.Append('\n');
                            }
                        }

                        string pText = pSb.ToString().TrimEnd();
                        if (!string.IsNullOrEmpty(pText))
                        {
                            sb.AppendLine(pText);
                        }
                    }
                }
            }

            return sb.ToString().Trim();
        }

        #endregion

        #region PDF Extraction

        /// <summary>
        /// Extracts text from a PDF document by scanning streams, decompressing FlateDecode streams,
        /// and parsing BT ... ET text blocks (Tj and TJ operators).
        /// </summary>
        private static string ExtractPdfText(string filePath)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DocumentTextExtractor] Unable to read PDF file '{filePath}': {ex.Message}");
                return string.Empty;
            }

            if (data == null || data.Length == 0)
                return string.Empty;

            // Check if PDF is encrypted
            string headerAscii = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 4096));
            if (Regex.IsMatch(headerAscii, @"/Encrypt\s+(\d+\s+\d+\s+R|<<)"))
            {
                Debug.LogWarning($"[DocumentTextExtractor] PDF '{Path.GetFileName(filePath)}' is encrypted or password-protected. Text extraction is not supported for encrypted PDFs.");
                return string.Empty;
            }

            // Also check trailer / catalog for /Encrypt
            int trailerLookback = Math.Max(0, data.Length - 4096);
            string trailerAscii = Encoding.ASCII.GetString(data, trailerLookback, data.Length - trailerLookback);
            if (Regex.IsMatch(trailerAscii, @"/Encrypt\s+(\d+\s+\d+\s+R|<<)"))
            {
                Debug.LogWarning($"[DocumentTextExtractor] PDF '{Path.GetFileName(filePath)}' is encrypted or password-protected. Text extraction is not supported for encrypted PDFs.");
                return string.Empty;
            }

            byte[] streamPattern = Encoding.ASCII.GetBytes("stream");
            byte[] endstreamPattern = Encoding.ASCII.GetBytes("endstream");

            var extractedSb = new StringBuilder();
            int searchIdx = 0;

            while (searchIdx < data.Length)
            {
                int streamPos = IndexOfBytes(data, streamPattern, searchIdx);
                if (streamPos == -1) break;

                // Inspect dictionary preceding the stream
                int dictLookback = Math.Max(0, streamPos - 500);
                string dictSnippet = Encoding.ASCII.GetString(data, dictLookback, streamPos - dictLookback);
                bool isFlate = dictSnippet.Contains("/FlateDecode");

                // Content begins after stream keyword and newline (\r\n or \n)
                int contentStart = streamPos + 6;
                if (contentStart < data.Length && data[contentStart] == '\r') contentStart++;
                if (contentStart < data.Length && data[contentStart] == '\n') contentStart++;

                int endstreamPos = IndexOfBytes(data, endstreamPattern, contentStart);
                if (endstreamPos == -1) break;

                int contentEnd = endstreamPos;
                if (contentEnd > contentStart && data[contentEnd - 1] == '\n') contentEnd--;
                if (contentEnd > contentStart && data[contentEnd - 1] == '\r') contentEnd--;

                int streamLen = contentEnd - contentStart;
                if (streamLen > 0)
                {
                    string textStream = null;
                    if (isFlate)
                    {
                        byte[] decomp = DecompressFlate(data, contentStart, streamLen);
                        if (decomp != null && decomp.Length > 0)
                        {
                            textStream = Encoding.UTF8.GetString(decomp);
                        }
                    }
                    else
                    {
                        textStream = Encoding.UTF8.GetString(data, contentStart, streamLen);
                    }

                    if (!string.IsNullOrEmpty(textStream))
                    {
                        string pageText = ExtractTextFromPdfStream(textStream);
                        if (!string.IsNullOrWhiteSpace(pageText))
                        {
                            extractedSb.AppendLine(pageText);
                        }
                    }
                }

                searchIdx = endstreamPos + 9;
            }

            string result = extractedSb.ToString().Trim();
            if (string.IsNullOrEmpty(result))
            {
                Debug.LogWarning($"[DocumentTextExtractor] PDF '{Path.GetFileName(filePath)}' contains no selectable text. It may be a scanned image or bitmap-only document.");
            }

            return result;
        }

        private static int IndexOfBytes(byte[] data, byte[] pattern, int startIndex)
        {
            if (data == null || pattern == null || startIndex < 0) return -1;
            int max = data.Length - pattern.Length;
            for (int i = startIndex; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static byte[] DecompressFlate(byte[] data, int offset, int length)
        {
            if (data == null || length <= 0 || offset < 0 || offset + length > data.Length)
                return Array.Empty<byte>();

            // Zlib header check (RFC 1950):
            // Check byte 0 is 0x78 and (byte0 * 256 + byte1) % 31 == 0
            int skip = 0;
            if (length > 2 && data[offset] == 0x78 && ((data[offset] * 256 + data[offset + 1]) % 31 == 0))
            {
                skip = 2;
            }

            try
            {
                using (var mem = new MemoryStream(data, offset + skip, length - skip))
                using (var deflate = new DeflateStream(mem, CompressionMode.Decompress))
                using (var outMem = new MemoryStream())
                {
                    deflate.CopyTo(outMem);
                    return outMem.ToArray();
                }
            }
            catch
            {
                // Fallback: if skipping 2 bytes failed, try raw stream without skip
                if (skip > 0)
                {
                    try
                    {
                        using (var mem = new MemoryStream(data, offset, length))
                        using (var deflate = new DeflateStream(mem, CompressionMode.Decompress))
                        using (var outMem = new MemoryStream())
                        {
                            deflate.CopyTo(outMem);
                            return outMem.ToArray();
                        }
                    }
                    catch
                    {
                        return Array.Empty<byte>();
                    }
                }
                return Array.Empty<byte>();
            }
        }

        private static string ExtractTextFromPdfStream(string streamContent)
        {
            var sb = new StringBuilder();
            int btIndex = 0;

            while ((btIndex = streamContent.IndexOf("BT", btIndex, StringComparison.Ordinal)) != -1)
            {
                // Verify BT is a standalone token
                bool validStart = (btIndex == 0 || char.IsWhiteSpace(streamContent[btIndex - 1]));
                bool validAfter = (btIndex + 2 >= streamContent.Length || char.IsWhiteSpace(streamContent[btIndex + 2]));

                if (!validStart || !validAfter)
                {
                    btIndex += 2;
                    continue;
                }

                int etIndex = streamContent.IndexOf("ET", btIndex + 2, StringComparison.Ordinal);
                if (etIndex == -1) break;

                string block = streamContent.Substring(btIndex + 2, etIndex - (btIndex + 2));
                ParsePdfTextBlock(block, sb);
                sb.AppendLine();

                btIndex = etIndex + 2;
            }

            return sb.ToString().Trim();
        }

        private static void ParsePdfTextBlock(string block, StringBuilder outSb)
        {
            int i = 0;
            while (i < block.Length)
            {
                char c = block[i];

                if (c == '(')
                {
                    string lit = ReadParenthesesString(block, ref i);
                    string op = ReadNextToken(block, ref i);
                    if (op == "Tj" || op == "'" || op == "\"")
                    {
                        outSb.Append(lit);
                        if (op == "'" || op == "\"") outSb.AppendLine();
                    }
                }
                else if (c == '<' && i + 1 < block.Length && block[i + 1] != '<')
                {
                    string hex = ReadHexString(block, ref i);
                    string op = ReadNextToken(block, ref i);
                    if (op == "Tj" || op == "'" || op == "\"")
                    {
                        outSb.Append(hex);
                        if (op == "'" || op == "\"") outSb.AppendLine();
                    }
                }
                else if (c == '[')
                {
                    // Array for TJ operator
                    i++;
                    var arrSb = new StringBuilder();
                    while (i < block.Length)
                    {
                        char ac = block[i];
                        if (ac == ']')
                        {
                            i++;
                            break;
                        }

                        if (ac == '(')
                        {
                            string item = ReadParenthesesString(block, ref i);
                            arrSb.Append(item);
                        }
                        else if (ac == '<' && i + 1 < block.Length && block[i + 1] != '<')
                        {
                            string item = ReadHexString(block, ref i);
                            arrSb.Append(item);
                        }
                        else if (char.IsDigit(ac) || ac == '-' || ac == '+')
                        {
                            string numStr = ReadNumber(block, ref i);
                            if (float.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float offset))
                            {
                                // Kerning offset: large negative number indicates space between words
                                if (offset <= -100 && arrSb.Length > 0 && arrSb[arrSb.Length - 1] != ' ')
                                {
                                    arrSb.Append(' ');
                                }
                            }
                        }
                        else
                        {
                            i++;
                        }
                    }

                    string op = ReadNextToken(block, ref i);
                    if (op == "TJ")
                    {
                        outSb.Append(arrSb.ToString());
                    }
                }
                else if (c == 'T' && i + 1 < block.Length)
                {
                    if (block[i + 1] == '*')
                    {
                        outSb.AppendLine();
                        i += 2;
                    }
                    else if (block[i + 1] == 'd' || block[i + 1] == 'D')
                    {
                        outSb.AppendLine();
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }
        }

        private static string ReadParenthesesString(string text, ref int index)
        {
            index++; // skip '('
            int depth = 1;
            bool escaped = false;
            var sb = new StringBuilder();

            while (index < text.Length && depth > 0)
            {
                char c = text[index];
                if (escaped)
                {
                    escaped = false;
                    switch (c)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '(': sb.Append('('); break;
                        case ')': sb.Append(')'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\r':
                            if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                            break;
                        case '\n': break;
                        default:
                            if (c >= '0' && c <= '7')
                            {
                                int oct = c - '0';
                                for (int k = 0; k < 2 && index + 1 < text.Length && text[index + 1] >= '0' && text[index + 1] <= '7'; k++)
                                {
                                    index++;
                                    oct = (oct << 3) + (text[index] - '0');
                                }
                                sb.Append((char)oct);
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '(')
                {
                    depth++;
                    sb.Append('(');
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth > 0) sb.Append(')');
                }
                else
                {
                    sb.Append(c);
                }
                index++;
            }

            return sb.ToString();
        }

        private static string ReadHexString(string text, ref int index)
        {
            index++; // skip '<'
            var sb = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '>')
                {
                    index++;
                    break;
                }
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
                index++;
            }

            string hex = sb.ToString();
            if (hex.Length % 2 != 0) hex += "0";

            byte[] bytes = new byte[hex.Length / 2];
            for (int k = 0; k < bytes.Length; k++)
            {
                bytes[k] = Convert.ToByte(hex.Substring(k * 2, 2), 16);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private static string ReadNextToken(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            int start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '(' && text[index] != '[' && text[index] != '<')
                index++;

            return index > start ? text.Substring(start, index - start) : string.Empty;
        }

        private static string ReadNumber(string text, ref int index)
        {
            int start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+'))
                index++;

            bool hasDot = false;
            while (index < text.Length)
            {
                char c = text[index];
                if (char.IsDigit(c))
                {
                    index++;
                }
                else if (c == '.' && !hasDot)
                {
                    hasDot = true;
                    index++;
                }
                else
                {
                    break;
                }
            }

            return index > start ? text.Substring(start, index - start) : "0";
        }

        #endregion
    }
}
