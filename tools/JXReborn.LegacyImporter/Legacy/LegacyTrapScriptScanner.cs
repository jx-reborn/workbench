using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.Primitives.Encodings;
using JXReborn.Primitives.WorldZones;

namespace JXReborn.LegacyImporter.Legacy;

/// <summary>
/// Scans 100% of legacy <c>script/**/*.lua</c> files from <see cref="ILegacyServerSource"/>, hashes canonical
/// GBK byte paths via <see cref="PakFilePathHasher"/>, normalizes <c>ScriptPath</c> to clean UTF-8, and extracts
/// <see cref="PortalTrapBindingTemplate"/> definitions (including <c>\连接trap\</c> and <c>CustomScript</c> traps).
/// </summary>
public static partial class LegacyTrapScriptScanner
{
    private static readonly Encoding Cp1252;

    static LegacyTrapScriptScanner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(1252);
    }

    public static FrozenDictionary<uint, PortalTrapBindingTemplate> ScanServerRoot(string serverRootPath)
    {
        ArgumentNullException.ThrowIfNull(serverRootPath);
        return ScanServerRoot(new FolderLegacyServerSource(serverRootPath));
    }

    public static FrozenDictionary<uint, PortalTrapBindingTemplate> ScanServerRoot(ILegacyServerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Order trap-dedicated paths first so any dedicated trap script takes precedence
        string[] luaFiles = source.EnumerateServerFiles("script", recursive: true)
            .Where(static relKey => relKey.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
                                    !LegacyScriptCorpusChecker.IsExcludedSimBotScript(relKey))
            .OrderByDescending(static relKey => relKey.Contains("trap", StringComparison.OrdinalIgnoreCase))
            .ThenBy(static relKey => relKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var bindings = new Dictionary<uint, PortalTrapBindingTemplate>(luaFiles.Length);
        foreach (string relKey in luaFiles)
        {
            string rawCanonicalPath = "\\" + relKey.Replace('/', '\\').TrimStart('\\');
            string utf8ScriptPath = LegacyScriptPathNormalizer.NormalizeToUtf8(rawCanonicalPath);
            if (string.IsNullOrWhiteSpace(utf8ScriptPath))
            {
                continue;
            }

            uint[] candidateHashes = ComputeCandidateHashes(rawCanonicalPath, utf8ScriptPath);
            if (candidateHashes.Length == 0 || !source.TryReadServerFile(relKey, out byte[]? fileBytes) || fileBytes is null)
            {
                continue;
            }

            PortalTrapBindingTemplate parsed = ParseLuaTrapOrCustomScript(utf8ScriptPath, candidateHashes[0], fileBytes);
            foreach (uint hash in candidateHashes)
            {
                bindings.TryAdd(hash, parsed with { TrapId = hash });
            }
        }

        return bindings.ToFrozenDictionary();
    }

    private static uint[] ComputeCandidateHashes(string rawCanonicalPath, string utf8ScriptPath)
    {
        var hashes = new List<uint>(3);

        void AddHash(byte[] pathBytes)
        {
            if (pathBytes.Length == 0)
            {
                return;
            }

            uint h = PakFilePathHasher.ComputeHash(pathBytes);
            if (!hashes.Contains(h))
            {
                hashes.Add(h);
            }
        }

        AddHash(GbkEncoding.Instance.GetBytes(utf8ScriptPath));
        AddHash(Cp1252.GetBytes(rawCanonicalPath));
        AddHash(Encoding.Latin1.GetBytes(rawCanonicalPath));

        return hashes.ToArray();
    }

    private static PortalTrapBindingTemplate ParseLuaTrapOrCustomScript(
        string utf8ScriptPath,
        uint primaryTrapId,
        ReadOnlySpan<byte> rawBytes)
    {
        int nullIdx = rawBytes.IndexOf((byte)0);
        if (nullIdx >= 0)
        {
            rawBytes = rawBytes[..nullIdx];
        }

        string content = Encoding.Latin1.GetString(rawBytes);
        bool hasInclude = IncludeCallRegex().IsMatch(content);

        // 1. Single-target static Cross-Map NewWorld(mapId, cellX, cellY) without Include(...)
        MatchCollection newWorldMatches = NewWorldRegex().Matches(content);
        if (!hasInclude &&
            newWorldMatches.Count == 1 &&
            ushort.TryParse(newWorldMatches[0].Groups[1].Value, out ushort targetMapId) &&
            int.TryParse(newWorldMatches[0].Groups[2].Value, out int targetCellX) &&
            int.TryParse(newWorldMatches[0].Groups[3].Value, out int targetCellY))
        {
            int targetMpsX = ToMpsX(targetCellX);
            int targetMpsY = ToMpsY(targetCellY);

            ushort minLevel = 1;
            Match levelMatch = GetLevelRegex().Match(content);
            if (levelMatch.Success && ushort.TryParse(levelMatch.Groups[1].Value, out ushort parsedLevel))
            {
                minLevel = parsedLevel;
            }

            byte targetFightMode = 1;
            Match fightMatch = SetFightStateRegex().Match(content);
            if (fightMatch.Success && byte.TryParse(fightMatch.Groups[1].Value, out byte parsedFight))
            {
                targetFightMode = parsedFight;
            }

            int bounceMpsX = 0;
            int bounceMpsY = 0;
            Match setPosMatch = SetPosRegex().Match(content);
            if (setPosMatch.Success &&
                int.TryParse(setPosMatch.Groups[1].Value, out int bounceCellX) &&
                int.TryParse(setPosMatch.Groups[2].Value, out int bounceCellY))
            {
                bounceMpsX = ToMpsX(bounceCellX);
                bounceMpsY = ToMpsY(bounceCellY);
            }

            string? deniedMessage = null;
            Match talkMatch = TalkRegex().Match(content);
            if (talkMatch.Success)
            {
                byte[] rawTalkBytes = HybridLegacyTextDecoder.UnescapeLuaStringBytes(
                    Encoding.Latin1.GetBytes(talkMatch.Groups[1].Value));
                deniedMessage = HybridLegacyTextDecoder.DecodeSmart(rawTalkBytes, preferVietnamese: true).Trim();
            }

            return new PortalTrapBindingTemplate(
                TrapId: primaryTrapId,
                ScriptPath: utf8ScriptPath,
                ActionKind: PortalTrapActionKind.ChangeMapPortal,
                TargetMapId: targetMapId,
                TargetMpsX: targetMpsX,
                TargetMpsY: targetMpsY,
                TargetFightMode: targetFightMode,
                MinRequiredLevel: minLevel,
                BounceBackMpsX: bounceMpsX,
                BounceBackMpsY: bounceMpsY,
                DeniedMessage: deniedMessage);
        }

        // 2. Town Gate Fight-State Toggle: SetPos(...) + SetFightState(...) without Include(...) or NewWorld(...)
        MatchCollection setPosMatches = SetPosRegex().Matches(content);
        if (!hasInclude && newWorldMatches.Count == 0)
        {
            if (setPosMatches.Count >= 2 &&
                int.TryParse(setPosMatches[0].Groups[1].Value, out int cellX0) &&
                int.TryParse(setPosMatches[0].Groups[2].Value, out int cellY0) &&
                int.TryParse(setPosMatches[1].Groups[1].Value, out int cellX1) &&
                int.TryParse(setPosMatches[1].Groups[2].Value, out int cellY1))
            {
                return new PortalTrapBindingTemplate(
                    TrapId: primaryTrapId,
                    ScriptPath: utf8ScriptPath,
                    ActionKind: PortalTrapActionKind.ToggleFightStateAndSetPos,
                    TargetMapId: 0,
                    TargetMpsX: ToMpsX(cellX0),
                    TargetMpsY: ToMpsY(cellY0),
                    TargetFightMode: 1,
                    AlternateMpsX: ToMpsX(cellX1),
                    AlternateMpsY: ToMpsY(cellY1),
                    AlternateFightMode: 0);
            }

            if (setPosMatches.Count == 1 &&
                int.TryParse(setPosMatches[0].Groups[1].Value, out int singleCellX) &&
                int.TryParse(setPosMatches[0].Groups[2].Value, out int singleCellY))
            {
                byte fightMode = 0;
                Match fightMatch = SetFightStateRegex().Match(content);
                if (fightMatch.Success && byte.TryParse(fightMatch.Groups[1].Value, out byte parsedFight))
                {
                    fightMode = parsedFight;
                }

                return new PortalTrapBindingTemplate(
                    TrapId: primaryTrapId,
                    ScriptPath: utf8ScriptPath,
                    ActionKind: PortalTrapActionKind.ToggleFightStateAndSetPos,
                    TargetMapId: 0,
                    TargetMpsX: ToMpsX(singleCellX),
                    TargetMpsY: ToMpsY(singleCellY),
                    TargetFightMode: fightMode,
                    AlternateMpsX: ToMpsX(singleCellX),
                    AlternateMpsY: ToMpsY(singleCellY),
                    AlternateFightMode: fightMode);
            }
        }

        // 3. Any other script (Include-based traps, Song-Jin signup, multi-branch random mazes, dialog cliff traps)
        return new PortalTrapBindingTemplate(
            TrapId: primaryTrapId,
            ScriptPath: utf8ScriptPath,
            ActionKind: PortalTrapActionKind.CustomScript,
            TargetMapId: 0,
            TargetMpsX: 0,
            TargetMpsY: 0,
            TargetFightMode: 0);
    }

    private static int ToMpsX(int coordX) =>
        coordX < 8192 ? coordX * WorldCoordinateConsts.CellWidthMps : coordX;

    private static int ToMpsY(int coordY) =>
        coordY < 16384 ? coordY * WorldCoordinateConsts.CellHeightMps : coordY;

    [GeneratedRegex(@"\b(?:Include|dofile)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex IncludeCallRegex();

    [GeneratedRegex(@"NewWorld\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NewWorldRegex();

    [GeneratedRegex(@"SetPos\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SetPosRegex();

    [GeneratedRegex(@"SetFightState\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SetFightStateRegex();

    [GeneratedRegex(@"GetLevel\s*\(\s*\)\s*>=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GetLevelRegex();

    [GeneratedRegex(@"Talk\s*\(\s*\d+\s*,\s*""[^""]*""\s*,\s*""([^""]+)""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TalkRegex();
}
