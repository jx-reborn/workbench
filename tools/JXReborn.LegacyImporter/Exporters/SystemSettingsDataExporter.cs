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
/// Deterministically exports the 8 core gameplay root <c>server1/settings/</c> tables
/// (<c>timertask.txt</c>, <c>playertitle.txt</c>, <c>taxrates.ini</c>, <c>map_type.txt</c>/<c>item_type.txt</c>/<c>item_detail.txt</c>,
/// <c>gamesetting.ini</c>, <c>progressconfig.txt</c>, weapon physics skill map, and <c>tongname.flt</c>)
/// into <c>data/system/*.json</c> with literal UTF-8 and zero guessing.
/// </summary>
public sealed class SystemSettingsDataExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int ExportCoreSystemSettings(ILegacyServerSource source, string dataDir)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);

        string systemDir = Path.Combine(dataDir, "system");
        Directory.CreateDirectory(systemDir);

        int exportedFiles = 0;

        if (ExportTimerTasks(source, Path.Combine(systemDir, "timer-tasks.json")))
        {
            exportedFiles++;
        }

        if (ExportPlayerTitles(source, Path.Combine(systemDir, "player-titles.json")))
        {
            exportedFiles++;
        }

        if (ExportTaxRates(source, Path.Combine(systemDir, "tax-rates.json")))
        {
            exportedFiles++;
        }

        if (ExportMapItemRules(source, Path.Combine(systemDir, "map-item-rules.json")))
        {
            exportedFiles++;
        }

        if (ExportGameSettings(source, Path.Combine(systemDir, "game-settings.json")))
        {
            exportedFiles++;
        }

        if (ExportProgressBars(source, Path.Combine(systemDir, "progress-bars.json")))
        {
            exportedFiles++;
        }

        if (ExportWeaponPhysicsSkills(source, Path.Combine(systemDir, "weapon-physics-skills.json")))
        {
            exportedFiles++;
        }

        if (ExportTongNameFilter(source, Path.Combine(systemDir, "tong-name-filter.json")))
        {
            exportedFiles++;
        }

        if (ExportGoldenBossSchedules(source, Path.Combine(systemDir, "golden-boss-schedules.json")))
        {
            exportedFiles++;
        }

        if (ExportKillerBossSettings(source, Path.Combine(systemDir, "killer-boss-settings.json")))
        {
            exportedFiles++;
        }

        if (ExportCityWarSettings(source, Path.Combine(systemDir, "citywar-settings.json")))
        {
            exportedFiles++;
        }

        if (ExportMapNavigation(source, Path.Combine(systemDir, "map-navigation.json")))
        {
            exportedFiles++;
        }

        if (ExportThiefSkills(source, Path.Combine(systemDir, "thief-skills.json")))
        {
            exportedFiles++;
        }

        if (ExportLotterySettings(source, Path.Combine(systemDir, "lottery-settings.json")))
        {
            exportedFiles++;
        }

        if (ExportFatigueSettings(source, Path.Combine(systemDir, "fatigue-settings.json")))
        {
            exportedFiles++;
        }

        if (ExportMarketAndDialogPermits(source, Path.Combine(systemDir, "market-and-dialog-permits.json")))
        {
            exportedFiles++;
        }

        var stringTableExporter = new StringTableDataExporter();
        if (stringTableExporter.ExportStringResources(source, Path.Combine(systemDir, "string-resources.json")))
        {
            exportedFiles++;
        }

        return exportedFiles;
    }

    private static bool ExportTimerTasks(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\timertask.txt", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        var entries = new JsonArray();
        string[] lines = SplitLines(bytes);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cols = lines[i].Split('\t');
            if (cols.Length < 2 || !int.TryParse(cols[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int taskId))
            {
                continue;
            }

            string rawScript = cols[1].Trim();
            if (string.IsNullOrEmpty(rawScript))
            {
                continue;
            }

            string utf8Script = DecodeGbkPath(rawScript);
            entries.Add(new JsonObject
            {
                ["timerTaskId"] = taskId,
                ["scriptPath"] = utf8Script,
                ["rawScriptPath"] = rawScript,
            });
        }

        WriteJson(outPath, "timer-tasks", entries);
        return true;
    }

    private static bool ExportPlayerTitles(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\playertitle.txt", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        var entries = new JsonArray();
        string[] lines = SplitLines(bytes);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cols = lines[i].Split('\t');
            if (cols.Length < 6 || !int.TryParse(cols[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int titleId))
            {
                continue;
            }

            string titleName = DecodeVietnamese(cols[0]).Trim();
            var extSkills = new JsonArray();
            for (int k = 0; k < 5; k++)
            {
                int idCol = 6 + (k * 2);
                int lvCol = 7 + (k * 2);
                if (lvCol < cols.Length &&
                    TryParseInt(cols[idCol], out int extSkillId) && extSkillId > 0)
                {
                    TryParseInt(cols[lvCol], out int extSkillLevel);
                    extSkills.Add(new JsonObject
                    {
                        ["skillId"] = extSkillId,
                        ["level"] = extSkillLevel,
                    });
                }
            }

            TryParseInt(cols[2], out int specialGraphic);
            TryParseInt(cols[3], out int faceId);
            TryParseInt(cols[4], out int auraSkill);
            TryParseInt(cols[5], out int auraSkillLevel);
            string memo = cols.Length > 16 ? DecodeVietnamese(cols[16]).Trim() : string.Empty;
            string color = cols.Length > 17 ? cols[17].Trim() : string.Empty;

            entries.Add(new JsonObject
            {
                ["titleId"] = titleId,
                ["name"] = titleName,
                ["specialGraphic"] = specialGraphic,
                ["faceId"] = faceId,
                ["auraSkillId"] = auraSkill,
                ["auraSkillLevel"] = auraSkillLevel,
                ["extSkills"] = extSkills,
                ["memo"] = memo,
                ["color"] = color,
            });
        }

        WriteJson(outPath, "player-titles", entries);
        return true;
    }

    private static bool ExportTaxRates(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\taxrates.ini", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        JsonObject sections = ParseIniSections(bytes);
        WriteJsonObject(outPath, "tax-rates", sections);
        return true;
    }

    private static bool ExportMapItemRules(ILegacyServerSource source, string outPath)
    {
        var mapTypes = new JsonArray();
        if (source.TryReadServerFile(@"settings\map_type.txt", out byte[]? mapBytes) && mapBytes is not null)
        {
            string[] lines = SplitLines(mapBytes);
            for (int i = 2; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split('\t');
                if (cols.Length < 3 || string.IsNullOrWhiteSpace(cols[0]))
                {
                    continue;
                }

                mapTypes.Add(new JsonObject
                {
                    ["mapType"] = cols[0].Trim(),
                    ["mapIdsRaw"] = cols[1].Trim(),
                    ["forbidItemTypes"] = SplitCsvArray(cols[2]),
                    ["description"] = cols.Length > 3 ? DecodeVietnamese(cols[3]).Trim() : string.Empty,
                });
            }
        }

        var itemTypes = new JsonArray();
        if (source.TryReadServerFile(@"settings\item_type.txt", out byte[]? typeBytes) && typeBytes is not null)
        {
            string[] lines = SplitLines(typeBytes);
            for (int i = 2; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split('\t');
                if (cols.Length < 2 || string.IsNullOrWhiteSpace(cols[0]))
                {
                    continue;
                }

                itemTypes.Add(new JsonObject
                {
                    ["itemType"] = cols[0].Trim(),
                    ["onlyInMapTypes"] = SplitCsvArray(cols[1]),
                    ["description"] = cols.Length > 2 ? DecodeVietnamese(cols[2]).Trim() : string.Empty,
                });
            }
        }

        var itemDetails = new JsonArray();
        if (source.TryReadServerFile(@"settings\item_detail.txt", out byte[]? detailBytes) && detailBytes is not null)
        {
            string[] lines = SplitLines(detailBytes);
            for (int i = 2; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split('\t');
                if (cols.Length < 4 || string.IsNullOrWhiteSpace(cols[0]))
                {
                    continue;
                }

                TryParseInt(cols[1], out int genre);
                TryParseInt(cols[2], out int detail);
                TryParseInt(cols[3], out int particular);
                itemDetails.Add(new JsonObject
                {
                    ["itemType"] = cols[0].Trim(),
                    ["genre"] = genre,
                    ["detail"] = detail,
                    ["particular"] = particular,
                    ["name"] = cols.Length > 4 ? DecodeVietnamese(cols[4]).Trim() : string.Empty,
                });
            }
        }

        if (mapTypes.Count == 0 && itemTypes.Count == 0 && itemDetails.Count == 0)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["mapTypes"] = mapTypes,
            ["itemTypes"] = itemTypes,
            ["itemDetails"] = itemDetails,
        };
        WriteJsonObject(outPath, "map-item-rules", payload);
        return true;
    }

    private static bool ExportGameSettings(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\gamesetting.ini", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        JsonObject sections = ParseIniSections(bytes);
        WriteJsonObject(outPath, "game-settings", sections);
        return true;
    }

    private static bool ExportProgressBars(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\progressconfig.txt", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        var entries = new JsonArray();
        string[] lines = SplitLines(bytes);
        if (lines.Length < 2)
        {
            return false;
        }

        string[] headers = lines[0].Split('\t');
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cols = lines[i].Split('\t');
            if (cols.Length < 3 || !TryParseInt(cols[0], out int id))
            {
                continue;
            }

            TryParseInt(cols[2], out int seconds);
            var interruptEvents = new JsonArray();
            for (int c = 3; c < Math.Min(cols.Length, headers.Length); c++)
            {
                string header = headers[c].Trim();
                if (header.StartsWith("EVENT_", StringComparison.OrdinalIgnoreCase) &&
                    TryParseInt(cols[c], out int flag) && flag != 0)
                {
                    interruptEvents.Add(header);
                }
            }

            string comment = cols.Length >= headers.Length && headers[^1].Trim().Equals("comment", StringComparison.OrdinalIgnoreCase)
                ? DecodeVietnamese(cols[^1]).Trim()
                : string.Empty;

            entries.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = DecodeVietnamese(cols[1]).Trim(),
                ["durationSeconds"] = seconds,
                ["interruptEvents"] = interruptEvents,
                ["comment"] = comment,
            });
        }

        WriteJson(outPath, "progress-bars", entries);
        return true;
    }

    private static bool ExportWeaponPhysicsSkills(ILegacyServerSource source, string outPath)
    {
        byte[]? bytes = null;
        if (!source.TryReadServerFile(@"settings\ÎäÆ÷ÎïÀí¹¥»÷¶ÔÕÕ±í.txt", out bytes) || bytes is null)
        {
            if (!source.TryReadServerFile(@"settings\武器物理攻击对照表.txt", out bytes) || bytes is null)
            {
                return false;
            }
        }

        var entries = new JsonArray();
        string[] lines = SplitLines(bytes);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cols = lines[i].Split('\t');
            if (cols.Length < 3 ||
                !TryParseInt(cols[0], out int detailType) ||
                !TryParseInt(cols[1], out int particularType) ||
                !TryParseInt(cols[2], out int physicsSkillId))
            {
                continue;
            }

            entries.Add(new JsonObject
            {
                ["detailType"] = detailType,
                ["particularType"] = particularType,
                ["physicsSkillId"] = physicsSkillId,
            });
        }

        WriteJson(outPath, "weapon-physics-skills", entries);
        return true;
    }

    private static bool ExportTongNameFilter(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\tongname.flt", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        var patterns = new JsonArray();
        foreach (string line in SplitLines(bytes))
        {
            string trimmed = DecodeVietnamese(line).Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                patterns.Add(trimmed);
            }
        }

        WriteJson(outPath, "tong-name-filter", patterns, Sha256Hex(bytes));
        return true;
    }

    private static bool ExportGoldenBossSchedules(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\goldboss.txt", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        var entries = new JsonArray();
        string[] lines = SplitLines(bytes);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cols = lines[i].Split('\t');
            if (cols.Length < 11)
            {
                continue;
            }

            string bossName = DecodeVietnamese(cols[0]).Trim();
            if (string.IsNullOrEmpty(bossName))
            {
                continue;
            }

            TryParseInt(cols[2], out int physMagic);
            TryParseInt(cols[4], out int poisonMagic);
            TryParseInt(cols[6], out int coldMagic);
            TryParseInt(cols[8], out int fireMagic);
            TryParseInt(cols[10], out int lightMagic);

            entries.Add(new JsonObject
            {
                ["name"] = bossName,
                ["physicalDamageBase"] = cols[1].Trim(),
                ["physicalMagic"] = physMagic,
                ["poisonDamageBase"] = cols[3].Trim(),
                ["poisonMagic"] = poisonMagic,
                ["coldDamageBase"] = cols[5].Trim(),
                ["coldMagic"] = coldMagic,
                ["fireDamageBase"] = cols[7].Trim(),
                ["fireMagic"] = fireMagic,
                ["lightingDamageBase"] = cols[9].Trim(),
                ["lightingMagic"] = lightMagic,
                ["auraSkillName"] = cols.Length > 11 ? DecodeVietnamese(cols[11]).Trim() : string.Empty,
                ["auraSkillLevel"] = cols.Length > 12 ? cols[12].Trim() : string.Empty,
                ["passiveSkillName"] = cols.Length > 13 ? DecodeVietnamese(cols[13]).Trim() : string.Empty,
                ["passiveSkillLevel"] = cols.Length > 14 ? cols[14].Trim() : string.Empty,
            });
        }

        WriteJson(outPath, "golden-boss-schedules", entries, Sha256Hex(bytes));
        return true;
    }

    private static bool ExportKillerBossSettings(ILegacyServerSource source, string outPath)
    {
        bool hasKiller = source.TryReadServerFile(@"settings\killer.ini", out byte[]? killerBytes) && killerBytes is not null;
        bool hasAdventure = source.TryReadServerFile(@"settings\adventure.txt", out byte[]? advBytes) && advBytes is not null;
        if (!hasKiller && !hasAdventure)
        {
            return false;
        }

        JsonObject config = hasKiller ? ParseIniSections(killerBytes!) : new JsonObject();
        var positions = new JsonArray();
        if (hasAdventure)
        {
            string[] lines = SplitLines(advBytes!);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split('\t');
                if (cols.Length < 3 ||
                    !TryParseInt(cols[0], out int mapId) ||
                    !TryParseInt(cols[1], out int cellX) ||
                    !TryParseInt(cols[2], out int cellY))
                {
                    continue;
                }

                positions.Add(new JsonObject
                {
                    ["mapId"] = mapId,
                    ["cellX"] = cellX,
                    ["cellY"] = cellY,
                });
            }
        }

        var payload = new JsonObject
        {
            ["killerIniSha256"] = hasKiller ? Sha256Hex(killerBytes!) : string.Empty,
            ["adventureTxtSha256"] = hasAdventure ? Sha256Hex(advBytes!) : string.Empty,
            ["config"] = config,
            ["adventurePositions"] = positions,
        };
        WriteJsonObject(outPath, "killer-boss-settings", payload);
        return true;
    }

    private static bool ExportCityWarSettings(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\citywar.ini", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        JsonObject sections = ParseIniSections(bytes);
        WriteJsonObject(outPath, "citywar-settings", sections, Sha256Hex(bytes));
        return true;
    }

    private static bool ExportMapNavigation(ILegacyServerSource source, string outPath)
    {
        bool hasCaves = source.TryReadServerFile(@"settings\cavelist.ini", out byte[]? caveBytes) && caveBytes is not null;
        bool hasTraffic = source.TryReadServerFile(@"settings\maptraffic.ini", out byte[]? trafficBytes) && trafficBytes is not null;
        if (!hasCaves && !hasTraffic)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["caveListSha256"] = hasCaves ? Sha256Hex(caveBytes!) : string.Empty,
            ["mapTrafficSha256"] = hasTraffic ? Sha256Hex(trafficBytes!) : string.Empty,
            ["caveList"] = hasCaves ? ParseIniSections(caveBytes!) : new JsonObject(),
            ["mapTraffic"] = hasTraffic ? ParseIniSections(trafficBytes!) : new JsonObject(),
        };
        WriteJsonObject(outPath, "map-navigation", payload);
        return true;
    }

    private static bool ExportThiefSkills(ILegacyServerSource source, string outPath)
    {
        if (!source.TryReadServerFile(@"settings\thiefskill.txt", out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        var entries = new JsonArray();
        string[] lines = SplitLines(bytes);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cols = lines[i].Split('\t');
            if (cols.Length < 10 || !TryParseInt(cols[0], out int skillId))
            {
                continue;
            }

            TryParseInt(cols[2], out int thiefStyle);
            TryParseInt(cols[3], out int attackRadius);
            TryParseInt(cols[4], out int maxLevel);
            TryParseInt(cols[5], out int timePerCast);
            TryParseInt(cols[6], out int thiefPercent);
            TryParseInt(cols[7], out int skillCostType);
            TryParseInt(cols[8], out int param1);
            TryParseInt(cols[9], out int param2);
            int cost = cols.Length > 15 && TryParseInt(cols[15], out int cVal) ? cVal : 0;

            entries.Add(new JsonObject
            {
                ["skillId"] = skillId,
                ["name"] = DecodeVietnamese(cols[1]).Trim(),
                ["thiefStyle"] = thiefStyle,
                ["attackRadius"] = attackRadius,
                ["maxLevel"] = maxLevel,
                ["timePerCast"] = timePerCast,
                ["thiefPercent"] = thiefPercent,
                ["skillCostType"] = skillCostType,
                ["param1"] = param1,
                ["param2"] = param2,
                ["cost"] = cost,
                ["description"] = cols.Length > 16 ? DecodeVietnamese(cols[16]).Trim() : string.Empty,
            });
        }

        WriteJson(outPath, "thief-skills", entries, Sha256Hex(bytes));
        return true;
    }

    private static bool ExportLotterySettings(ILegacyServerSource source, string outPath)
    {
        bool hasLottery = source.TryReadServerFile(@"settings\lottery.txt", out byte[]? lotBytes) && lotBytes is not null;
        bool hasLotterys = source.TryReadServerFile(@"settings\lotterys.txt", out byte[]? lotsBytes) && lotsBytes is not null;
        if (!hasLottery && !hasLotterys)
        {
            return false;
        }

        var questions = new JsonArray();
        if (hasLottery)
        {
            string[] lines = SplitLines(lotBytes!);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split('\t');
                if (cols.Length < 5 || !TryParseInt(cols[0], out int qId))
                {
                    continue;
                }

                TryParseInt(cols[3], out int chooseCount);
                TryParseInt(cols[4], out int price);
                var choices = new JsonArray();
                for (int c = 5; c < Math.Min(cols.Length, 15); c++)
                {
                    string ch = DecodeVietnamese(cols[c]).Trim();
                    if (!string.IsNullOrEmpty(ch))
                    {
                        choices.Add(ch);
                    }
                }

                int answer = cols.Length > 19 && TryParseInt(cols[19], out int ans) ? ans : 0;
                int bonusRate = cols.Length > 20 && TryParseInt(cols[20], out int br) ? br : 0;
                int maxBonus = cols.Length > 21 && TryParseInt(cols[21], out int mb) ? mb : 0;

                questions.Add(new JsonObject
                {
                    ["questionId"] = qId,
                    ["tip"] = DecodeVietnamese(cols[1]).Trim(),
                    ["content"] = DecodeVietnamese(cols[2]).Trim(),
                    ["chooseCount"] = chooseCount,
                    ["price"] = price,
                    ["choices"] = choices,
                    ["answer"] = answer,
                    ["bonusRate"] = bonusRate,
                    ["maxBonus"] = maxBonus,
                });
            }
        }

        var recurring = new JsonArray();
        if (hasLotterys)
        {
            string[] lines = SplitLines(lotsBytes!);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split('\t');
                if (cols.Length < 2 || string.IsNullOrWhiteSpace(cols[0]))
                {
                    continue;
                }

                recurring.Add(new JsonObject
                {
                    ["name"] = DecodeVietnamese(cols[0]).Trim(),
                });
            }
        }

        var payload = new JsonObject
        {
            ["lotteryTxtSha256"] = hasLottery ? Sha256Hex(lotBytes!) : string.Empty,
            ["lotterysTxtSha256"] = hasLotterys ? Sha256Hex(lotsBytes!) : string.Empty,
            ["questions"] = questions,
            ["recurringLotteries"] = recurring,
        };
        WriteJsonObject(outPath, "lottery-settings", payload);
        return true;
    }

    private static bool ExportFatigueSettings(ILegacyServerSource source, string outPath)
    {
        bool hasLimit = source.TryReadServerFile(@"settings\player_limittime.ini", out byte[]? limitBytes) && limitBytes is not null;
        bool hasTired = source.TryReadServerFile(@"settings\tiredwarning.ini", out byte[]? tiredBytes) && tiredBytes is not null;
        if (!hasLimit && !hasTired)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["playerLimitTimeSha256"] = hasLimit ? Sha256Hex(limitBytes!) : string.Empty,
            ["tiredWarningSha256"] = hasTired ? Sha256Hex(tiredBytes!) : string.Empty,
            ["playerLimitTime"] = hasLimit ? ParseIniSections(limitBytes!) : new JsonObject(),
            ["tiredWarning"] = hasTired ? ParseIniSections(tiredBytes!) : new JsonObject(),
        };
        WriteJsonObject(outPath, "fatigue-settings", payload);
        return true;
    }

    private static bool ExportMarketAndDialogPermits(ILegacyServerSource source, string outPath)
    {
        bool hasAuction = source.TryReadServerFile(@"settings\auction.ini", out byte[]? auctionBytes) && auctionBytes is not null;
        bool hasStall = source.TryReadServerFile(@"settings\stallscript_permit.ini", out byte[]? stallBytes) && stallBytes is not null;
        bool hasPermitNpc = source.TryReadServerFile(@"settings\permitdialognpc_info.txt", out byte[]? permitBytes) && permitBytes is not null;
        if (!hasAuction && !hasStall && !hasPermitNpc)
        {
            return false;
        }

        var permittedNpcs = new JsonArray();
        if (hasPermitNpc)
        {
            string[] lines = SplitLines(permitBytes!);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split('\t');
                if (cols.Length < 2 || string.IsNullOrWhiteSpace(cols[0]) || !TryParseInt(cols[1], out int mapId))
                {
                    continue;
                }

                int tireLimit = cols.Length > 2 && TryParseInt(cols[2], out int tl) ? tl : 0;
                permittedNpcs.Add(new JsonObject
                {
                    ["npcName"] = DecodeVietnamese(cols[0]).Trim(),
                    ["mapId"] = mapId,
                    ["tireLimit"] = tireLimit,
                });
            }
        }

        var payload = new JsonObject
        {
            ["auctionIniSha256"] = hasAuction ? Sha256Hex(auctionBytes!) : string.Empty,
            ["stallPermitIniSha256"] = hasStall ? Sha256Hex(stallBytes!) : string.Empty,
            ["permitDialogNpcSha256"] = hasPermitNpc ? Sha256Hex(permitBytes!) : string.Empty,
            ["auction"] = hasAuction ? ParseIniSections(auctionBytes!) : new JsonObject(),
            ["stallScriptPermits"] = hasStall ? ParseIniSections(stallBytes!) : new JsonObject(),
            ["permittedDialogNpcs"] = permittedNpcs,
        };
        WriteJsonObject(outPath, "market-and-dialog-permits", payload);
        return true;
    }

    private static JsonObject ParseIniSections(byte[] bytes)
    {
        var sections = new JsonObject();
        JsonObject? currentSection = null;
        string currentSectionName = "Default";

        foreach (string rawLine in SplitLines(bytes))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) ||
                line.StartsWith(';') ||
                line.StartsWith("\";", StringComparison.Ordinal) ||
                line.StartsWith('#') ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                currentSectionName = line[1..^1].Trim();
                if (sections[currentSectionName] is not JsonObject existing)
                {
                    existing = new JsonObject();
                    sections[currentSectionName] = existing;
                }

                currentSection = existing;
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (currentSection is null)
            {
                currentSection = new JsonObject();
                sections[currentSectionName] = currentSection;
            }

            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
            {
                currentSection[key] = intVal;
            }
            else
            {
                currentSection[key] = DecodeVietnamese(val);
            }
        }

        return sections;
    }

    private static JsonArray SplitCsvArray(string raw)
    {
        var arr = new JsonArray();
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                arr.Add(part);
            }
        }

        return arr;
    }

    private static string[] SplitLines(byte[] bytes) =>
        Encoding.Latin1.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string DecodeVietnamese(string latin1)
    {
        byte[] raw = Encoding.Latin1.GetBytes(latin1);
        return HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese: true);
    }

    private static string DecodeGbkPath(string latin1)
    {
        byte[] raw = Encoding.Latin1.GetBytes(latin1);
        return HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese: false);
    }

    private static bool TryParseInt(string s, out int value) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteJson(string outPath, string domain, JsonArray entries, string? sourceSha256 = null)
    {
        var doc = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["domain"] = domain,
        };
        if (!string.IsNullOrEmpty(sourceSha256))
        {
            doc["sourceSha256"] = sourceSha256;
        }

        doc["entries"] = entries;
        File.WriteAllText(outPath, doc.ToJsonString(JsonOptions) + "\n", new UTF8Encoding(false));
    }

    private static void WriteJsonObject(string outPath, string domain, JsonObject payload, string? sourceSha256 = null)
    {
        var doc = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["domain"] = domain,
        };
        if (!string.IsNullOrEmpty(sourceSha256))
        {
            doc["sourceSha256"] = sourceSha256;
        }

        doc["data"] = payload;
        File.WriteAllText(outPath, doc.ToJsonString(JsonOptions) + "\n", new UTF8Encoding(false));
    }
}
