using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Primitives.Quests;

namespace JXReborn.LegacyImporter.Exporters;

/// <summary>
/// Exports all canonical Dã Tẩu (<c>TaskLink</c>) settings tables (<c>settings/task/*.txt</c>)
/// and map entrance table (<c>script/task/newtask/map_index.lua</c>) into
/// <c>data/quests/da-tau.quests.json</c> per <c>JX Reborn Data Standard v1</c>.
/// </summary>
public sealed partial class QuestDataExporter
{
    public (int TreasurePositions, int MilestoneAwards, int BasicAwards, int BuyGoods, int FindGoods, int UpGround, int KillMonsters) ExportQuests(
        ILegacyServerSource source,
        string outputDataDirectory,
        ExportCatalogRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        registry ??= new ExportCatalogRegistry();

        string questsOutDir = Path.Combine(outputDataDirectory, "quests");
        Directory.CreateDirectory(questsOutDir);

        List<Dictionary<string, object?>> treasurePositions = ExportTreasurePositions(source);
        List<Dictionary<string, object?>> milestoneAwards = ExportMilestoneAwards(source);
        List<Dictionary<string, object?>> basicAwards = ExportBasicAwards(source, @"settings\task\award_basic.txt");
        List<Dictionary<string, object?>> loopAwards = ExportBasicAwards(source, @"settings\task\award_loop.txt");
        List<Dictionary<string, object?>> buyGoodsTasks = ExportBuyGoods(source);
        List<Dictionary<string, object?>> findGoodsTasks = ExportFindGoods(source);
        List<Dictionary<string, object?>> showGoodsTasks = ExportShowGoods(source);
        List<Dictionary<string, object?>> findMapsTasks = ExportFindMaps(source);
        List<Dictionary<string, object?>> upGroundTasks = ExportUpGround(source);
        List<Dictionary<string, object?>> worldMapsTasks = ExportWorldMaps(source);
        List<Dictionary<string, object?>> killMonsterTasks = ExportKillMonsters(source);
        List<Dictionary<string, object?>> levelLinks = ExportLevelLinks(source);
        List<Dictionary<string, object?>> mainLinkRates = ExportMainLinkRates(source);
        List<Dictionary<string, object?>> mapTraps = ExportMapTraps(source);

        registry.RecordCount("quests.treasurePositions", treasurePositions.Count, treasurePositions.Count);
        registry.RecordCount("quests.milestoneAwards", milestoneAwards.Count, milestoneAwards.Count);
        registry.RecordCount("quests.basicAwards", basicAwards.Count, basicAwards.Count);
        registry.RecordCount("quests.loopAwards", loopAwards.Count, loopAwards.Count);
        registry.RecordCount("quests.buyGoodsTasks", buyGoodsTasks.Count, buyGoodsTasks.Count);
        registry.RecordCount("quests.findGoodsTasks", findGoodsTasks.Count, findGoodsTasks.Count);
        registry.RecordCount("quests.showGoodsTasks", showGoodsTasks.Count, showGoodsTasks.Count);
        registry.RecordCount("quests.findMapsTasks", findMapsTasks.Count, findMapsTasks.Count);
        registry.RecordCount("quests.upGroundTasks", upGroundTasks.Count, upGroundTasks.Count);
        registry.RecordCount("quests.worldMapsTasks", worldMapsTasks.Count, worldMapsTasks.Count);
        registry.RecordCount("quests.killMonsterTasks", killMonsterTasks.Count, killMonsterTasks.Count);
        registry.RecordCount("quests.levelLinks", levelLinks.Count, levelLinks.Count);
        registry.RecordCount("quests.mainLinkRates", mainLinkRates.Count, mainLinkRates.Count);
        registry.RecordCount("quests.mapTraps", mapTraps.Count, mapTraps.Count);

        var rootDoc = new Dictionary<string, object?>
        {
            ["$schema"] = "../../schemas/quest-catalog.schema.json",
            ["schemaVersion"] = "1.0.0",
            ["treasurePositions"] = treasurePositions,
            ["milestoneAwards"] = milestoneAwards,
            ["basicAwards"] = basicAwards,
            ["loopAwards"] = loopAwards,
            ["buyGoodsTasks"] = buyGoodsTasks,
            ["findGoodsTasks"] = findGoodsTasks,
            ["showGoodsTasks"] = showGoodsTasks,
            ["findMapsTasks"] = findMapsTasks,
            ["upGroundTasks"] = upGroundTasks,
            ["worldMapsTasks"] = worldMapsTasks,
            ["killMonsterTasks"] = killMonsterTasks,
            ["levelLinks"] = levelLinks,
            ["mainLinkRates"] = mainLinkRates,
            ["mapTraps"] = mapTraps,
        };

        LegacyPathResolver.WriteCanonicalJson(Path.Combine(questsOutDir, "da-tau.quests.json"), rootDoc);

        return (
            treasurePositions.Count,
            milestoneAwards.Count,
            basicAwards.Count,
            buyGoodsTasks.Count,
            findGoodsTasks.Count,
            upGroundTasks.Count,
            killMonsterTasks.Count);
    }

    private static List<Dictionary<string, object?>> ExportTreasurePositions(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\random\trea_pos.txt");
        if (table is null)
        {
            return list;
        }

        int rowId = 1;
        for (int r = 2; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 6)
            {
                continue;
            }

            if (TryReadInt(table, r, 1, out int mapId) &&
                TryReadInt(table, r, 2, out int worldX) &&
                TryReadInt(table, r, 3, out int worldY) &&
                TryReadInt(table, r, 4, out int mapX) &&
                TryReadInt(table, r, 5, out int mapY))
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["rowId"] = rowId++,
                    ["mapName"] = ReadPreserveTrailingSpaceText(table, r, 0),
                    ["mapId"] = mapId,
                    ["worldX"] = worldX,
                    ["worldY"] = worldY,
                    ["mapX"] = mapX,
                    ["mapY"] = mapY,
                    ["description"] = table.GetColumnCount(r) > 6 ? ReadPreserveTrailingSpaceText(table, r, 6) : string.Empty,
                    ["sprPath"] = table.GetColumnCount(r) > 7 ? ReadRawAsciiOrText(table, r, 7) : string.Empty,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportMilestoneAwards(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\award_link.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 9)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int step) &&
                TryReadInt(table, r, 2, out int quality) &&
                TryReadInt(table, r, 3, out int genre) &&
                TryReadInt(table, r, 4, out int detail) &&
                TryReadInt(table, r, 5, out int particular) &&
                TryReadInt(table, r, 6, out int level) &&
                TryReadInt(table, r, 7, out int goodsFive) &&
                TryReadInt(table, r, 8, out int magicLevel))
            {
                long taskValue = table.GetColumnCount(r) > 9 && TryReadLong(table, r, 9, out long tv) ? tv : 10000L;
                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["milestoneStep"] = step,
                    ["name"] = ReadPreserveTrailingSpaceText(table, r, 1),
                    ["quality"] = quality,
                    ["genre"] = genre,
                    ["detail"] = detail,
                    ["particular"] = particular,
                    ["level"] = level,
                    ["goodsFive"] = goodsFive,
                    ["magicLevel"] = magicLevel,
                    ["taskValue"] = taskValue,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportBasicAwards(ILegacyServerSource source, string relativePath)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, relativePath);
        if (table is null)
        {
            return list;
        }

        int id = 1;
        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 8)
            {
                continue;
            }

            if (TryReadInt(table, r, 1, out int quality) &&
                TryReadInt(table, r, 2, out int genre) &&
                TryReadInt(table, r, 3, out int detail) &&
                TryReadInt(table, r, 4, out int particular) &&
                TryReadInt(table, r, 5, out int goodsFive) &&
                TryReadInt(table, r, 6, out int level))
            {
                int magicLevel = TryReadInt(table, r, 7, out int ml) ? ml : 0;
                long taskValue = table.GetColumnCount(r) > 8 && TryReadLong(table, r, 8, out long tv) ? tv : 0L;
                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["awardId"] = id++,
                    ["name"] = ReadPreserveTrailingSpaceText(table, r, 0),
                    ["awardKind"] = (int)DaTauAwardKind.Item,
                    ["quality"] = quality,
                    ["genre"] = genre,
                    ["detail"] = detail,
                    ["particular"] = particular,
                    ["goodsFive"] = goodsFive,
                    ["level"] = level,
                    ["magicLevel"] = magicLevel,
                    ["taskValue"] = taskValue,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportBuyGoods(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\tasklink_buygoods.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 6)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskId) &&
                TryReadInt(table, r, 1, out int genre) &&
                TryReadInt(table, r, 2, out int detail) &&
                TryReadInt(table, r, 3, out int particular) &&
                TryReadByte(table, r, 4, out byte level) &&
                TryReadByte(table, r, 5, out byte goodsFive))
            {
                int taskValue1 = TryReadInt(table, r, 6, out int tv1) ? tv1 : 0;
                int taskValue2 = TryReadInt(table, r, 7, out int tv2) ? tv2 : 10000;
                int[] taskRates = ReadRateVector20(table, r, startCol: 8);

                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["taskId"] = taskId,
                    ["genre"] = genre,
                    ["detail"] = detail,
                    ["particular"] = particular,
                    ["level"] = (int)level,
                    ["goodsFive"] = (int)goodsFive,
                    ["shopName"] = table.GetColumnCount(r) > 28 ? ReadPreserveTrailingSpaceText(table, r, 28) : string.Empty,
                    ["itemName"] = table.GetColumnCount(r) > 29 ? ReadPreserveTrailingSpaceText(table, r, 29) : string.Empty,
                    ["taskValue1"] = taskValue1,
                    ["taskValue2"] = taskValue2,
                    ["taskRates"] = taskRates,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportFindGoods(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\tasklink_findgoods.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 10)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskId) &&
                TryReadInt(table, r, 1, out int genre) &&
                TryReadInt(table, r, 2, out int detail) &&
                TryReadInt(table, r, 3, out int particular))
            {
                // In tasklink_head.lua lines 313-327: "n" means wildcard (any level / any series -> 255)
                byte level = TryReadByte(table, r, 4, out byte lvl) ? lvl : (byte)255;
                byte goodsFive = TryReadByte(table, r, 5, out byte gf) ? gf : (byte)255;
                string magicEnName = ReadRawAsciiOrText(table, r, 6);
                string magicCnName = ReadPreserveTrailingSpaceText(table, r, 7);
                int minVal = TryReadInt(table, r, 8, out int mn) ? mn : 0;
                int maxVal = TryReadInt(table, r, 9, out int mx) ? mx : 0;
                int taskValue1 = TryReadInt(table, r, 10, out int tv1) ? tv1 : 10000;
                int taskValue2 = TryReadInt(table, r, 11, out int tv2) ? tv2 : 250000;
                int[] taskRates = ReadRateVector20(table, r, startCol: 12);
                string itemName = table.GetColumnCount(r) > 32 ? ReadPreserveTrailingSpaceText(table, r, 32) : string.Empty;

                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["taskId"] = taskId,
                    ["genre"] = genre,
                    ["detail"] = detail,
                    ["particular"] = particular,
                    ["level"] = (int)level,
                    ["goodsFive"] = (int)goodsFive,
                    ["magicEnName"] = magicEnName,
                    ["magicCnName"] = magicCnName,
                    ["minValue"] = minVal,
                    ["maxValue"] = maxVal,
                    ["itemName"] = itemName,
                    ["taskValue1"] = taskValue1,
                    ["taskValue2"] = taskValue2,
                    ["taskRates"] = taskRates,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportShowGoods(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\tasklink_showgoods.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 8)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskId))
            {
                string magicEnName = ReadRawAsciiOrText(table, r, 2);
                string magicCnName = ReadPreserveTrailingSpaceText(table, r, 3);
                int minVal = TryReadInt(table, r, 4, out int mn) ? mn : 0;
                int maxVal = TryReadInt(table, r, 5, out int mx) ? mx : 0;
                int taskValue1 = TryReadInt(table, r, 6, out int tv1) ? tv1 : 0;
                int taskValue2 = TryReadInt(table, r, 7, out int tv2) ? tv2 : 20000;
                int[] taskRates = ReadRateVector20(table, r, startCol: 8);

                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["taskId"] = taskId,
                    ["magicEnName"] = magicEnName,
                    ["magicCnName"] = magicCnName,
                    ["minValue"] = minVal,
                    ["maxValue"] = maxVal,
                    ["taskValue1"] = taskValue1,
                    ["taskValue2"] = taskValue2,
                    ["taskRates"] = taskRates,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportFindMaps(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\tasklink_findmaps.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 7)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskId) &&
                TryReadInt(table, r, 1, out int mapId) &&
                TryReadInt(table, r, 3, out int requiredCount) &&
                TryReadInt(table, r, 4, out int mapType))
            {
                string mapName = ReadPreserveTrailingSpaceText(table, r, 2);
                int taskValue1 = TryReadInt(table, r, 5, out int tv1) ? tv1 : 0;
                int taskValue2 = TryReadInt(table, r, 6, out int tv2) ? tv2 : 500000;
                int[] taskRates = ReadRateVector20(table, r, startCol: 7);

                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["taskId"] = taskId,
                    ["mapId"] = mapId,
                    ["mapName"] = mapName,
                    ["requiredCount"] = requiredCount,
                    ["mapType"] = Math.Clamp(mapType, 1, 2),
                    ["taskValue1"] = taskValue1,
                    ["taskValue2"] = taskValue2,
                    ["taskRates"] = taskRates,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportUpGround(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\tasklink_upground.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 3)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskId) &&
                TryReadByte(table, r, 1, out byte numType) &&
                TryReadInt(table, r, 2, out int delta))
            {
                int taskValue1 = TryReadInt(table, r, 3, out int tv1) ? tv1 : 0;
                int taskValue2 = TryReadInt(table, r, 4, out int tv2) ? tv2 : 20000;
                int[] taskRates = ReadRateVector20(table, r, startCol: 5);

                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["taskId"] = taskId,
                    ["numericType"] = (int)numType,
                    ["targetDelta"] = delta,
                    ["taskValue1"] = taskValue1,
                    ["taskValue2"] = taskValue2,
                    ["taskRates"] = taskRates,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportWorldMaps(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\tasklink_worldmaps.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 4)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskId) &&
                TryReadInt(table, r, 1, out int requiredCount))
            {
                int taskValue1 = TryReadInt(table, r, 2, out int tv1) ? tv1 : 0;
                int taskValue2 = TryReadInt(table, r, 3, out int tv2) ? tv2 : 15000;
                int[] taskRates = ReadRateVector20(table, r, startCol: 4);

                list.Add(new Dictionary<string, object?>
                {
                    ["tabRowIndex"] = r + 1,
                    ["taskId"] = taskId,
                    ["requiredCount"] = requiredCount,
                    ["taskValue1"] = taskValue1,
                    ["taskValue2"] = taskValue2,
                    ["taskRates"] = taskRates,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportKillMonsters(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\killmostert.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 6)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskId) &&
                TryReadInt(table, r, 2, out int mapId) &&
                TryReadInt(table, r, 5, out int killCount))
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["mapId"] = mapId,
                    ["mapName"] = table.GetString(r, 3),
                    ["monsterName"] = table.GetString(r, 4),
                    ["killCount"] = killCount,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportLevelLinks(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\levellink.txt");
        if (table is null)
        {
            return list;
        }

        var rows = new List<(int MinLevel, int StartLink)>();
        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 2)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int minLevel) && TryReadInt(table, r, 1, out int startLink))
            {
                rows.Add((minLevel, startLink));
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            int endLink = i + 1 < rows.Count ? rows[i + 1].StartLink - 1 : 20;
            list.Add(new Dictionary<string, object?>
            {
                ["minPlayerLevel"] = rows[i].MinLevel,
                ["startChainLevel"] = rows[i].StartLink,
                ["endChainLevel"] = endLink,
            });
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportMainLinkRates(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\task\tasklink_mainlink.txt");
        if (table is null)
        {
            return list;
        }

        for (int r = 1; r < table.LineCount; r++)
        {
            if (table.IsRowCompletelyBlankOrTabsOnly(r) || table.GetColumnCount(r) < 21)
            {
                continue;
            }

            if (TryReadInt(table, r, 0, out int taskType) && taskType is >= 1 and <= 6)
            {
                int[] rates = ReadRateVector20(table, r, startCol: 1);
                list.Add(new Dictionary<string, object?>
                {
                    ["taskType"] = taskType,
                    ["levelRates"] = rates,
                });
            }
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ExportMapTraps(ILegacyServerSource source)
    {
        var list = new List<Dictionary<string, object?>>();
        if (!source.TryReadFile(@"script\task\newtask\map_index.lua", out byte[]? rawBytes) || rawBytes is null)
        {
            return list;
        }

        int pos = 0;
        while (pos < rawBytes.Length)
        {
            int lineEnd = Array.IndexOf(rawBytes, (byte)'\n', pos);
            if (lineEnd < 0)
            {
                lineEnd = rawBytes.Length;
            }

            ReadOnlySpan<byte> lineSpan = rawBytes.AsSpan(pos, lineEnd - pos);
            if (!lineSpan.IsEmpty && lineSpan[^1] == (byte)'\r')
            {
                lineSpan = lineSpan[..^1];
            }

            pos = lineEnd + 1;

            int openBrace = lineSpan.IndexOf((byte)'{');
            int closeBrace = lineSpan.LastIndexOf((byte)'}');
            if (openBrace < 0 || closeBrace <= openBrace)
            {
                continue;
            }

            int firstQuote = lineSpan.IndexOf((byte)'"');
            int secondQuote = firstQuote >= 0 ? lineSpan[(firstQuote + 1)..].IndexOf((byte)'"') : -1;
            if (firstQuote < openBrace || secondQuote < 0)
            {
                continue;
            }

            secondQuote += firstQuote + 1;
            string prefix = Encoding.Latin1.GetString(lineSpan[(openBrace + 1)..firstQuote]);
            int commaIdx = prefix.IndexOf(',');
            if (commaIdx < 0 || !int.TryParse(prefix[..commaIdx].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
            {
                continue;
            }

            ReadOnlySpan<byte> nameBytes = UnescapeLuaStringLiteral(lineSpan[(firstQuote + 1)..secondQuote]);
            string mapName = HybridLegacyTextDecoder.DecodeSmart(nameBytes, preferVietnamese: true);

            string suffix = Encoding.Latin1.GetString(lineSpan[(secondQuote + 1)..closeBrace]);
            Match coordsMatch = MapTrapCoordsRegex().Match(suffix);
            if (!coordsMatch.Success ||
                !int.TryParse(coordsMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileX) ||
                !int.TryParse(coordsMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileY))
            {
                continue;
            }

            list.Add(new Dictionary<string, object?>
            {
                ["mapId"] = mapId,
                ["mapName"] = mapName,
                ["tileX"] = tileX,
                ["tileY"] = tileY,
            });
        }

        return list;
    }

    private static byte[] UnescapeLuaStringLiteral(ReadOnlySpan<byte> escaped)
    {
        using var ms = new MemoryStream(escaped.Length);
        for (int i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == (byte)'\\' && i + 1 < escaped.Length)
            {
                byte next = escaped[i + 1];
                if (next is >= (byte)'0' and <= (byte)'9')
                {
                    int val = 0;
                    int digits = 0;
                    while (digits < 3 && i + 1 + digits < escaped.Length &&
                           escaped[i + 1 + digits] is >= (byte)'0' and <= (byte)'9')
                    {
                        val = (val * 10) + (escaped[i + 1 + digits] - '0');
                        digits++;
                    }

                    ms.WriteByte((byte)(val & 0xFF));
                    i += digits;
                    continue;
                }

                ms.WriteByte(next);
                i++;
                continue;
            }

            ms.WriteByte(escaped[i]);
        }

        return ms.ToArray();
    }

    [GeneratedRegex(@",\s*(\d+)\s*,\s*(\d+)")]
    private static partial Regex MapTrapCoordsRegex();

    private static int[] ReadRateVector20(KTabFileTable table, int row, int startCol)
    {
        var rates = new int[20];
        int colCount = table.GetColumnCount(row);
        for (int i = 0; i < 20; i++)
        {
            int col = startCol + i;
            if (col < colCount && TryReadInt(table, row, col, out int val) && val > 0)
            {
                rates[i] = val;
            }
        }

        return rates;
    }

    private static bool TryReadInt(KTabFileTable table, int row, int col, out int value)
    {
        string text = Encoding.Latin1.GetString(table.GetRawCell(row, col)).Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadLong(KTabFileTable table, int row, int col, out long value)
    {
        string text = Encoding.Latin1.GetString(table.GetRawCell(row, col)).Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadByte(KTabFileTable table, int row, int col, out byte value)
    {
        string text = Encoding.Latin1.GetString(table.GetRawCell(row, col)).Trim();
        return byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string ReadRawAsciiOrText(KTabFileTable table, int row, int col)
    {
        ReadOnlySpan<byte> raw = table.GetRawCell(row, col);
        return raw.IsEmpty ? string.Empty : HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese: true).Trim();
    }

    private static string ReadPreserveTrailingSpaceText(KTabFileTable table, int row, int col)
    {
        ReadOnlySpan<byte> raw = table.GetRawCell(row, col);
        while (!raw.IsEmpty && raw[^1] is (byte)'\r' or (byte)'\n')
        {
            raw = raw[..^1];
        }

        return raw.IsEmpty ? string.Empty : HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese: true);
    }
}
