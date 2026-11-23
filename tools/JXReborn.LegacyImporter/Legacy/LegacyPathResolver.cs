using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using JXReborn.Primitives.Encodings;

namespace JXReborn.LegacyImporter.Legacy;

/// <summary>
/// Resolves legacy paths on Windows NTFS across both decoded GBK (CP936) and Latin1-mojibake filenames,
/// and provides canonical unescaped UTF-8 JSON serialization options (`JX Reborn Data Standard v1`).
/// </summary>
public static class LegacyPathResolver
{
    public static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<string, Dictionary<string, string>> DirectoryCaseMaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a raw byte path (such as <c>\script\skill\wudu\¶¾¶Ü.lua</c> or <c>settings\faction\门派设定.ini</c>)
    /// against <paramref name="serverRootPath"/> by testing GBK, Latin1, and case-insensitive segment matching.
    /// </summary>
    public static string? TryResolveFile(string serverRootPath, ReadOnlySpan<byte> rawPathBytes)
    {
        int nullIdx = rawPathBytes.IndexOf((byte)0);
        if (nullIdx >= 0)
        {
            rawPathBytes = rawPathBytes[..nullIdx];
        }

        if (rawPathBytes.IsEmpty)
        {
            return null;
        }

        string gbkPath = GbkEncoding.Instance.GetString(rawPathBytes).Trim();
        string? resolved = TryResolveStringPath(serverRootPath, gbkPath);
        if (resolved is not null)
        {
            return resolved;
        }

        string latin1Path = Encoding.Latin1.GetString(rawPathBytes).Trim();
        if (!string.Equals(latin1Path, gbkPath, StringComparison.Ordinal))
        {
            resolved = TryResolveStringPath(serverRootPath, latin1Path);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a string relative path against <paramref name="serverRootPath"/>, converting raw GBK/Unicode
    /// both ways if needed on Windows NTFS.
    /// </summary>
    public static string? TryResolveStringPath(string serverRootPath, string relativeOrEnginePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrEnginePath))
        {
            return null;
        }

        string trimmed = relativeOrEnginePath.Trim().TrimStart('\\', '/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        string direct = Path.Combine(serverRootPath, trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(direct))
        {
            return direct;
        }

        string? caseInsensitive = TryWalkSegmentsCaseInsensitive(serverRootPath, trimmed, expectDirectory: false);
        if (caseInsensitive is not null)
        {
            return caseInsensitive;
        }

        // Convert Unicode GBK string -> raw GBK bytes -> Latin1 mojibake string
        byte[] gbkBytes = GbkEncoding.Instance.GetBytes(trimmed);
        string mojibake = Encoding.Latin1.GetString(gbkBytes);
        if (!string.Equals(mojibake, trimmed, StringComparison.Ordinal))
        {
            string mojiDirect = Path.Combine(serverRootPath, mojibake.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(mojiDirect))
            {
                return mojiDirect;
            }

            caseInsensitive = TryWalkSegmentsCaseInsensitive(serverRootPath, mojibake, expectDirectory: false);
            if (caseInsensitive is not null)
            {
                return caseInsensitive;
            }
        }

        // Convert Latin1 mojibake string -> raw bytes -> Unicode GBK string
        bool allLatin1 = true;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] > 0xFF)
            {
                allLatin1 = false;
                break;
            }
        }

        if (allLatin1)
        {
            byte[] rawLatin1 = Encoding.Latin1.GetBytes(trimmed);
            string decodedGbk = GbkEncoding.Instance.GetString(rawLatin1);
            if (!string.Equals(decodedGbk, trimmed, StringComparison.Ordinal))
            {
                string gbkDirect = Path.Combine(serverRootPath, decodedGbk.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                if (File.Exists(gbkDirect))
                {
                    return gbkDirect;
                }

                caseInsensitive = TryWalkSegmentsCaseInsensitive(serverRootPath, decodedGbk, expectDirectory: false);
                if (caseInsensitive is not null)
                {
                    return caseInsensitive;
                }
            }
        }

        return null;
    }

    public static string? TryResolveDirectory(string serverRootPath, ReadOnlySpan<byte> rawPathBytes, string parentSubDir = "maps")
    {
        int nullIdx = rawPathBytes.IndexOf((byte)0);
        if (nullIdx >= 0)
        {
            rawPathBytes = rawPathBytes[..nullIdx];
        }

        if (rawPathBytes.IsEmpty)
        {
            return null;
        }

        string baseRoot = string.IsNullOrEmpty(parentSubDir)
            ? serverRootPath
            : (TryResolveDirectoryStringPath(serverRootPath, parentSubDir) ?? Path.Combine(serverRootPath, parentSubDir));
        if (!Directory.Exists(baseRoot))
        {
            return null;
        }

        string gbkRel = GbkEncoding.Instance.GetString(rawPathBytes).Trim();
        string? resolved = TryResolveDirectoryStringPath(baseRoot, gbkRel);
        if (resolved is not null)
        {
            return resolved;
        }

        string latin1Rel = Encoding.Latin1.GetString(rawPathBytes).Trim();
        if (!string.Equals(latin1Rel, gbkRel, StringComparison.Ordinal))
        {
            resolved = TryResolveDirectoryStringPath(baseRoot, latin1Rel);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    public static string? TryResolveDirectoryStringPath(string rootDir, string relativeDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            return null;
        }

        string trimmed = (relativeDir ?? string.Empty).Trim().Trim('\\', '/');
        if (trimmed.Length == 0)
        {
            return Directory.Exists(rootDir) ? Path.GetFullPath(rootDir) : null;
        }

        string direct = Path.Combine(rootDir, trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (Directory.Exists(direct))
        {
            return direct;
        }

        string? caseInsensitive = TryWalkSegmentsCaseInsensitive(rootDir, trimmed, expectDirectory: true);
        if (caseInsensitive is not null)
        {
            return caseInsensitive;
        }

        byte[] gbkBytes = GbkEncoding.Instance.GetBytes(trimmed);
        string mojibake = Encoding.Latin1.GetString(gbkBytes);
        if (!string.Equals(mojibake, trimmed, StringComparison.Ordinal))
        {
            string mojiDirect = Path.Combine(rootDir, mojibake.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            if (Directory.Exists(mojiDirect))
            {
                return mojiDirect;
            }

            caseInsensitive = TryWalkSegmentsCaseInsensitive(rootDir, mojibake, expectDirectory: true);
            if (caseInsensitive is not null)
            {
                return caseInsensitive;
            }
        }

        bool allLatin1 = true;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] > 0xFF)
            {
                allLatin1 = false;
                break;
            }
        }

        if (allLatin1)
        {
            byte[] rawLatin1 = Encoding.Latin1.GetBytes(trimmed);
            string decodedGbk = GbkEncoding.Instance.GetString(rawLatin1);
            if (!string.Equals(decodedGbk, trimmed, StringComparison.Ordinal))
            {
                string gbkDirect = Path.Combine(rootDir, decodedGbk.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                if (Directory.Exists(gbkDirect))
                {
                    return gbkDirect;
                }

                caseInsensitive = TryWalkSegmentsCaseInsensitive(rootDir, decodedGbk, expectDirectory: true);
                if (caseInsensitive is not null)
                {
                    return caseInsensitive;
                }
            }
        }

        return null;
    }

    public static string NormalizeEnginePath(ReadOnlySpan<byte> rawPathBytes)
    {
        int nullIdx = rawPathBytes.IndexOf((byte)0);
        if (nullIdx >= 0)
        {
            rawPathBytes = rawPathBytes[..nullIdx];
        }

        if (rawPathBytes.IsEmpty)
        {
            return string.Empty;
        }

        // Engine file paths on jx-linux are strictly GBK/ASCII (never TCVN3).
        string decoded = GbkEncoding.DecodeNullTerminated(rawPathBytes).Trim();
        if (decoded.Length == 0)
        {
            return string.Empty;
        }

        string normalized = decoded.Replace('/', '\\');
        return normalized.StartsWith('\\') ? normalized : "\\" + normalized;
    }

    public static void WriteCanonicalJson<T>(string outputPath, T document)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(document, CanonicalJsonOptions);
        File.WriteAllBytes(outputPath, utf8Bytes);
    }

    private static string? TryWalkSegmentsCaseInsensitive(string rootDir, string relativePath, bool expectDirectory)
    {
        if (!Directory.Exists(rootDir))
        {
            return null;
        }

        string[] parts = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        string current = rootDir;
        for (int i = 0; i < parts.Length; i++)
        {
            bool isLast = i == parts.Length - 1;
            string part = parts[i];
            string direct = Path.Combine(current, part);
            if (isLast)
            {
                bool directExists = expectDirectory ? Directory.Exists(direct) : File.Exists(direct);
                if (directExists)
                {
                    return direct;
                }

                Dictionary<string, string> entryMap = GetDirectoryEntries(current);
                if (entryMap.TryGetValue(part, out string? actualEntry))
                {
                    bool actualExists = expectDirectory ? Directory.Exists(actualEntry) : File.Exists(actualEntry);
                    if (actualExists)
                    {
                        return actualEntry;
                    }
                }

                return null;
            }

            if (Directory.Exists(direct))
            {
                current = direct;
                continue;
            }

            Dictionary<string, string> dirMap = GetDirectoryEntries(current);
            if (dirMap.TryGetValue(part, out string? actualDir) && Directory.Exists(actualDir))
            {
                current = actualDir;
            }
            else
            {
                return null;
            }
        }

        return null;
    }

    private static Dictionary<string, string> GetDirectoryEntries(string dir)
    {
        if (DirectoryCaseMaps.TryGetValue(dir, out Dictionary<string, string>? cached))
        {
            return cached;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                string name = Path.GetFileName(entry);
                map.TryAdd(name, entry);
            }
        }
        catch
        {
            // Ignore inaccessible directories
        }

        DirectoryCaseMaps[dir] = map;
        return map;
    }
}
