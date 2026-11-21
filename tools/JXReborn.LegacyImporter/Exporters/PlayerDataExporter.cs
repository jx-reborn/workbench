using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;

namespace JXReborn.LegacyImporter.Exporters;

/// <summary>
/// Exports starter player templates (<c>newplayerini00..09.ini</c>), series growth rules (<c>level_add.txt</c>),
/// five-elements matrix, level &amp; leadership experience tables (<c>level_exp.txt</c>, <c>level_lead_exp.txt</c>),
/// factions &amp; rank titles (<c>门派设定.ini</c>, <c>factiontitle.txt</c>), starter villages (<c>nativeplacelist.ini</c>,
/// <c>revivalid.ini</c>, <c>revivepos.ini</c>), PK rules (<c>pkpunish.txt</c>), meridians (<c>meridian.txt</c>,
/// <c>meridian_level.txt</c>), stamina rules (<c>stamina.ini</c>), team resist auras (<c>team_resist_effect.ini</c>),
/// and Tong progression (<c>tong_level_data.txt</c>, <c>workshops.txt</c>, <c>tongstunt_setting.txt</c>)
/// into <c>data/players/progression.players.json</c> per <c>JX Reborn Data Standard v1</c>.
/// </summary>
public sealed class PlayerDataExporter
{
    private static readonly string[] SeriesSlugs = ["metal", "wood", "water", "fire", "earth"];
    private static readonly string[] GenderSlugs = ["male", "female"];
    private static readonly string[] TeamGrantedResistTypes = ["physics", "poison", "cold", "fire", "light"];

    public int ExportPlayers(ILegacyServerSource source, string outputDataDirectory, ExportCatalogRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        registry ??= new ExportCatalogRegistry();

        string playersOutDir = Path.Combine(outputDataDirectory, "players");
        Directory.CreateDirectory(playersOutDir);

        string obsoleteFile = Path.Combine(playersOutDir, "players.players.json");
        if (File.Exists(obsoleteFile))
        {
            File.Delete(obsoleteFile);
        }

        List<Dictionary<string, object?>> characterTemplates = ExportCharacterTemplates(source, registry);
        List<Dictionary<string, object?>> seriesGrowthRules = ExportSeriesGrowthRules(source, registry);
        var fiveElementsMatrix = new Dictionary<string, object?>
        {
            ["generates"] = new[] { 2, 3, 1, 4, 0 },
            ["conquers"] = new[] { 1, 4, 3, 0, 2 },
            ["conqueredDamageBonusPercent"] = 20,
            ["conqueredDamagePenaltyPercent"] = -20,
        };
        List<Dictionary<string, object?>> levelExpTable = ExportLevelExpTable(source, registry);
        List<Dictionary<string, object?>> factions = ExportFactions(source, registry);
        List<Dictionary<string, object?>> starterVillages = ExportStarterVillages(source, registry);
        Dictionary<string, object?> pkRules = ExportPkRules(source, registry);
        List<Dictionary<string, object?>> meridians = ExportMeridians(source, registry);
        Dictionary<string, object?> staminaRules = ExportStaminaRules(source);
        List<Dictionary<string, object?>> teamResistEffects = ExportTeamResistEffects(source, registry);
        Dictionary<string, object?> tongConfig = ExportTongConfig(source, registry);
        List<Dictionary<string, object?>> chatCosts = ExportChatCosts(source, registry);
        List<string> prohibitedRoleNamePatterns = ExportProhibitedRoleNamePatterns(source);

        var rootDoc = new Dictionary<string, object?>
        {
            ["$schema"] = "../../schemas/player-template.schema.json",
            ["schemaVersion"] = "1.0.0",
            ["characterTemplates"] = characterTemplates,
            ["seriesGrowthRules"] = seriesGrowthRules,
            ["fiveElementsMatrix"] = fiveElementsMatrix,
            ["levelExpTable"] = levelExpTable,
            ["factions"] = factions,
            ["starterVillages"] = starterVillages,
            ["pkRules"] = pkRules,
            ["meridians"] = meridians,
            ["staminaRules"] = staminaRules,
            ["teamResistEffects"] = teamResistEffects,
            ["tongConfig"] = tongConfig,
            ["chatCosts"] = chatCosts,
            ["prohibitedRoleNamePatterns"] = prohibitedRoleNamePatterns,
        };

        LegacyPathResolver.WriteCanonicalJson(Path.Combine(playersOutDir, "progression.players.json"), rootDoc);
        return characterTemplates.Count;
    }

    private static List<Dictionary<string, object?>> ExportCharacterTemplates(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var list = new List<Dictionary<string, object?>>(10);
        int legacyFound = 0;

        for (int seriesIdx = 0; seriesIdx < 5; seriesIdx++)
        {
            for (int genderIdx = 0; genderIdx < 2; genderIdx++)
            {
                int tplId = (seriesIdx * 2) + genderIdx;
                if (!source.TryReadServerFile($@"settings\npc\player\newplayerini{tplId:D2}.ini", out byte[]? iniBytes))
                {
                    continue;
                }

                legacyFound++;
                Dictionary<string, Dictionary<string, byte[]>> secs = ParseRawIniSections(iniBytes);
                secs.TryGetValue("ROLE", out Dictionary<string, byte[]>? roleSec);
                roleSec ??= new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                int baseStr = Math.Max(0, GetIniInt(roleSec, "ipower", 25));
                int baseDex = Math.Max(0, GetIniInt(roleSec, "iagility", 25));
                int baseVit = Math.Max(0, GetIniInt(roleSec, "iouter", 25));
                int baseEng = Math.Max(0, GetIniInt(roleSec, "iinside", 25));
                int baseLucky = Math.Max(0, GetIniInt(roleSec, "iluck", 0));
                int initLife = Math.Max(1, GetIniInt(roleSec, "imaxlife", 100));
                int initMana = Math.Max(1, GetIniInt(roleSec, "imaxinner", 100));
                int initStamina = Math.Max(1, GetIniInt(roleSec, "imaxstamina", 100));

                var starterSkills = new List<Dictionary<string, object?>>();
                if (secs.TryGetValue("FSKILLS", out Dictionary<string, byte[]>? fskillsSec))
                {
                    int skillCount = GetIniInt(fskillsSec, "COUNT", 0);
                    var seenSkillIds = new HashSet<int>();
                    for (int sIdx = 1; sIdx <= skillCount; sIdx++)
                    {
                        int sid = GetIniInt(fskillsSec, $"S{sIdx}", 0);
                        int lvl = Math.Max(1, GetIniInt(fskillsSec, $"L{sIdx}", 1));
                        if (sid >= 1 && seenSkillIds.Add(sid))
                        {
                            var skillObj = new Dictionary<string, object?>
                            {
                                ["skillId"] = sid,
                            };
                            if (registry.SkillCodeById.TryGetValue(sid, out string? sc))
                            {
                                skillObj["skillCode"] = sc;
                            }

                            skillObj["level"] = lvl;
                            starterSkills.Add(skillObj);
                        }
                    }
                }

                var starterItems = new List<Dictionary<string, object?>>();

                if (secs.TryGetValue("ITEMS", out Dictionary<string, byte[]>? itemsHeaderSec))
                {
                    int itemCount = GetIniInt(itemsHeaderSec, "COUNT", 0);
                    for (int itemIdx = 1; itemIdx <= itemCount; itemIdx++)
                    {
                        if (!secs.TryGetValue($"ITEM{itemIdx}", out Dictionary<string, byte[]>? itemSec))
                        {
                            continue;
                        }

                        int genre = GetIniInt(itemSec, "iequipclasscode", 0);
                        int detail = GetIniInt(itemSec, "idetailtype", 0);
                        int part = GetIniInt(itemSec, "iparticulartype", 0);
                        int lvl = Math.Max(1, GetIniInt(itemSec, "ilevel", 1));
                        int local = GetIniInt(itemSec, "ilocal", 3);
                        int ix = Math.Max(0, GetIniInt(itemSec, "ix", 0));
                        int iy = Math.Max(0, GetIniInt(itemSec, "iy", 0));
                        int itemSeries = Math.Clamp(GetIniInt(itemSec, "iseries", -1), -1, 4);
                        int equipVersion = Math.Max(0, GetIniInt(itemSec, "iequipversion", 4));
                        uint randSeed = (uint)Math.Max(0, GetIniInt(itemSec, "irandseed", 0));

                        string? resolvedItemCode = null;
                        if (registry.ItemCodeByTupleAndLevel.TryGetValue((genre, detail, part, lvl), out string? exactCode))
                        {
                            resolvedItemCode = exactCode;
                        }
                        else if (registry.PrimaryItemCodeByTuple.TryGetValue((genre, detail, part), out string? primaryCode))
                        {
                            resolvedItemCode = primaryCode;
                        }

                        if (!string.IsNullOrEmpty(resolvedItemCode))
                        {
                            string container = local switch
                            {
                                1 => "equip",
                                6 => "immediacy",
                                _ => "equipRoom",
                            };

                            int effectiveSeries = itemSeries;
                            starterItems.Add(new Dictionary<string, object?>
                            {
                                ["itemCode"] = resolvedItemCode,
                                ["container"] = container,
                                ["gridX"] = ix,
                                ["gridY"] = iy,
                                ["stackCount"] = 1,
                                ["elementSeries"] = effectiveSeries,
                                ["generatorVersion"] = equipVersion,
                                ["randomSeed"] = randSeed,
                            });
                        }
                    }
                }

                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = tplId,
                    ["code"] = $"starter-{SeriesSlugs[seriesIdx]}-{GenderSlugs[genderIdx]}",
                    ["elementSeries"] = seriesIdx,
                    ["gender"] = genderIdx,
                    ["baseStrength"] = baseStr,
                    ["baseDexterity"] = baseDex,
                    ["baseVitality"] = baseVit,
                    ["baseEnergy"] = baseEng,
                    ["baseLucky"] = baseLucky,
                    ["initialLifeMax"] = initLife,
                    ["initialManaMax"] = initMana,
                    ["initialStaminaMax"] = initStamina,
                    ["starterSkills"] = starterSkills,
                    ["starterItems"] = starterItems,
                });
            }
        }

        registry.RecordCount("players.characterTemplates", legacyFound, list.Count);
        return list;
    }

    private static List<Dictionary<string, object?>> ExportSeriesGrowthRules(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var list = new List<Dictionary<string, object?>>(5);
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\npc\player\level_add.txt");
        if (table is null)
        {
            return list;
        }

        int rows = Math.Min(5, table.DataRowCount);
        for (int r = 1; r <= rows; r++)
        {
            int series = r - 1;
            string name = table.GetString(r, 0, preferVietnamese: true).Trim();
            var props = new Dictionary<string, object?>
            {
                ["elementSeries"] = series,
                ["code"] = $"growth-{SeriesSlugs[series]}",
                ["name"] = name.Length > 0 ? name : SeriesSlugs[series],
                ["lifePerLevel"] = table.GetInt(r, 1, 10),
                ["manaPerLevel"] = table.GetInt(r, 4, 5),
                ["staminaMalePerLevel"] = table.GetInt(r, 2, 4),
                ["staminaFemalePerLevel"] = table.GetInt(r, 3, 4),
                ["lifePerVitality"] = table.GetInt(r, 5, 5),
                ["manaPerEnergy"] = table.GetInt(r, 7, 3),
                ["staminaPerVitality"] = table.GetInt(r, 6, 2),
                ["leadExpShare"] = table.GetInt(r, 8, 25),
                ["baseFireResist"] = table.GetInt(r, 9, 0),
                ["baseColdResist"] = table.GetInt(r, 10, 0),
                ["basePoisonResist"] = table.GetInt(r, 11, 0),
                ["baseLightResist"] = table.GetInt(r, 12, 0),
                ["basePhysicsResist"] = table.GetInt(r, 13, 0),
                ["staminaMaleBase"] = table.GetInt(r, 14, 180),
                ["staminaFemaleBase"] = table.GetInt(r, 15, 180),
                ["attributePointsPerLevel"] = 5,
                ["skillPointsPerLevel"] = 1,
            };
            list.Add(props);
        }

        registry.RecordCount("players.seriesGrowthRules", rows, list.Count);
        return list;
    }

    private static List<Dictionary<string, object?>> ExportLevelExpTable(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var leadExpByLevel = new Dictionary<int, long>();
        int legacyLeadRows = 0;
        if (KTabFileTable.TryLoad(source, @"settings\npc\player\level_lead_exp.txt") is { } leadTable)
        {
            for (int r = 1; r <= leadTable.DataRowCount; r++)
            {
                int lvl = leadTable.GetInt(r, 0, 0);
                long exp = Math.Max(0L, leadTable.GetLong(r, 1, 0L));
                if (lvl >= 1)
                {
                    legacyLeadRows++;
                    leadExpByLevel[lvl] = exp;
                }
            }
        }

        var list = new List<Dictionary<string, object?>>();
        int legacyExpRows = 0;
        int exportedLeadRows = 0;
        if (KTabFileTable.TryLoad(source, @"settings\npc\player\level_exp.txt") is { } expTable)
        {
            for (int r = 1; r <= expTable.DataRowCount; r++)
            {
                int lvl = expTable.GetInt(r, 0, 0);
                if (lvl < 1 || lvl > 255)
                {
                    continue;
                }

                legacyExpRows++;
                long exp = Math.Max(0L, expTable.GetLong(r, 1, 0L));
                var entry = new Dictionary<string, object?>
                {
                    ["level"] = lvl,
                    ["expRequired"] = exp,
                };
                if (leadExpByLevel.TryGetValue(lvl, out long leadExp))
                {
                    entry["leadExpRequired"] = leadExp;
                    exportedLeadRows++;
                }

                list.Add(entry);
            }
        }

        registry.RecordCount("players.levelExpTable", legacyExpRows, list.Count);
        registry.RecordCount("players.levelLeadExpTable", legacyLeadRows, exportedLeadRows);
        return list;
    }

    private static readonly Dictionary<int, int[]> CanonicalFactionBaseSkillIds = new()
    {
        [0] = [10, 14, 4, 6, 8, 15, 16, 20, 11, 19, 271, 21, 273],
        [1] = [29, 30, 34, 23, 24, 26, 33, 31, 35, 37, 40, 42, 32, 36, 41, 324],
        [2] = [45, 43, 347, 303, 47, 50, 54, 343, 345, 349, 48, 58, 249, 341],
        [3] = [63, 65, 60, 62, 67, 66, 70, 64, 68, 69, 384, 73, 356, 72, 71, 74, 75],
        [4] = [80, 85, 77, 79, 93, 82, 89, 385, 86, 92, 88, 91, 252, 282],
        [5] = [99, 102, 95, 97, 269, 105, 113, 100, 109, 108, 111, 114],
        [6] = [119, 122, 115, 116, 129, 124, 274, 277, 125, 128, 130, 360],
        [7] = [135, 145, 131, 132, 136, 137, 138, 140, 141, 364, 143, 142, 148, 150],
        [8] = [153, 155, 151, 152, 159, 158, 164, 160, 157, 165, 166, 267],
        [9] = [169, 179, 167, 168, 171, 392, 174, 172, 173, 178, 393, 175, 181, 90, 176, 182, 275, 630],
        [10] = [1347, 1372, 1349, 1374, 1350, 1375, 1351, 1376, 1354, 1378, 1355, 1379, 1358, 1360, 1380],
    };

    private static int MapFactionTitleFactionId(int factionOneBased) => factionOneBased switch
    {
        1 => 0,   // Thiếu Lâm
        2 => 8,   // Võ Đang
        3 => 4,   // Nga My
        4 => 9,   // Côn Lôn
        5 => 2,   // Đường Môn
        6 => 5,   // Thúy Yên
        7 => 6,   // Cái Bang
        8 => 1,   // Thiên Vương
        9 => 3,   // Ngũ Độc
        10 => 7,  // Thiên Nhẫn
        _ => -1,
    };

    private static List<Dictionary<string, object?>> ExportFactions(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var titlesByFactionId = new Dictionary<int, List<Dictionary<string, object?>>>();
        int legacyTitleCount = 0;
        int exportedTitleCount = 0;
        if (KTabFileTable.TryLoad(source, @"settings\factiontitle.txt") is { } titleTable)
        {
            for (int r = 1; r <= titleTable.DataRowCount; r++)
            {
                int rankId = titleTable.GetInt(r, 0, 0);
                string titleStr = titleTable.GetString(r, 1, preferVietnamese: true).Trim();
                int factionOneBased = titleTable.GetInt(r, 2, 0);
                if (rankId < 1 || factionOneBased < 1 || titleStr.Length == 0)
                {
                    continue;
                }

                legacyTitleCount++;
                int factionId = MapFactionTitleFactionId(factionOneBased);
                if (factionId < 0)
                {
                    continue;
                }
                if (!titlesByFactionId.TryGetValue(factionId, out List<Dictionary<string, object?>>? tList))
                {
                    tList = [];
                    titlesByFactionId[factionId] = tList;
                }

                tList.Add(new Dictionary<string, object?>
                {
                    ["rankId"] = rankId,
                    ["titleName"] = titleStr,
                });
            }
        }

        if (KTabFileTable.TryLoad(source, @"settings\ranksetting.txt") is { } rankSettingTable)
        {
            if (!titlesByFactionId.TryGetValue(10, out List<Dictionary<string, object?>>? huaShanTitles))
            {
                huaShanTitles = [];
                titlesByFactionId[10] = huaShanTitles;
            }

            var existingHuaShanRankIds = new HashSet<int>();
            foreach (Dictionary<string, object?> entry in huaShanTitles)
            {
                if (entry.TryGetValue("rankId", out object? existingId) && existingId is int idVal)
                {
                    existingHuaShanRankIds.Add(idVal);
                }
            }

            for (int r = 1; r <= rankSettingTable.DataRowCount; r++)
            {
                int rankId = rankSettingTable.GetInt(r, 0, 0);
                if (rankId < 82 || rankId > 89 || !existingHuaShanRankIds.Add(rankId))
                {
                    continue;
                }

                string titleStr = rankSettingTable.GetString(r, 1, preferVietnamese: true).Trim();
                if (titleStr.Length == 0)
                {
                    continue;
                }

                legacyTitleCount++;
                huaShanTitles.Add(new Dictionary<string, object?>
                {
                    ["rankId"] = rankId,
                    ["titleName"] = titleStr,
                });
            }

            if (huaShanTitles.Count == 0)
            {
                titlesByFactionId.Remove(10);
            }
        }

        byte[]? factionBytes = null;
        if (!source.TryReadServerFile(@"settings\faction\门派设定.ini", out factionBytes))
        {
            string? fallbackFactionKey = source.EnumerateServerFiles(@"settings\faction", recursive: false)
                .Where(static f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static f => Path.GetFileName(f).Length)
                .FirstOrDefault();
            if (fallbackFactionKey is not null)
            {
                source.TryReadServerFile(fallbackFactionKey, out factionBytes);
            }
        }

        var rawFactions = new List<(int Id, string ShowName, Dictionary<string, object?> Props)>();
        if (factionBytes is not null)
        {
            Dictionary<string, Dictionary<string, byte[]>> secs = ParseRawIniSections(factionBytes);
            for (int fid = 0; fid <= 15; fid++)
            {
                if (!secs.TryGetValue(fid.ToString(CultureInfo.InvariantCulture), out Dictionary<string, byte[]>? sec))
                {
                    continue;
                }

                string showName = GetIniDecodedString(sec, "ShowName", preferVietnamese: true).Trim();
                string rawName = GetIniAsciiString(sec, "Name");
                if (showName.Length == 0)
                {
                    showName = rawName.Length > 0 ? rawName : $"Faction {fid}";
                }

                string seriesStr = GetIniAsciiString(sec, "Series").ToUpperInvariant();
                int series = seriesStr switch
                {
                    "S_GOLD" => 0,
                    "S_WOOD" => 1,
                    "S_WATER" => 2,
                    "S_FIRE" => 3,
                    "S_EARTH" => 4,
                    _ => 0,
                };

                string campStr = GetIniAsciiString(sec, "Camp").ToUpperInvariant();
                int camp = campStr switch
                {
                    "C_JUSTICE" => 1,
                    "C_EVIL" => 2,
                    "C_BALANCE" => 3,
                    _ => 3,
                };

                int[] allowedGenders = fid switch
                {
                    0 => [0],
                    4 or 5 => [1],
                    _ => [0, 1],
                };

                var props = new Dictionary<string, object?>
                {
                    ["id"] = fid,
                    ["code"] = string.Empty,
                    ["name"] = showName,
                    ["elementSeries"] = series,
                    ["camp"] = camp,
                    ["allowedGenders"] = allowedGenders,
                    ["skillIds"] = CanonicalFactionBaseSkillIds.GetValueOrDefault(fid, []),
                };

                if (titlesByFactionId.TryGetValue(fid, out List<Dictionary<string, object?>>? titles) && titles.Count > 0)
                {
                    props["titles"] = titles;
                    exportedTitleCount += titles.Count;
                }

                rawFactions.Add((fid, showName, props));
            }
        }

        string[] codes = HybridLegacyTextDecoder.AssignSymmetricCodes(rawFactions, x => x.ShowName, x => x.Id);
        var result = new List<Dictionary<string, object?>>(rawFactions.Count);
        for (int i = 0; i < rawFactions.Count; i++)
        {
            rawFactions[i].Props["code"] = codes[i];
            result.Add(rawFactions[i].Props);
        }

        registry.RecordCount("players.factions", rawFactions.Count, result.Count);
        registry.RecordCount("players.factionTitles", legacyTitleCount, exportedTitleCount);
        return result;
    }

    private static List<Dictionary<string, object?>> ExportStarterVillages(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        if (!source.TryReadServerFile(@"settings\nativeplacelist.ini", out byte[]? nativeBytes))
        {
            return [];
        }

        Dictionary<string, Dictionary<string, byte[]>> nativeSecs = ParseRawIniSections(nativeBytes);
        Dictionary<string, Dictionary<string, byte[]>> revIdSecs = source.TryReadServerFile(@"settings\npc\player\revivalid.ini", out byte[]? revIdBytes)
            ? ParseRawIniSections(revIdBytes)
            : new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Dictionary<string, byte[]>> revPosSecs = source.TryReadServerFile(@"settings\revivepos.ini", out byte[]? revPosBytes)
            ? ParseRawIniSections(revPosBytes)
            : new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);

        int count = nativeSecs.TryGetValue("List", out Dictionary<string, byte[]>? listSec)
            ? GetIniInt(listSec, "Count", 8)
            : 8;

        var rawVillages = new List<(int Id, string Name, Dictionary<string, object?> Props)>();
        for (int idx = 0; idx < count; idx++)
        {
            if (!nativeSecs.TryGetValue(idx.ToString(CultureInfo.InvariantCulture), out Dictionary<string, byte[]>? sec))
            {
                continue;
            }

            int mapId = GetIniInt(sec, "Id", 0);
            if (mapId < 1)
            {
                continue;
            }

            string name = GetIniDecodedString(sec, "Name", preferVietnamese: true).Trim();
            if (name.Length == 0)
            {
                name = $"Village {mapId}";
            }

            string desc = GetIniDecodedString(sec, "Desc", preferVietnamese: true).Trim();

            int defaultRevId = 0;
            if (revIdSecs.TryGetValue(mapId.ToString(CultureInfo.InvariantCulture), out Dictionary<string, byte[]>? ridSec))
            {
                defaultRevId = Math.Max(0, GetIniInt(ridSec, "RevivalId00", 0));
            }

            var revPoints = new List<Dictionary<string, object?>>();
            var seenRevPointIds = new HashSet<int>();
            if (revPosSecs.TryGetValue(mapId.ToString(CultureInfo.InvariantCulture), out Dictionary<string, byte[]>? posSec))
            {
                foreach (var (k, vBytes) in posSec.OrderBy(kv => int.TryParse(kv.Key, out int pk) ? pk : int.MaxValue))
                {
                    if (!int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rpId) || rpId < 0)
                    {
                        continue;
                    }

                    string coords = Encoding.Latin1.GetString(vBytes);
                    string[] parts = coords.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2 &&
                        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mpsX) &&
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mpsY) &&
                        mpsX >= 0 && mpsY >= 0 &&
                        seenRevPointIds.Add(rpId))
                    {
                        revPoints.Add(new Dictionary<string, object?>
                        {
                            ["revivalPointId"] = rpId,
                            ["mpsX"] = mpsX,
                            ["mpsY"] = mpsY,
                        });
                    }
                }
            }

            if (revPoints.Count == 0)
            {
                revPoints.Add(new Dictionary<string, object?>
                {
                    ["revivalPointId"] = defaultRevId,
                    ["mpsX"] = 52032,
                    ["mpsY"] = 101696,
                });
            }
            else if (!seenRevPointIds.Contains(defaultRevId))
            {
                defaultRevId = (int)revPoints[0]["revivalPointId"]!;
            }

            var props = new Dictionary<string, object?>
            {
                ["id"] = idx,
                ["code"] = string.Empty,
                ["name"] = name,
                ["mapId"] = mapId,
                ["defaultRevivalPointId"] = defaultRevId,
                ["revivalPoints"] = revPoints,
            };
            if (desc.Length > 0)
            {
                props["description"] = desc;
            }

            rawVillages.Add((idx, name, props));
        }

        string[] codes = HybridLegacyTextDecoder.AssignSymmetricCodes(rawVillages, x => x.Name, x => x.Id);
        var result = new List<Dictionary<string, object?>>(rawVillages.Count);
        for (int i = 0; i < rawVillages.Count; i++)
        {
            rawVillages[i].Props["code"] = codes[i];
            result.Add(rawVillages[i].Props);
        }

        registry.RecordCount("players.starterVillages", rawVillages.Count, result.Count);
        return result;
    }

    private static Dictionary<string, object?> ExportPkRules(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var penalties = new List<Dictionary<string, object?>>();
        int cooldownFrames = 3240;
        int maxExpLoss = 500000;
        int maxPkValue = 10;
        int legacyRows = 0;

        if (KTabFileTable.TryLoad(source, @"settings\npc\player\pkpunish.txt") is { } table)
        {
            if (table.DataRowCount >= 1)
            {
                cooldownFrames = Math.Max(0, table.GetInt(1, 6, 3240));
                maxExpLoss = Math.Max(0, table.GetInt(1, 7, 500000));
            }

            for (int r = 1; r <= table.DataRowCount; r++)
            {
                int pkVal = table.GetInt(r, 0, -1);
                if (pkVal < 0 || pkVal > 10)
                {
                    continue;
                }

                legacyRows++;
                if (pkVal > maxPkValue)
                {
                    maxPkValue = pkVal;
                }

                int rowMaxExp = table.GetInt(r, 7, maxExpLoss);
                penalties.Add(new Dictionary<string, object?>
                {
                    ["pkValue"] = pkVal,
                    ["expLossPerMil"] = Math.Max(0, table.GetInt(r, 1, 0)),
                    ["maxExpLoss"] = Math.Max(0, rowMaxExp),
                    ["moneyDropPerMil"] = Math.Max(0, table.GetInt(r, 2, 0)),
                    ["bagItemDropPerMil"] = Math.Max(0, table.GetInt(r, 3, 0)),
                    ["equipDropPerMil"] = Math.Max(0, table.GetInt(r, 4, 0)),
                    ["durabilityLoss"] = Math.Max(0, table.GetInt(r, 8, 0)),
                });
            }
        }

        registry.RecordCount("players.pkRules", legacyRows, penalties.Count);
        return new Dictionary<string, object?>
        {
            ["switchToPeaceCooldownFrames"] = cooldownFrames,
            ["maxPkValue"] = Math.Max(1, maxPkValue),
            ["penaltiesByPkValue"] = penalties,
        };
    }

    private static List<Dictionary<string, object?>> ExportMeridians(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var acupointsByMeridian = new Dictionary<int, List<Dictionary<string, object?>>>();
        int legacyAcupoints = 0;
        int exportedAcupoints = 0;

        if (KTabFileTable.TryLoad(source, @"settings\meridian\meridian_level.txt") is { } lvlTable)
        {
            for (int r = 1; r <= lvlTable.DataRowCount; r++)
            {
                string acuName = lvlTable.GetString(r, 0, preferVietnamese: true).Trim();
                int meridianId = lvlTable.GetInt(r, 1, 0);
                int lvl = lvlTable.GetInt(r, 2, 0);
                if (meridianId < 1 || lvl < 1)
                {
                    continue;
                }

                legacyAcupoints++;
                if (acuName.Length == 0)
                {
                    acuName = $"Huyệt vị {lvl}";
                }

                int fallback = Math.Max(0, lvlTable.GetInt(r, 3, 0));
                int rate = Math.Clamp(lvlTable.GetInt(r, 4, 10000), 0, 10000);
                string desc = lvlTable.GetString(r, 5, preferVietnamese: true).Trim();

                var affixIds = new List<int>(3);
                for (int c = 6; c <= 8; c++)
                {
                    int aid = lvlTable.GetInt(r, c, 0);
                    if (aid > 0)
                    {
                        affixIds.Add(aid);
                    }
                }

                int zhenYuan = Math.Max(0, lvlTable.GetInt(r, 9, 0));
                int huMaiDan = Math.Max(0, lvlTable.GetInt(r, 10, 0));

                var acuObj = new Dictionary<string, object?>
                {
                    ["level"] = lvl,
                    ["name"] = acuName,
                    ["successRatePerTenThousand"] = rate,
                    ["fallbackLevel"] = fallback,
                    ["zhenYuanCost"] = zhenYuan,
                    ["huMaiDanCost"] = huMaiDan,
                    ["magicAffixIds"] = affixIds,
                };
                if (desc.Length > 0)
                {
                    acuObj["description"] = desc;
                }

                if (!acupointsByMeridian.TryGetValue(meridianId, out List<Dictionary<string, object?>>? list))
                {
                    list = [];
                    acupointsByMeridian[meridianId] = list;
                }

                list.Add(acuObj);
            }
        }

        var rawMeridians = new List<(int Id, string Name, Dictionary<string, object?> Props)>();
        if (KTabFileTable.TryLoad(source, @"settings\meridian\meridian.txt") is { } merTable)
        {
            for (int r = 1; r <= merTable.DataRowCount; r++)
            {
                int id = merTable.GetInt(r, 0, 0);
                if (id < 1 || id > 16)
                {
                    continue;
                }

                string name = merTable.GetString(r, 1, preferVietnamese: true).Trim();
                if (name.Length == 0)
                {
                    name = $"Kinh mạch {id}";
                }

                int taskVarId = Math.Max(1, merTable.GetInt(r, 2, 4000 + id));
                List<Dictionary<string, object?>> acus = acupointsByMeridian.GetValueOrDefault(id) ?? [];
                exportedAcupoints += acus.Count;

                rawMeridians.Add((id, name, new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["code"] = string.Empty,
                    ["name"] = name,
                    ["taskVariableId"] = taskVarId,
                    ["acupoints"] = acus,
                }));
            }
        }

        string[] codes = HybridLegacyTextDecoder.AssignSymmetricCodes(rawMeridians, x => x.Name, x => x.Id);
        var result = new List<Dictionary<string, object?>>(rawMeridians.Count);
        for (int i = 0; i < rawMeridians.Count; i++)
        {
            rawMeridians[i].Props["code"] = codes[i];
            result.Add(rawMeridians[i].Props);
        }

        registry.RecordCount("players.meridians", rawMeridians.Count, result.Count);
        registry.RecordCount("players.meridianAcupoints", legacyAcupoints, exportedAcupoints);
        return result;
    }

    private static Dictionary<string, object?> ExportStaminaRules(ILegacyServerSource source)
    {
        int normalAdd = 1;
        int exerciseSub = 1;
        int fightSub = 1;
        int killSub = 18;
        int sitAdd = 10;

        if (source.TryReadServerFile(@"settings\npc\player\stamina.ini", out byte[]? stamBytes))
        {
            Dictionary<string, Dictionary<string, byte[]>> secs = ParseRawIniSections(stamBytes);
            if (secs.TryGetValue("stamina", out Dictionary<string, byte[]>? sec))
            {
                normalAdd = Math.Max(0, GetIniInt(sec, "NormalAdd", 1));
                exerciseSub = Math.Max(0, GetIniInt(sec, "ExerciseRunSub", 1));
                fightSub = Math.Max(0, GetIniInt(sec, "FightRunSub", 1));
                killSub = Math.Max(0, GetIniInt(sec, "KillRunSub", 18));
                sitAdd = Math.Max(0, GetIniInt(sec, "SitAdd", 10));
            }
        }

        return new Dictionary<string, object?>
        {
            ["normalRegenPerTick"] = normalAdd,
            ["exerciseRunDrainPerTick"] = exerciseSub,
            ["fightRunDrainPerTick"] = fightSub,
            ["killRunDrainPerTick"] = killSub,
            ["sitRegenPerMil"] = sitAdd,
        };
    }

    private static List<Dictionary<string, object?>> ExportTeamResistEffects(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var result = new List<Dictionary<string, object?>>(5);
        int legacySections = 0;

        if (source.TryReadServerFile(@"settings\npc\player\team_resist_effect.ini", out byte[]? teamBytes))
        {
            Dictionary<string, Dictionary<string, byte[]>> secs = ParseRawIniSections(teamBytes);
            for (int s = 0; s < 5; s++)
            {
                if (!secs.TryGetValue(s.ToString(CultureInfo.InvariantCulture), out Dictionary<string, byte[]>? sec))
                {
                    continue;
                }

                legacySections++;
                int[] bonuses = new int[5];
                for (int t = 0; t < 5; t++)
                {
                    bonuses[t] = Math.Max(0, GetIniInt(sec, t.ToString(CultureInfo.InvariantCulture), 0));
                }

                result.Add(new Dictionary<string, object?>
                {
                    ["memberSeries"] = s,
                    ["grantedResistType"] = TeamGrantedResistTypes[s],
                    ["bonusByTeammateSeries"] = bonuses,
                });
            }
        }

        registry.RecordCount("players.teamResistEffects", legacySections, result.Count);
        return result;
    }

    private static Dictionary<string, object?> ExportTongConfig(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var levels = new List<Dictionary<string, object?>>();
        int legacyLvlCount = 0;
        if (KTabFileTable.TryLoad(source, @"settings\tong\tong_level_data.txt") is { } lvlTable)
        {
            for (int r = 1; r <= lvlTable.DataRowCount; r++)
            {
                int level = lvlTable.GetInt(r, 0, -1);
                if (level < 0)
                {
                    continue;
                }

                legacyLvlCount++;
                levels.Add(new Dictionary<string, object?>
                {
                    ["level"] = level,
                    ["workshopUpperLevel"] = Math.Max(1, lvlTable.GetInt(r, 1, 1)),
                    ["maxWorkshopCount"] = Math.Max(0, lvlTable.GetInt(r, 2, 0)),
                    ["weeklyBuildUpper"] = Math.Max(0, lvlTable.GetInt(r, 3, 0)),
                    ["upgradeFund"] = Math.Max(0, lvlTable.GetInt(r, 4, 0)),
                    ["upgradeWorkshopCount"] = Math.Max(0, lvlTable.GetInt(r, 5, 0)),
                    ["upgradeHiWorkshopCount"] = Math.Max(0, lvlTable.GetInt(r, 6, 0)),
                    ["upgradeHiWorkshopLevel"] = Math.Max(0, lvlTable.GetInt(r, 7, 0)),
                });
            }
        }

        var rawWorkshops = new List<(int Id, string Name, Dictionary<string, object?> Props)>();
        if (KTabFileTable.TryLoad(source, @"settings\tong\workshop\workshops.txt") is { } wsTable)
        {
            for (int r = 1; r <= wsTable.DataRowCount; r++)
            {
                int id = wsTable.GetInt(r, 0, 0);
                if (id < 1)
                {
                    continue;
                }

                string name = wsTable.GetString(r, 1, preferVietnamese: true).Trim();
                if (name.Length == 0)
                {
                    name = $"Tác phường {id}";
                }

                string desc = wsTable.GetString(r, 2, preferVietnamese: true).Trim();
                double coeff = Math.Max(0.0, wsTable.GetDouble(r, 3, 1.0));
                string scriptPath = LegacyPathResolver.NormalizeEnginePath(wsTable.GetRawCell(r, 7));

                var props = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["code"] = string.Empty,
                    ["name"] = name,
                    ["costCoefficient"] = coeff,
                };
                if (desc.Length > 0)
                {
                    props["description"] = desc;
                }

                if (scriptPath.Length > 0)
                {
                    props["scriptPath"] = scriptPath;
                }

                rawWorkshops.Add((id, name, props));
            }
        }

        string[] wsCodes = HybridLegacyTextDecoder.AssignSymmetricCodes(rawWorkshops, x => x.Name, x => x.Id);
        var workshops = new List<Dictionary<string, object?>>(rawWorkshops.Count);
        for (int i = 0; i < rawWorkshops.Count; i++)
        {
            rawWorkshops[i].Props["code"] = wsCodes[i];
            workshops.Add(rawWorkshops[i].Props);
        }

        var rawStunts = new List<(int Id, string Name, Dictionary<string, object?> Props)>();
        if (KTabFileTable.TryLoad(source, @"settings\tong\tongstunt_setting.txt") is { } stuntTable)
        {
            for (int r = 1; r <= stuntTable.DataRowCount; r++)
            {
                // Row 1 in tongstunt_setting.txt is a Vietnamese secondary header ("ID kỹ năng") -> GetInt(r, 0, 0) == 0
                int id = stuntTable.GetInt(r, 0, 0);
                if (id < 1)
                {
                    continue;
                }

                string name = stuntTable.GetString(r, 1, preferVietnamese: true).Trim();
                if (name.Length == 0)
                {
                    name = $"Bang kỹ {id}";
                }

                int maxMembers = Math.Max(1, stuntTable.GetInt(r, 2, 1));
                int maxPerMember = Math.Max(1, stuntTable.GetInt(r, 3, 1));
                int cycleWeeks = Math.Max(1, stuntTable.GetInt(r, 4, 4));
                int weeklyCost = Math.Max(0, stuntTable.GetInt(r, 5, 0));
                int rightLimitRaw = stuntTable.GetInt(r, 6, 2);
                int rightLimit = rightLimitRaw is 1 or 2 ? rightLimitRaw : 2;
                int skillId = Math.Max(1, stuntTable.GetInt(r, 7, 1));
                string desc = stuntTable.GetString(r, 8, preferVietnamese: true).Trim();

                var props = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["code"] = string.Empty,
                    ["name"] = name,
                    ["skillId"] = skillId,
                    ["maxMembers"] = maxMembers,
                    ["maxCountPerMember"] = maxPerMember,
                    ["cycleWeeks"] = cycleWeeks,
                    ["weeklyMaintenanceCost"] = weeklyCost,
                    ["rightLimit"] = rightLimit,
                };
                if (desc.Length > 0)
                {
                    props["description"] = desc;
                }

                rawStunts.Add((id, name, props));
            }
        }

        string[] stuntCodes = HybridLegacyTextDecoder.AssignSymmetricCodes(rawStunts, x => x.Name, x => x.Id);
        var stunts = new List<Dictionary<string, object?>>(rawStunts.Count);
        for (int i = 0; i < rawStunts.Count; i++)
        {
            rawStunts[i].Props["code"] = stuntCodes[i];
            stunts.Add(rawStunts[i].Props);
        }

        registry.RecordCount("players.tongLevels", legacyLvlCount, levels.Count);
        registry.RecordCount("players.tongWorkshops", rawWorkshops.Count, workshops.Count);
        registry.RecordCount("players.tongStunts", rawStunts.Count, stunts.Count);

        return new Dictionary<string, object?>
        {
            ["levels"] = levels,
            ["workshops"] = workshops,
            ["stunts"] = stunts,
        };
    }

    private static List<Dictionary<string, object?>> ExportChatCosts(ILegacyServerSource source, ExportCatalogRegistry registry)
    {
        var defaults = new Dictionary<byte, (int MinLevel, int MoneyCost, int ManaPercent, int StaminaPercent)>
        {
            [0] = (0, 0, 0, 0),
            [1] = (0, 10, 0, 0),
            [2] = (20, 0, 20, 0),
            [3] = (0, 0, 10, 0),
            [4] = (30, 0, 80, 0),
        };

        int legacySectionCount = 0;
        if (source.TryReadServerFile(@"settings\npc\player\chatcost.ini", out byte[]? iniBytes))
        {
            Dictionary<string, Dictionary<string, byte[]>> secs = ParseRawIniSections(iniBytes);
            foreach ((string sectionKey, Dictionary<string, byte[]> kv) in secs)
            {
                if (!byte.TryParse(sectionKey.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte costType))
                {
                    continue;
                }

                legacySectionCount++;
                (int defLevel, int defMoney, int defMana, int defStamina) = defaults.TryGetValue(costType, out var existing)
                    ? existing
                    : (0, 0, 0, 0);

                defaults[costType] = (
                    Math.Max(0, GetIniInt(kv, "Level", defLevel)),
                    Math.Max(0, GetIniInt(kv, "Money", defMoney)),
                    Math.Max(0, GetIniInt(kv, "ManaPercent", defMana)),
                    Math.Max(0, GetIniInt(kv, "StaminaPercent", defStamina)));
            }
        }

        var list = defaults
            .OrderBy(kv => kv.Key)
            .Select(kv => new Dictionary<string, object?>
            {
                ["costType"] = (int)kv.Key,
                ["minLevel"] = kv.Value.MinLevel,
                ["moneyCost"] = kv.Value.MoneyCost,
                ["manaPercent"] = kv.Value.ManaPercent,
                ["staminaPercent"] = kv.Value.StaminaPercent,
            })
            .ToList();

        if (legacySectionCount > 0)
        {
            registry.RecordCount("players.chatCosts", legacySectionCount, Math.Min(legacySectionCount, list.Count));
        }

        return list;
    }

    private static List<string> ExportProhibitedRoleNamePatterns(ILegacyServerSource source)
    {
        if (!source.TryReadGatewayFile(@"settings\goddess_rolename.flt", out byte[]? raw) &&
            !source.TryReadGatewayFile("goddess_rolename.flt", out raw) &&
            !source.TryReadServerFile(@"settings\rolename.flt", out raw) &&
            !source.TryReadServerFile(@"settings\goddess_rolename.flt", out raw) &&
            !source.TryReadServerFile("goddess_rolename.flt", out raw))
        {
            return ["@+GM", "@+GameMaster"];
        }

        var patterns = new List<string>();
        int lineStart = 0;
        while (lineStart <= raw.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < raw.Length && raw[lineEnd] != (byte)'\n' && raw[lineEnd] != (byte)'\r')
            {
                lineEnd++;
            }

            ReadOnlySpan<byte> slice = TrimAsciiWhitespace(raw.AsSpan(lineStart, lineEnd - lineStart));
            if (!slice.IsEmpty && slice[0] != (byte)';')
            {
                string decoded = HybridLegacyTextDecoder.DecodeSmart(slice, preferVietnamese: false).Trim();
                if (!string.IsNullOrEmpty(decoded))
                {
                    patterns.Add(decoded);
                }
            }

            if (lineEnd >= raw.Length)
            {
                break;
            }

            if (lineEnd + 1 < raw.Length && raw[lineEnd] == (byte)'\r' && raw[lineEnd + 1] == (byte)'\n')
            {
                lineStart = lineEnd + 2;
            }
            else
            {
                lineStart = lineEnd + 1;
            }
        }

        return patterns.Count > 0 ? patterns : ["@+GM", "@+GameMaster"];
    }

    private static Dictionary<string, Dictionary<string, byte[]>> ParseRawIniSections(ReadOnlySpan<byte> rawBytes)
    {
        var result = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, byte[]>? currentSection = null;

        int lineStart = 0;
        while (lineStart <= rawBytes.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < rawBytes.Length && rawBytes[lineEnd] != (byte)'\n' && rawBytes[lineEnd] != (byte)'\r')
            {
                lineEnd++;
            }

            ReadOnlySpan<byte> line = TrimAsciiWhitespace(rawBytes[lineStart..lineEnd]);
            if (!line.IsEmpty && line[0] != (byte)';' && !(line.Length >= 2 && line[0] == (byte)'/' && line[1] == (byte)'/'))
            {
                if (line[0] == (byte)'[' && line[^1] == (byte)']' && line.Length > 2)
                {
                    string secName = Encoding.Latin1.GetString(line[1..^1]).Trim();
                    if (!result.TryGetValue(secName, out currentSection))
                    {
                        currentSection = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                        result[secName] = currentSection;
                    }
                }
                else if (currentSection is not null)
                {
                    int eq = line.IndexOf((byte)'=');
                    if (eq > 0)
                    {
                        string key = Encoding.Latin1.GetString(TrimAsciiWhitespace(line[..eq]));
                        byte[] val = TrimAsciiWhitespace(line[(eq + 1)..]).ToArray();
                        currentSection[key] = val;
                    }
                }
            }

            if (lineEnd >= rawBytes.Length)
            {
                break;
            }

            if (lineEnd + 1 < rawBytes.Length && rawBytes[lineEnd] == (byte)'\r' && rawBytes[lineEnd + 1] == (byte)'\n')
            {
                lineStart = lineEnd + 2;
            }
            else
            {
                lineStart = lineEnd + 1;
            }
        }

        return result;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int s = 0;
        int e = span.Length - 1;
        while (s <= e && span[s] is (byte)' ' or (byte)'\t')
        {
            s++;
        }

        while (e >= s && span[e] is (byte)' ' or (byte)'\t' or 0)
        {
            e--;
        }

        return s <= e ? span[s..(e + 1)] : ReadOnlySpan<byte>.Empty;
    }

    private static int GetIniInt(Dictionary<string, byte[]> sec, string key, int defaultValue)
    {
        if (!sec.TryGetValue(key, out byte[]? raw) || raw.Length == 0)
        {
            return defaultValue;
        }

        string s = Encoding.Latin1.GetString(raw).Trim();
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : defaultValue;
    }

    private static string GetIniAsciiString(Dictionary<string, byte[]> sec, string key) =>
        sec.TryGetValue(key, out byte[]? raw) ? Encoding.Latin1.GetString(raw).Trim() : string.Empty;

    private static string GetIniDecodedString(Dictionary<string, byte[]> sec, string key, bool preferVietnamese) =>
        sec.TryGetValue(key, out byte[]? raw) ? HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese) : string.Empty;
}
