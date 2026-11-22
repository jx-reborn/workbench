using System;
using System.IO;
using System.Text;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.ScriptMigrator.Rules;

namespace JXReborn.ScriptMigrator.Analyzers;

/// <summary>
/// Decodes raw legacy Lua 4.0 scripts (GBK + TCVN3 decimal escapes <c>\ddd</c>) into readable UTF-8 mirror files
/// without altering Lua syntax or control flow, enabling 100% ground-truth inspection without guessing.
/// </summary>
public sealed class LuaUtf8MirrorExporter
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>
    /// Converts raw legacy Lua bytes into a UTF-8 string with all string literals and comments faithfully decoded.
    /// </summary>
    public static string ConvertLuaBytesToUtf8(ReadOnlySpan<byte> rawBytes, string? canonicalSourcePath = null)
    {
        string latin1Src = Latin1.GetString(rawBytes);
        int i = 0;
        int len = latin1Src.Length;

        if (len >= 3 && latin1Src[0] == '\u00EF' && latin1Src[1] == '\u00BB' && latin1Src[2] == '\u00BF')
        {
            i = 3;
        }

        var sb = new StringBuilder(len + 128);
        if (!string.IsNullOrWhiteSpace(canonicalSourcePath))
        {
            sb.Append("-- [JX-LINUX SOURCE] ").AppendLine(canonicalSourcePath);
        }

        while (i < len)
        {
            char c = latin1Src[i];

            // Line or block comment: -- ... or --[[ ... ]]
            if (c == '-' && i + 1 < len && latin1Src[i + 1] == '-')
            {
                if (i + 3 < len && latin1Src[i + 2] == '[' && latin1Src[i + 3] == '[')
                {
                    int start = i + 4;
                    int end = latin1Src.IndexOf("]]", start, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        end = len;
                    }

                    string decoded = DecodeRawCommentSegment(latin1Src.AsSpan(start, end - start));
                    sb.Append("--[[").Append(decoded).Append("]]");
                    i = end < len ? end + 2 : len;
                    continue;
                }

                int lineEnd = latin1Src.IndexOf('\n', i + 2);
                if (lineEnd < 0)
                {
                    lineEnd = len;
                }

                int contentEnd = lineEnd > i + 2 && latin1Src[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                string decodedLine = DecodeRawCommentSegment(latin1Src.AsSpan(i + 2, contentEnd - (i + 2)));
                sb.Append("--").Append(decodedLine);
                if (lineEnd < len)
                {
                    sb.Append('\n');
                    i = lineEnd + 1;
                }
                else
                {
                    i = len;
                }

                continue;
            }

            // Long string literal: [[ ... ]]
            if (c == '[' && i + 1 < len && latin1Src[i + 1] == '[')
            {
                int start = i + 2;
                int end = latin1Src.IndexOf("]]", start, StringComparison.Ordinal);
                if (end < 0)
                {
                    end = len;
                }

                byte[] rawLiteralBytes = Latin1.GetBytes(latin1Src.Substring(start, end - start));
                string decoded = HybridLegacyTextDecoder.DecodeSmart(rawLiteralBytes, preferVietnamese: true);
                sb.Append("[[").Append(decoded).Append("]]");
                i = end < len ? end + 2 : len;
                continue;
            }

            // Quoted string literal: "..." or '...'
            if (c is '"' or '\'')
            {
                char quote = c;
                int start = i + 1;
                int cursor = start;
                while (cursor < len)
                {
                    char sc = latin1Src[cursor];
                    if (sc == '\\' && cursor + 1 < len)
                    {
                        cursor += 2;
                        continue;
                    }

                    if (sc == quote || sc == '\n')
                    {
                        break;
                    }

                    cursor++;
                }

                string rawInner = latin1Src.Substring(start, cursor - start);
                string decoded = DecodeLuaQuotedString(rawInner, quote);
                sb.Append(quote).Append(decoded).Append(quote);
                i = cursor < len && latin1Src[cursor] == quote ? cursor + 1 : cursor;
                continue;
            }

            // Raw non-ASCII bytes outside strings/comments (rare legacy identifiers or stray bytes)
            if (c >= 0x80)
            {
                int start = i;
                while (i < len && latin1Src[i] >= 0x80)
                {
                    i++;
                }

                byte[] chunk = Latin1.GetBytes(latin1Src.Substring(start, i - start));
                sb.Append(HybridLegacyTextDecoder.DecodeSmart(chunk, preferVietnamese: false));
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    public int ExportMirror(string legacyBinRoot, string mirrorOutputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyBinRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorOutputRoot);

        int exported = 0;
        exported += ExportScope(legacyBinRoot, Path.Combine(legacyBinRoot, "server1", "script"), mirrorOutputRoot);
        exported += ExportScope(legacyBinRoot, Path.Combine(legacyBinRoot, "gateway", "s3relay", "script"), mirrorOutputRoot);
        return exported;
    }

    private static int ExportScope(string legacyBinRoot, string scopeDir, string mirrorOutputRoot)
    {
        if (!Directory.Exists(scopeDir))
        {
            return 0;
        }

        int count = 0;
        foreach (string filePath in Directory.EnumerateFiles(scopeDir, "*.lua", SearchOption.AllDirectories))
        {
            string relPath = LegacyExclusionRules.NormalizeRelativePath(Path.GetRelativePath(legacyBinRoot, filePath));
            if (LegacyExclusionRules.IsExcludedSimBotScript(relPath))
            {
                continue;
            }

            byte[] rawBytes = File.ReadAllBytes(filePath);
            string utf8Content = ConvertLuaBytesToUtf8(rawBytes, relPath);
            string outPath = Path.Combine(mirrorOutputRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            string? outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            File.WriteAllText(outPath, utf8Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            count++;
        }

        return count;
    }

    private static string DecodeRawCommentSegment(ReadOnlySpan<char> segment)
    {
        bool hasHigh = false;
        for (int i = 0; i < segment.Length; i++)
        {
            if (segment[i] >= 0x80)
            {
                hasHigh = true;
                break;
            }
        }

        if (!hasHigh)
        {
            return segment.ToString();
        }

        byte[] rawBytes = Latin1.GetBytes(segment.ToString());
        return HybridLegacyTextDecoder.DecodeSmart(rawBytes, preferVietnamese: false);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string DecodeLuaQuotedString(string rawInner, char quote)
    {
        if (rawInner.Length == 0)
        {
            return string.Empty;
        }

        byte[] rawBytes = Latin1.GetBytes(rawInner);
        byte[] unescaped = HybridLegacyTextDecoder.UnescapeLuaStringBytes(rawBytes);

        string decoded = DecodeMixedSegments(unescaped);

        var escaped = new StringBuilder(decoded.Length + 8);
        foreach (char ch in decoded)
        {
            switch (ch)
            {
                case '\\':
                    escaped.Append(@"\\");
                    break;
                case '\n':
                    escaped.Append(@"\n");
                    break;
                case '\r':
                    escaped.Append(@"\r");
                    break;
                case '\t':
                    escaped.Append(@"\t");
                    break;
                case '\0':
                    escaped.Append(@"\000");
                    break;
                default:
                    if (ch == quote)
                    {
                        escaped.Append('\\').Append(quote);
                    }
                    else
                    {
                        escaped.Append(ch);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    private static string DecodeMixedSegments(byte[] bytes)
    {
        // Preserve already-valid multi-byte UTF-8 strings verbatim
        if (IsValidMultiByteUtf8(bytes))
        {
            return StrictUtf8.GetString(bytes);
        }

        // Split `<...>` tags (e.g. `<link=image:\spr\...\GBK.spr>TCVN3 text<link>`) so GBK paths and TCVN3 text decode independently
        int ltIdx = Array.IndexOf(bytes, (byte)'<');
        if (ltIdx < 0)
        {
            return HybridLegacyTextDecoder.DecodeSmart(bytes, preferVietnamese: true);
        }

        var sb = new StringBuilder(bytes.Length);
        int cursor = 0;
        while (cursor < bytes.Length)
        {
            int nextLt = Array.IndexOf(bytes, (byte)'<', cursor);
            if (nextLt < 0)
            {
                sb.Append(HybridLegacyTextDecoder.DecodeSmart(bytes.AsSpan(cursor), preferVietnamese: true));
                break;
            }

            if (nextLt > cursor)
            {
                sb.Append(HybridLegacyTextDecoder.DecodeSmart(bytes.AsSpan(cursor, nextLt - cursor), preferVietnamese: true));
            }

            int nextGt = Array.IndexOf(bytes, (byte)'>', nextLt + 1);
            if (nextGt < 0)
            {
                sb.Append(HybridLegacyTextDecoder.DecodeSmart(bytes.AsSpan(nextLt), preferVietnamese: true));
                break;
            }

            ReadOnlySpan<byte> tagSpan = bytes.AsSpan(nextLt, nextGt - nextLt + 1);
            bool isPathTag = tagSpan.IndexOf((byte)'\\') >= 0 || tagSpan.IndexOf((byte)'/') >= 0;
            sb.Append(HybridLegacyTextDecoder.DecodeSmart(tagSpan, preferVietnamese: !isPathTag));
            cursor = nextGt + 1;
        }

        return sb.ToString();
    }

    private static bool IsValidMultiByteUtf8(byte[] bytes)
    {
        bool hasMultiByte = false;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] >= 0x80)
            {
                hasMultiByte = true;
                break;
            }
        }

        if (!hasMultiByte)
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
