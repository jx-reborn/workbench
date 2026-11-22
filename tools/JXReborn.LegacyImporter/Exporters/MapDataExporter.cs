using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Encodings;
using JXReborn.Primitives.WorldZones;

namespace JXReborn.LegacyImporter.Exporters;

/// <summary>
/// Exports unified world map documents (<c>data/maps/&lt;id:D3&gt;_&lt;code&gt;.map.json</c>) per <c>JX Reborn Data Standard v1</c>,
/// extracting full RLE-compressed 16x32 obstacle grids, <c>TRAP_SECTION</c> portals &amp; unmapped trap spans,
/// <c>NPC_SECTION</c> static map spawns, <c>OBJ_SECTION</c> ground objects, and <c>maplist.ini</c> / <c>forbitheart.txt</c> rules.
/// </summary>
public sealed class MapDataExporter
{
    private static readonly HashSet<string> ValidMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "City",
        "Capital",
        "Field",
        "Cave",
        "Battle",
        "Battlefield",
        "Mission",
        "Prison",
        "Country",
        "Tong",
        "Other",
        "Others",
    };

    public void PreloadMapMetadata(ILegacyServerSource source, bool exportAllMaps, ExportCatalogRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);

        Dictionary<int, Dictionary<string, byte[]>> mapPropsById = LoadMapListIni(source, out Dictionary<int, byte[]> rawSubPathById);
        HashSet<int> worldSetIds = LoadWorldSetIds(source);
        if (worldSetIds.Contains(324))
        {
            worldSetIds.Add(323);
            worldSetIds.Add(325);
        }
        XPackArchiveReader? pak = OpenMapsPak(source);

        var selectedMaps = new List<(int MapId, string Name)>();
        IEnumerable<int> candidateIds = exportAllMaps
            ? rawSubPathById.Keys.OrderBy(id => id)
            : worldSetIds.OrderBy(id => id);

        foreach (int mapId in candidateIds)
        {
            if (mapId < 1 || !rawSubPathById.TryGetValue(mapId, out byte[]? rawSubPath))
            {
                continue;
            }

            if (exportAllMaps && !worldSetIds.Contains(mapId) && !TryReadWorBounds(source, rawSubPath, pak, out _))
            {
                continue;
            }

            string mapName = $"Map {mapId}";
            if (mapPropsById.TryGetValue(mapId, out Dictionary<string, byte[]>? props) &&
                props.TryGetValue("name", out byte[]? rawName))
            {
                string decoded = HybridLegacyTextDecoder.DecodeSmart(rawName, preferVietnamese: true).Trim();
                if (decoded.Length > 0)
                {
                    mapName = decoded;
                }
            }

            selectedMaps.Add((mapId, mapName));
        }

        string[] codes = HybridLegacyTextDecoder.AssignSymmetricCodes(selectedMaps, x => x.Name, x => x.MapId);
        registry.ActiveMapIds.Clear();
        registry.MapCodeById.Clear();
        registry.MapNameById.Clear();

        for (int i = 0; i < selectedMaps.Count; i++)
        {
            int mapId = selectedMaps[i].MapId;
            registry.ActiveMapIds.Add(mapId);
            registry.MapCodeById[mapId] = codes[i];
            registry.MapNameById[mapId] = selectedMaps[i].Name;
        }
    }

    public int ExportMaps(
        ILegacyServerSource source,
        string outputDataDirectory,
        bool exportAllMaps = false,
        ExportCatalogRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        registry ??= new ExportCatalogRegistry();
        if (registry.ActiveMapIds.Count == 0)
        {
            PreloadMapMetadata(source, exportAllMaps, registry);
        }

        string mapsOutDir = Path.Combine(outputDataDirectory, "maps");
        Directory.CreateDirectory(mapsOutDir);

        foreach (string existingJson in Directory.EnumerateFiles(mapsOutDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            File.Delete(existingJson);
        }

        Dictionary<int, Dictionary<string, byte[]>> mapPropsById = LoadMapListIni(source, out Dictionary<int, byte[]> rawSubPathById);
        HashSet<int> forbidHeartIds = LoadForbidHeartMapIds(source);
        XPackArchiveReader? pak = OpenMapsPak(source);
        LegacyReplaceNameTable replaceNames = LegacyReplaceNameTable.LoadNpcReplaceNames(source);

        FrozenDictionary<uint, PortalTrapBindingTemplate> trapBindings = LegacyTrapScriptScanner.ScanServerRoot(source);

        int exportedMaps = 0;
        int legacyObstacleRegions = 0;
        int exportedObstacleRegions = 0;
        int legacyTrapSpans = 0;
        int exportedTrapSpans = 0;
        int legacyRegionNpcs = 0;
        int exportedRegionNpcs = 0;
        int legacyRegionObjs = 0;
        int exportedRegionObjs = 0;

        foreach (int mapId in registry.ActiveMapIds.OrderBy(id => id))
        {
            if (!rawSubPathById.TryGetValue(mapId, out byte[]? rawSubPath))
            {
                continue;
            }

            string code = registry.MapCodeById[mapId];
            string mapName = registry.MapNameById[mapId];
            mapPropsById.TryGetValue(mapId, out Dictionary<string, byte[]>? props);
            props ??= new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            string mapType = NormalizeMapType(GetPropAscii(props, "MapType"));
            Dictionary<string, object?>? worldMapPos = ParseWorldMapPos(GetPropAscii(props, "MapPos"));
            Dictionary<string, object?> mapRules = BuildMapRules(mapId, mapType, props, forbidHeartIds, registry);

            (int leftX, int topY, int rightX, int bottomY) = TryReadWorBounds(source, rawSubPath, pak, out var worRect)
                ? worRect
                : (0, 0, 0, 0);

            string? looseMapDir = source.TryResolveServerDirectory(rawSubPath, "maps");

            var encodedRegions = new List<Dictionary<string, object?>>();
            var trapSpansByTrapId = new Dictionary<uint, List<ParsedTrapCellSpan>>();
            var regionNpcs = new List<ParsedRegionNpc>();
            var regionObjs = new List<ParsedRegionObj>();

            if (rightX >= leftX && bottomY >= topY && !(leftX == 0 && rightX == 0 && topY == 0 && bottomY == 0 && looseMapDir is null))
            {
                for (int ry = topY; ry <= bottomY; ry++)
                {
                    for (int rx = leftX; rx <= rightX; rx++)
                    {
                        if (!TryReadRegionDatBytes(source, looseMapDir, rawSubPath, pak, rx, ry, out byte[]? regionBytes) ||
                            regionBytes is null ||
                            !RegionDatBinaryReader.TryParse(regionBytes, rx, ry, out ParsedRegionDat? parsed, replaceNames) ||
                            parsed is null)
                        {
                            continue;
                        }

                        string? rleB64 = ObstacleRleCodec.EncodeCellsToBase64(parsed.ObstacleCells);
                        if (rleB64 is not null)
                        {
                            legacyObstacleRegions++;
                            encodedRegions.Add(new Dictionary<string, object?>
                            {
                                ["regionX"] = rx,
                                ["regionY"] = ry,
                                ["obstacleRleBase64"] = rleB64,
                            });
                            exportedObstacleRegions++;
                        }

                        foreach (ParsedTrapCellSpan span in parsed.Traps)
                        {
                            legacyTrapSpans++;
                            if (!trapSpansByTrapId.TryGetValue(span.TrapId, out List<ParsedTrapCellSpan>? list))
                            {
                                list = [];
                                trapSpansByTrapId[span.TrapId] = list;
                            }

                            list.Add(span);
                        }

                        foreach (ParsedRegionNpc rNpc in parsed.Npcs)
                        {
                            if (rNpc.TemplateId >= 0 && registry.NpcCodeById.ContainsKey(rNpc.TemplateId) && rNpc.MpsX >= 0 && rNpc.MpsY >= 0)
                            {
                                legacyRegionNpcs++;
                                regionNpcs.Add(rNpc);
                            }
                        }

                        foreach (ParsedRegionObj rObj in parsed.Objects)
                        {
                            if (rObj.ObjDataId >= 1 && registry.ObjCodeById.ContainsKey(rObj.ObjDataId) && rObj.MpsX >= 0 && rObj.MpsY >= 0)
                            {
                                legacyRegionObjs++;
                                regionObjs.Add(rObj);
                            }
                        }
                    }
                }
            }

            List<Dictionary<string, object?>> portals = BuildPortals(trapSpansByTrapId, trapBindings, registry, ref exportedTrapSpans);
            List<Dictionary<string, object?>> spawnGroups = BuildSpawnGroups(regionNpcs, registry, ref exportedRegionNpcs);
            List<Dictionary<string, object?>> mapObjects = BuildMapObjects(regionObjs, registry, ref exportedRegionObjs);

            var rootDoc = new Dictionary<string, object?>
            {
                ["$schema"] = "../../schemas/map.schema.json",
                ["schemaVersion"] = "1.0.0",
                ["id"] = mapId,
                ["code"] = code,
                ["name"] = mapName,
                ["mapType"] = mapType,
            };

            if (worldMapPos is not null)
            {
                rootDoc["worldMapPos"] = worldMapPos;
            }

            rootDoc["mapRules"] = mapRules;
            rootDoc["regionBounds"] = new Dictionary<string, object?>
            {
                ["leftRegionX"] = leftX,
                ["topRegionY"] = topY,
                ["rightRegionX"] = rightX,
                ["bottomRegionY"] = bottomY,
            };
            rootDoc["regions"] = encodedRegions;
            rootDoc["portals"] = portals;
            rootDoc["spawnGroups"] = spawnGroups;

            if (mapObjects.Count > 0)
            {
                rootDoc["mapObjects"] = mapObjects;
            }

            string outFile = Path.Combine(mapsOutDir, $"{mapId:D3}_{code}.map.json");
            LegacyPathResolver.WriteCanonicalJson(outFile, rootDoc);
            exportedMaps++;
        }

        registry.RecordCount("maps.exportedMaps", registry.ActiveMapIds.Count, exportedMaps);
        registry.RecordCount("maps.obstacleRegions", legacyObstacleRegions, exportedObstacleRegions);
        registry.RecordCount("maps.trapSpans", legacyTrapSpans, exportedTrapSpans);
        registry.RecordCount("maps.regionNpcs", legacyRegionNpcs, exportedRegionNpcs);
        registry.RecordCount("maps.regionObjects", legacyRegionObjs, exportedRegionObjs);

        return exportedMaps;
    }

    public static string Slugify(string text) => HybridLegacyTextDecoder.Slugify(text, "unknown");

    private static List<Dictionary<string, object?>> BuildPortals(
        Dictionary<uint, List<ParsedTrapCellSpan>> trapSpansByTrapId,
        FrozenDictionary<uint, PortalTrapBindingTemplate> trapBindings,
        ExportCatalogRegistry registry,
        ref int exportedTrapSpans)
    {
        var rawPortals = new List<(int Ordinal, string BaseSlug, Dictionary<string, object?> Props)>(trapSpansByTrapId.Count);
        int ord = 0;

        foreach (var (trapId, spans) in trapSpansByTrapId.OrderBy(kv => kv.Key))
        {
            ord++;
            var triggerCells = new List<Dictionary<string, object?>>(spans.Count);
            foreach (ParsedTrapCellSpan s in spans)
            {
                triggerCells.Add(new Dictionary<string, object?>
                {
                    ["regionX"] = s.RegionX,
                    ["regionY"] = s.RegionY,
                    ["cellY"] = (int)s.CellY,
                    ["startCellX"] = (int)s.CellX,
                    ["cellCount"] = (int)s.SpanCells,
                });
                exportedTrapSpans++;
            }

            long jsonTrapId = trapId;
            var pObj = new Dictionary<string, object?>
            {
                ["trapId"] = jsonTrapId,
                ["code"] = string.Empty,
                ["spawnSource"] = "regionDat",
            };

            string baseSlug;
            if (trapBindings.TryGetValue(trapId, out PortalTrapBindingTemplate? b) && b is not null)
            {
                if (!string.IsNullOrWhiteSpace(b.ScriptPath))
                {
                    pObj["scriptPath"] = b.ScriptPath;
                }

                if (b.ActionKind == PortalTrapActionKind.ChangeMapPortal)
                {
                    pObj["action"] = "teleport";
                    pObj["targetMapId"] = (int)b.TargetMapId;
                    pObj["targetMpsX"] = Math.Max(0, b.TargetMpsX);
                    pObj["targetMpsY"] = Math.Max(0, b.TargetMpsY);
                    pObj["targetFightMode"] = b.TargetFightMode == 0 ? 0 : 1;
                    if (b.MinRequiredLevel > 1)
                    {
                        pObj["minLevel"] = (int)b.MinRequiredLevel;
                    }

                    if (b.BounceBackMpsX > 0 || b.BounceBackMpsY > 0)
                    {
                        pObj["bounceMpsX"] = Math.Max(0, b.BounceBackMpsX);
                        pObj["bounceMpsY"] = Math.Max(0, b.BounceBackMpsY);
                    }

                    if (!string.IsNullOrWhiteSpace(b.DeniedMessage))
                    {
                        pObj["deniedMessage"] = b.DeniedMessage;
                    }

                    baseSlug = registry.MapCodeById.TryGetValue(b.TargetMapId, out string? targetMapCode)
                        ? $"portal-to-{targetMapCode}"
                        : $"portal-to-map-{b.TargetMapId}";
                }
                else
                {
                    pObj["action"] = "toggleFightMode";
                    pObj["targetMpsX"] = Math.Max(0, b.TargetMpsX);
                    pObj["targetMpsY"] = Math.Max(0, b.TargetMpsY);
                    pObj["targetFightMode"] = b.TargetFightMode == 0 ? 0 : 1;
                    pObj["alternateMpsX"] = Math.Max(0, b.AlternateMpsX);
                    pObj["alternateMpsY"] = Math.Max(0, b.AlternateMpsY);
                    pObj["alternateFightMode"] = b.AlternateFightMode == 0 ? 0 : 1;
                    baseSlug = "gate-fight-toggle";
                }
            }
            else
            {
                pObj["action"] = "unmapped";
                baseSlug = $"trap-{trapId}";
            }

            pObj["triggerCells"] = triggerCells;
            rawPortals.Add((ord, baseSlug, pObj));
        }

        string[] codes = HybridLegacyTextDecoder.AssignSymmetricCodes(rawPortals, x => x.BaseSlug, x => x.Ordinal);
        var result = new List<Dictionary<string, object?>>(rawPortals.Count);
        for (int i = 0; i < rawPortals.Count; i++)
        {
            rawPortals[i].Props["code"] = codes[i];
            result.Add(rawPortals[i].Props);
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildSpawnGroups(
        List<ParsedRegionNpc> regionNpcs,
        ExportCatalogRegistry registry,
        ref int exportedRegionNpcs)
    {
        var normalized = new List<(
            int TplId,
            string NpcCode,
            string EntityKind,
            int Level,
            int Series,
            int Camp,
            int FightMode,
            int RespawnFrames,
            string? LootTableCode,
            bool IsSpecial,
            string NameOverride,
            string ScriptPath,
            int MpsX,
            int MpsY,
            int DirIndex)>(regionNpcs.Count);

        foreach (ParsedRegionNpc n in regionNpcs)
        {
            if (!registry.NpcCodeById.TryGetValue(n.TemplateId, out string? npcCode))
            {
                continue;
            }

            registry.NpcMetaById.TryGetValue(n.TemplateId, out var meta);
            string entityKind = n.Kind switch
            {
                3 => "DialogNpc",
                4 => "Partner",
                >= 0 => "Monster",
                _ => !string.IsNullOrEmpty(meta.EntityKind) ? meta.EntityKind : "Monster",
            };

            int level = Math.Max(1, (int)n.Level);
            int series = n.Series <= 4 ? (int)n.Series : (meta.DefaultSeries is >= -1 and <= 4 ? meta.DefaultSeries : -1);
            int camp = n.Camp < 255 ? (int)n.Camp : Math.Max(0, meta.DefaultCamp);
            int fightMode = string.Equals(entityKind, "DialogNpc", StringComparison.Ordinal) ? 0 : 1;
            int respawnFrames = string.Equals(entityKind, "DialogNpc", StringComparison.Ordinal)
                ? 0
                : Math.Max(0, meta.RespawnFrames > 0 ? meta.RespawnFrames : 180);
            string? lootCode = string.Equals(entityKind, "DialogNpc", StringComparison.Ordinal) ? null : meta.LootTableCode;
            int dirIdx = Math.Clamp((int)n.CurFrame, 0, 63);

            string nameOverride = string.Empty;
            if (!string.IsNullOrWhiteSpace(n.RawCustomName) &&
                !string.Equals(n.RawCustomName, meta.Name, StringComparison.Ordinal) &&
                (n.IsSpecialNpc || !ContainsCjkIdeographs(n.RawCustomName)))
            {
                nameOverride = n.RawCustomName;
            }

            normalized.Add((
                n.TemplateId,
                npcCode,
                entityKind,
                level,
                series,
                camp,
                fightMode,
                respawnFrames,
                lootCode,
                n.IsSpecialNpc,
                nameOverride,
                n.ScriptPath,
                n.MpsX,
                n.MpsY,
                dirIdx));
        }

        var groups = normalized
            .GroupBy(x => (x.TplId, x.EntityKind, x.Level, x.Series, x.Camp, x.FightMode, x.IsSpecial, x.NameOverride, x.ScriptPath))
            .OrderBy(g => g.Key.TplId)
            .ThenBy(g => g.Key.Level)
            .ToList();

        var result = new List<Dictionary<string, object?>>(groups.Count);
        foreach (var g in groups)
        {
            var first = g.First();
            var spawns = new List<Dictionary<string, object?>>();
            foreach (var item in g)
            {
                var sp = new Dictionary<string, object?>
                {
                    ["mpsX"] = item.MpsX,
                    ["mpsY"] = item.MpsY,
                };
                if (item.DirIndex != 0)
                {
                    sp["dirIndex"] = item.DirIndex;
                }

                spawns.Add(sp);
                exportedRegionNpcs++;
            }

            var grpObj = new Dictionary<string, object?>
            {
                ["npcTemplateId"] = first.TplId,
                ["npcCode"] = first.NpcCode,
                ["spawnSource"] = "regionDat",
                ["entityKind"] = first.EntityKind,
                ["level"] = first.Level,
                ["series"] = first.Series,
                ["camp"] = first.Camp,
                ["fightMode"] = first.FightMode,
                ["respawnFrames"] = first.RespawnFrames,
            };

            if (first.IsSpecial)
            {
                grpObj["isSpecialNpc"] = true;
            }

            if (first.NameOverride.Length > 0)
            {
                grpObj["nameOverride"] = first.NameOverride;
            }

            if (first.ScriptPath.Length > 0)
            {
                grpObj["scriptPath"] = first.ScriptPath;
            }

            if (!string.IsNullOrEmpty(first.LootTableCode))
            {
                grpObj["lootTableCode"] = first.LootTableCode;
            }

            grpObj["spawns"] = spawns;
            result.Add(grpObj);
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildMapObjects(
        List<ParsedRegionObj> regionObjs,
        ExportCatalogRegistry registry,
        ref int exportedRegionObjs)
    {
        var result = new List<Dictionary<string, object?>>(regionObjs.Count);
        foreach (ParsedRegionObj o in regionObjs)
        {
            if (!registry.ObjCodeById.TryGetValue(o.ObjDataId, out string? objCode))
            {
                continue;
            }

            var entry = new Dictionary<string, object?>
            {
                ["objTemplateId"] = o.ObjDataId,
                ["objCode"] = objCode,
                ["mpsX"] = o.MpsX,
                ["mpsY"] = o.MpsY,
            };

            int dir = Math.Clamp((int)o.Dir, 0, 63);
            if (dir != 0)
            {
                entry["dirIndex"] = dir;
            }

            int state = Math.Max(0, (int)o.State);
            if (state != 0)
            {
                entry["state"] = state;
            }

            if (!string.IsNullOrWhiteSpace(o.ScriptPath))
            {
                entry["scriptPath"] = o.ScriptPath;
            }

            result.Add(entry);
            exportedRegionObjs++;
        }

        return result;
    }

    private static Dictionary<string, object?> BuildMapRules(
        int mapId,
        string mapType,
        Dictionary<string, byte[]> props,
        HashSet<int> forbidHeartIds,
        ExportCatalogRegistry registry)
    {
        bool isSafeCity = mapType is "City" or "Capital";
        var rules = new Dictionary<string, object?>
        {
            ["defaultFightMode"] = isSafeCity ? 0 : 1,
            ["allowPk"] = !isSafeCity,
            ["forbidHeartTeleport"] = forbidHeartIds.Contains(mapId),
        };

        string? normalLoot = registry.TryResolveLootCode(GetPropEnginePath(props, "NormalDropRate"));
        if (!string.IsNullOrEmpty(normalLoot))
        {
            rules["normalLootTableCode"] = normalLoot;
        }

        string? goldenLoot = registry.TryResolveLootCode(GetPropEnginePath(props, "GoldenDropRate"));
        if (!string.IsNullOrEmpty(goldenLoot))
        {
            rules["goldenLootTableCode"] = goldenLoot;
        }

        int autoGoldenRate = Math.Max(0, GetPropInt(props, "AutoGoldenNpc", 0));
        if (autoGoldenRate > 0)
        {
            rules["autoGoldenNpcRate"] = autoGoldenRate;
        }

        int goldenType = Math.Max(0, GetPropInt(props, "GoldenType", 0));
        if (goldenType > 0)
        {
            rules["goldenType"] = goldenType;
        }

        string nwScript = GetPropEnginePath(props, "NewWorldScript");
        if (nwScript.Length > 0)
        {
            rules["newWorldScriptPath"] = nwScript;
        }

        string nwParam = GetPropDecoded(props, "NewWorldParam");
        if (nwParam.Length > 0)
        {
            rules["newWorldParam"] = nwParam;
        }

        string mapInfo = GetPropDecoded(props, "MapInfo");
        if (mapInfo.Length > 0)
        {
            rules["mapInfo"] = mapInfo;
        }

        int autoLvlFlag = GetPropInt(props, "NpcAutoLevelFlag", 0);
        if (autoLvlFlag != 0)
        {
            int minLvl = Math.Max(1, GetPropInt(props, "NpcAutoLevelMin", 1));
            int maxLvl = Math.Max(minLvl, GetPropInt(props, "NpcAutoLevelMax", minLvl));
            rules["npcAutoLevel"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["minLevel"] = minLvl,
                ["maxLevel"] = maxLvl,
            };
        }

        int autoSeriesFlag = GetPropInt(props, "NpcSeriesAuto", 0);
        if (autoSeriesFlag != 0)
        {
            rules["npcAutoSeries"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["metalWeight"] = Math.Max(0, GetPropInt(props, "NpcSeriesMetal", 20)),
                ["woodWeight"] = Math.Max(0, GetPropInt(props, "NpcSeriesWood", 20)),
                ["waterWeight"] = Math.Max(0, GetPropInt(props, "NpcSeriesWater", 20)),
                ["fireWeight"] = Math.Max(0, GetPropInt(props, "NpcSeriesFire", 20)),
                ["earthWeight"] = Math.Max(0, GetPropInt(props, "NpcSeriesEarth", 20)),
            };
        }

        return rules;
    }

    private static string NormalizeMapType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Field";
        }

        foreach (string valid in ValidMapTypes)
        {
            if (string.Equals(valid, raw.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return valid;
            }
        }

        return "Other";
    }

    private static Dictionary<string, object?>? ParseWorldMapPos(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            return new Dictionary<string, object?>
            {
                ["x"] = x,
                ["y"] = y,
            };
        }

        return null;
    }

    private static readonly int[] ClassicHubAndSectMapIds =
    [
        // 7 Major Cities
        1, 11, 37, 78, 80, 162, 176,
        // 8 Starter Villages
        20, 53, 99, 100, 101, 121, 153, 174,
        // 10 Sect Home Maps
        103, 59, 25, 183, 13, 154, 115, 49, 81, 131,
        // Ba Lăng Huyện Portals
        54, 70, 74, 199,
    ];

    private static HashSet<int> LoadWorldSetIds(ILegacyServerSource source)
    {
        var ids = new HashSet<int>(ClassicHubAndSectMapIds);
        if (!source.TryReadServerFile(@"maps\worldset.ini", out byte[]? wsBytes))
        {
            return ids;
        }

        string text = Encoding.Latin1.GetString(wsBytes);
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (key.StartsWith("World", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId) &&
                mapId >= 1)
            {
                ids.Add(mapId);
            }
        }

        return ids;
    }

    private static HashSet<int> LoadForbidHeartMapIds(ILegacyServerSource source)
    {
        var ids = new HashSet<int>();
        KTabFileTable? table = KTabFileTable.TryLoad(source, @"settings\forbitheart.txt");
        if (table is null)
        {
            return ids;
        }

        for (int r = 1; r <= table.DataRowCount; r++)
        {
            int mid = table.GetInt(r, 0, 0);
            if (mid >= 1)
            {
                ids.Add(mid);
            }
        }

        return ids;
    }

    public static Dictionary<int, Dictionary<string, byte[]>> LoadMapListIni(
        ILegacyServerSource source,
        out Dictionary<int, byte[]> rawSubPathById)
    {
        ArgumentNullException.ThrowIfNull(source);
        rawSubPathById = [];
        var mapPropsById = new Dictionary<int, Dictionary<string, byte[]>>();

        if (!source.TryReadServerFile(@"settings\maplist.ini", out byte[]? bytes))
        {
            return mapPropsById;
        }

        int lineStart = 0;
        while (lineStart <= bytes.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < bytes.Length && bytes[lineEnd] != (byte)'\n' && bytes[lineEnd] != (byte)'\r')
            {
                lineEnd++;
            }

            ReadOnlySpan<byte> line = TrimSpaces(bytes.AsSpan(lineStart, lineEnd - lineStart));
            if (!line.IsEmpty && line[0] != (byte)';' && line[0] != (byte)'[')
            {
                int eq = line.IndexOf((byte)'=');
                if (eq > 0)
                {
                    string key = Encoding.Latin1.GetString(TrimSpaces(line[..eq]));
                    byte[] val = TrimSpaces(line[(eq + 1)..]).ToArray();

                    int underscore = key.IndexOf('_');
                    if (underscore < 0)
                    {
                        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId) && mapId >= 1 && val.Length > 0)
                        {
                            rawSubPathById[mapId] = val;
                        }
                    }
                    else
                    {
                        string idPart = key[..underscore];
                        string propName = key[(underscore + 1)..];
                        if (int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId) && mapId >= 1)
                        {
                            if (!mapPropsById.TryGetValue(mapId, out Dictionary<string, byte[]>? dict))
                            {
                                dict = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                                mapPropsById[mapId] = dict;
                            }

                            dict[propName] = val;
                        }
                    }
                }
            }

            if (lineEnd >= bytes.Length)
            {
                break;
            }

            lineStart = lineEnd + 1 < bytes.Length && bytes[lineEnd] == (byte)'\r' && bytes[lineEnd + 1] == (byte)'\n'
                ? lineEnd + 2
                : lineEnd + 1;
        }

        return mapPropsById;
    }

    public static XPackArchiveReader? OpenMapsPak(ILegacyServerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string[] candidates =
        [
            @"pak\maps.pak",
            @"data\maps.pak",
            "maps.pak",
        ];

        foreach (string c in candidates)
        {
            if (source.TryReadServerFile(c, out byte[]? pakBytes))
            {
                XPackArchiveReader? reader = XPackArchiveReader.TryOpenBytes(pakBytes);
                if (reader is not null)
                {
                    return reader;
                }
            }
        }

        return null;
    }

    public static bool TryReadWorBounds(
        ILegacyServerSource source,
        ReadOnlySpan<byte> rawSubPath,
        XPackArchiveReader? pak,
        out (int LeftX, int TopY, int RightX, int BottomY) rect)
    {
        rect = (0, 0, 0, 0);
        byte[]? worBytes = null;

        ReadOnlySpan<byte> subPath = TrimLeadingSlashes(rawSubPath);
        string? looseDir = source.TryResolveServerDirectory(subPath, "maps");
        if (looseDir is not null)
        {
            string? worKey = source.EnumerateServerFiles(looseDir, recursive: false)
                .FirstOrDefault(static f => f.EndsWith(".wor", StringComparison.OrdinalIgnoreCase));
            if (worKey is not null)
            {
                source.TryReadServerFile(worKey, out worBytes);
            }
        }

        if (worBytes is null)
        {
            // Try <maps>\<subPath>.wor directly on source or in maps.pak
            byte[] relWorBytes = BuildPakPathBytes(@"\maps\", subPath, ".wor");
            if (source.TryReadServerFile(relWorBytes, out byte[]? directWorBytes))
            {
                worBytes = directWorBytes;
            }
            else if (pak is not null && pak.TryReadFile(relWorBytes, out byte[] pakWor))
            {
                worBytes = pakWor;
            }
        }

        if (worBytes is null || worBytes.Length == 0)
        {
            return false;
        }

        string text = Encoding.Latin1.GetString(worBytes);
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("rect=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line[5..].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 4 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int left) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int top) &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int right) &&
                int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bottom))
            {
                rect = (Math.Max(0, left), Math.Max(0, top), Math.Max(0, right), Math.Max(0, bottom));
                return true;
            }
        }

        return false;
    }

    public static bool TryReadRegionDatBytes(
        ILegacyServerSource source,
        string? looseMapDir,
        ReadOnlySpan<byte> rawSubPath,
        XPackArchiveReader? pak,
        int rx,
        int ry,
        out byte[]? regionBytes)
    {
        regionBytes = null;
        if (looseMapDir is not null)
        {
            string sKey = $@"{looseMapDir}\v_{ry:D3}\{rx:D3}_Region_S.dat";
            if (source.TryReadServerFile(sKey, out byte[]? looseBytes))
            {
                regionBytes = looseBytes;
                return true;
            }
        }

        if (pak is not null)
        {
            ReadOnlySpan<byte> subPath = TrimLeadingSlashes(rawSubPath);
            byte[] pakPathS = BuildPakPathBytes(@"\maps\", subPath, $@"\v_{ry:D3}\{rx:D3}_Region_S.dat");
            if (pak.TryReadFile(pakPathS, out byte[] dataS))
            {
                regionBytes = dataS;
                return true;
            }

            byte[] pakPathC = BuildPakPathBytes(@"\maps\", subPath, $@"\v_{ry:D3}\{rx:D3}_Region_C.dat");
            if (pak.TryReadFile(pakPathC, out byte[] dataC))
            {
                regionBytes = dataC;
                return true;
            }
        }

        return false;
    }

    private static byte[] BuildPakPathBytes(string prefixAscii, ReadOnlySpan<byte> middleRaw, string suffixAscii)
    {
        byte[] pre = Encoding.ASCII.GetBytes(prefixAscii);
        byte[] suf = Encoding.ASCII.GetBytes(suffixAscii);
        byte[] buf = new byte[pre.Length + middleRaw.Length + suf.Length];
        pre.CopyTo(buf, 0);
        middleRaw.CopyTo(buf.AsSpan(pre.Length));
        suf.CopyTo(buf.AsSpan(pre.Length + middleRaw.Length));
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] == (byte)'/')
            {
                buf[i] = (byte)'\\';
            }
        }

        return buf;
    }

    private static ReadOnlySpan<byte> TrimLeadingSlashes(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && span[i] is (byte)'\\' or (byte)'/')
        {
            i++;
        }

        return span[i..];
    }

    private static ReadOnlySpan<byte> TrimSpaces(ReadOnlySpan<byte> span)
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

    private static int GetPropInt(Dictionary<string, byte[]> props, string key, int fallback) =>
        props.TryGetValue(key, out byte[]? raw) &&
        int.TryParse(Encoding.Latin1.GetString(raw).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : fallback;

    private static string GetPropAscii(Dictionary<string, byte[]> props, string key) =>
        props.TryGetValue(key, out byte[]? raw) ? Encoding.Latin1.GetString(raw).Trim() : string.Empty;

    private static string GetPropDecoded(Dictionary<string, byte[]> props, string key) =>
        props.TryGetValue(key, out byte[]? raw) ? HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese: true).Trim() : string.Empty;

    private static string GetPropEnginePath(Dictionary<string, byte[]> props, string key) =>
        props.TryGetValue(key, out byte[]? raw) ? LegacyPathResolver.NormalizeEnginePath(raw) : string.Empty;

    private static bool ContainsCjkIdeographs(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is (>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF'))
            {
                return true;
            }
        }

        return false;
    }
}
