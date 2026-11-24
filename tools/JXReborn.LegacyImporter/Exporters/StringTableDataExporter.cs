using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;

namespace JXReborn.LegacyImporter.Exporters;

/// <summary>
/// Deterministically exports <c>server1/settings/stringresource.txt</c> numeric string IDs
/// (used by <c>Say(nStringId, ...)</c>, <c>Talk(nCount, szCb, nStringId)</c>, and <c>AddNote(nStringId)</c>)
/// into <c>data/system/string-resources.json</c> with exact SHA-256 provenance and preserved trailing whitespace.
/// </summary>
public sealed class StringTableDataExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool ExportStringResources(ILegacyServerSource source, string outPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outPath);

        if (!source.TryReadServerFile(@"settings\stringresource.txt", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        string[] lines = Encoding.Latin1.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var entries = new JsonArray();
        var seenIds = new HashSet<int>();

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            int tabIdx = line.IndexOf('\t');
            if (tabIdx <= 0)
            {
                continue;
            }

            string idRaw = line[..tabIdx].Trim();
            if (!int.TryParse(idRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stringId) ||
                !seenIds.Add(stringId))
            {
                continue;
            }

            string rawLatin1Text = line[(tabIdx + 1)..];
            byte[] rawBytes = Encoding.Latin1.GetBytes(rawLatin1Text);
            string decodedText = HybridLegacyTextDecoder.DecodeSmart(rawBytes, preferVietnamese: true);

            entries.Add(new JsonObject
            {
                ["id"] = stringId,
                ["text"] = decodedText,
            });
        }

        string? parentDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var doc = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["domain"] = "string-resources",
            ["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["entries"] = entries,
        };

        File.WriteAllText(outPath, doc.ToJsonString(JsonOptions) + "\n", new UTF8Encoding(false));
        return true;
    }
}
