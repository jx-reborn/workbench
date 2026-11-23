using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Deterministically exports 100% of verified <c>IsPureStaticDialog</c> scripts from
/// <c>legacy-server/bin</c> into <c>pack-tieudaotu/data/dialogs/ambient-npcs.dialogs.json</c>
/// with zero guessing and full SHA-256 provenance.
/// </summary>
public sealed class StaticDialogExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public StaticDialogExportResult Export(
        ScriptInventoryManifest manifest,
        string legacyBinRoot,
        string packRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyBinRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);

        string dialogsPath = Path.Combine(packRoot, "data", "dialogs", "ambient-npcs.dialogs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(dialogsPath)!);

        var candidates = manifest.Entries
            .Where(static e => !e.IsExcludedSimBot && e.IsPureStaticDialog)
            .OrderBy(static e => e.Utf8DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preserve any non-pure-static curated entries already present in ambient-npcs.dialogs.json
        var candidatePathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScriptInventoryEntry c in candidates)
        {
            candidatePathSet.Add(NormalizeDialogPath(c.CanonicalScriptPath));
            candidatePathSet.Add(NormalizeDialogPath(ToScriptPath(c.Utf8DisplayPath)));
        }

        var preservedEntries = new List<JsonObject>();
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nextId = 1;

        if (File.Exists(dialogsPath))
        {
            JsonNode? existingRoot = JsonNode.Parse(File.ReadAllText(dialogsPath, Encoding.UTF8));
            if (existingRoot is JsonObject rootObj &&
                rootObj["entries"] is JsonArray existingArr)
            {
                foreach (JsonNode? node in existingArr)
                {
                    if (node is not JsonObject entryObj)
                    {
                        continue;
                    }

                    bool isCoveredByPureStatic = false;
                    if (entryObj["scriptPaths"] is JsonArray spArr)
                    {
                        foreach (JsonNode? spNode in spArr)
                        {
                            string? sp = spNode?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(sp) && candidatePathSet.Contains(NormalizeDialogPath(sp)))
                            {
                                isCoveredByPureStatic = true;
                                break;
                            }
                        }
                    }

                    if (!isCoveredByPureStatic)
                    {
                        preservedEntries.Add((JsonObject)entryObj.DeepClone());
                        string? code = entryObj["code"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(code))
                        {
                            usedCodes.Add(code);
                        }

                        int id = entryObj["id"]?.GetValue<int>() ?? 0;
                        if (id >= nextId)
                        {
                            nextId = id + 1;
                        }
                    }
                }
            }
        }

        var outputEntries = new JsonArray();
        foreach (JsonObject preserved in preservedEntries)
        {
            outputEntries.Add(preserved);
        }

        var skippedPaths = new List<string>();
        int exportedCount = 0;

        foreach (ScriptInventoryEntry entry in candidates)
        {
            string fullPath = Path.Combine(legacyBinRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                skippedPaths.Add(entry.Utf8DisplayPath);
                continue;
            }

            byte[] rawBytes = File.ReadAllBytes(fullPath);
            if (!TryExtractDialog(rawBytes, out string mode, out List<string> lines) || lines.Count == 0)
            {
                skippedPaths.Add(entry.Utf8DisplayPath);
                continue;
            }

            string baseCode = BuildKebabCode(entry.Utf8DisplayPath, entry.Sha256);
            string code = baseCode;
            int suffix = 2;
            while (!usedCodes.Add(code))
            {
                code = $"{baseCode}-{suffix++}";
            }

            var scriptPathsArr = new JsonArray();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string utf8ScriptPath = ToScriptPath(entry.Utf8DisplayPath);
            if (seenPaths.Add(utf8ScriptPath))
            {
                scriptPathsArr.Add(utf8ScriptPath);
            }

            string rawScriptPath = NormalizeDialogPath(entry.CanonicalScriptPath);
            if (!string.IsNullOrWhiteSpace(rawScriptPath) && seenPaths.Add(rawScriptPath))
            {
                scriptPathsArr.Add(rawScriptPath);
            }

            var linesArr = new JsonArray();
            foreach (string line in lines)
            {
                linesArr.Add(line);
            }

            var entryObj = new JsonObject
            {
                ["id"] = nextId++,
                ["code"] = code,
                ["scriptPaths"] = scriptPathsArr,
                ["mode"] = mode,
                ["lines"] = linesArr,
                ["sourceSha256"] = entry.Sha256,
            };

            outputEntries.Add(entryObj);
            exportedCount++;
        }

        var finalDoc = new JsonObject
        {
            ["$schema"] = "../../schemas/dialog-catalog.schema.json",
            ["schemaVersion"] = "1.0.0",
            ["domain"] = "dialogs",
            ["entries"] = outputEntries,
        };

        File.WriteAllText(dialogsPath, finalDoc.ToJsonString(JsonOptions) + "\n", new UTF8Encoding(false));

        return new StaticDialogExportResult
        {
            TotalPureStaticCandidates = candidates.Count,
            ExportedEntries = exportedCount,
            SkippedCandidates = skippedPaths.Count,
            SkippedPaths = skippedPaths,
        };
    }

    public static bool TryExtractDialog(ReadOnlySpan<byte> rawBytes, out string mode, out List<string> lines)
    {
        mode = "say";
        lines = [];

        string latin1Src = Encoding.Latin1.GetString(rawBytes);
        List<Lua4Token> tokens = Lua4Lexer.Tokenize(latin1Src);

        // First collect any string literals inside the file (handles both direct Say/Talk args and local table arrays)
        var tableStrings = new List<string>();
        bool sawTalk = false;
        bool sawSay = false;
        var directCallStrings = new List<string>();

        for (int i = 0; i < tokens.Count; i++)
        {
            Lua4Token tok = tokens[i];
            if (tok.Kind == Lua4TokenKind.Identifier &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Kind == Lua4TokenKind.LParen)
            {
                if (tok.Text.Equals("Talk", StringComparison.Ordinal))
                {
                    sawTalk = true;
                    ExtractCallStringArgs(tokens, i + 2, skipTalkHeaderArgs: true, directCallStrings);
                }
                else if (tok.Text.Equals("Say", StringComparison.Ordinal))
                {
                    sawSay = true;
                    ExtractCallStringArgs(tokens, i + 2, skipTalkHeaderArgs: false, directCallStrings);
                }
            }
            else if (tok.Kind == Lua4TokenKind.LBrace)
            {
                int depth = 1;
                int j = i + 1;
                while (j < tokens.Count && depth > 0)
                {
                    if (tokens[j].Kind == Lua4TokenKind.LBrace)
                    {
                        depth++;
                    }
                    else if (tokens[j].Kind == Lua4TokenKind.RBrace)
                    {
                        depth--;
                    }
                    else if (tokens[j].Kind == Lua4TokenKind.String && depth == 1)
                    {
                        string combined = ReadConcatenatedString(tokens, ref j);
                        if (!string.IsNullOrWhiteSpace(combined))
                        {
                            tableStrings.Add(combined);
                        }
                    }

                    j++;
                }
            }
        }

        if (sawTalk && !sawSay)
        {
            mode = "talk";
        }
        else
        {
            mode = "say";
        }

        if (directCallStrings.Count > 0)
        {
            lines.AddRange(directCallStrings);
            return true;
        }

        if (tableStrings.Count > 0)
        {
            lines.AddRange(tableStrings);
            return true;
        }

        return false;
    }

    private static void ExtractCallStringArgs(
        List<Lua4Token> tokens,
        int argStartIdx,
        bool skipTalkHeaderArgs,
        List<string> destination)
    {
        int depth = 1;
        int argIndex = 0;
        int i = argStartIdx;

        while (i < tokens.Count && depth > 0)
        {
            Lua4Token t = tokens[i];
            if (t.Kind == Lua4TokenKind.LParen)
            {
                depth++;
                i++;
                continue;
            }

            if (t.Kind == Lua4TokenKind.RParen)
            {
                depth--;
                i++;
                continue;
            }

            if (depth == 1 && t.Kind == Lua4TokenKind.Comma)
            {
                argIndex++;
                i++;
                continue;
            }

            if (depth == 1 && t.Kind == Lua4TokenKind.String)
            {
                // Talk(n, callback, line1, line2, ...): skip argIndex 0 (count) and 1 (callback)
                // Say(prompt, 0): take argIndex 0 only
                bool accept = skipTalkHeaderArgs ? argIndex >= 2 : argIndex == 0;
                string combined = ReadConcatenatedString(tokens, ref i);
                if (accept && !string.IsNullOrWhiteSpace(combined))
                {
                    destination.Add(combined);
                }
            }

            i++;
        }
    }

    private static string ReadConcatenatedString(List<Lua4Token> tokens, ref int index)
    {
        var sb = new StringBuilder();
        sb.Append(DecodeLuaTokenString(tokens[index].Text));

        while (index + 2 < tokens.Count &&
               tokens[index + 1].Kind == Lua4TokenKind.Concat &&
               tokens[index + 2].Kind == Lua4TokenKind.String)
        {
            index += 2;
            sb.Append(DecodeLuaTokenString(tokens[index].Text));
        }

        return sb.ToString();
    }

    private static string DecodeLuaTokenString(string rawTokenText)
    {
        if (string.IsNullOrEmpty(rawTokenText))
        {
            return string.Empty;
        }

        byte[] raw = HybridLegacyTextDecoder.UnescapeLuaStringBytes(Encoding.Latin1.GetBytes(rawTokenText));
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        // Preserve already-valid multi-byte UTF-8
        if (IsStrictValidUtf8WithMultibyte(raw))
        {
            return Encoding.UTF8.GetString(raw);
        }

        return HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese: true);
    }

    private static bool IsStrictValidUtf8WithMultibyte(ReadOnlySpan<byte> bytes)
    {
        bool hasNonAscii = false;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] >= 0x80)
            {
                hasNonAscii = true;
                break;
            }
        }

        if (!hasNonAscii)
        {
            return false;
        }

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            _ = strictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string BuildKebabCode(string utf8DisplayPath, string sha256)
    {
        string fileName = Path.GetFileNameWithoutExtension(utf8DisplayPath);
        var sb = new StringBuilder("static-");
        bool lastDash = true;

        foreach (char ch in fileName.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length < 40)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        string prefix = sb.ToString().TrimEnd('-');
        string shortHash = sha256.Length >= 8 ? sha256[..8].ToLowerInvariant() : "00000000";
        return $"{prefix}-{shortHash}";
    }

    private static string ToScriptPath(string utf8DisplayPath)
    {
        string normalized = utf8DisplayPath.Replace('/', '\\').TrimStart('\\');
        if (normalized.StartsWith("server1\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["server1\\".Length..];
        }

        return "\\" + normalized;
    }

    private static string NormalizeDialogPath(string path)
    {
        string normalized = path.Trim().Replace('/', '\\');
        return normalized.StartsWith('\\') ? normalized : "\\" + normalized;
    }
}
