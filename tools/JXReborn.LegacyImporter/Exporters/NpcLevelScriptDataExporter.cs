using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Hosting;

namespace JXReborn.LegacyImporter.Exporters;

/// <summary>
/// Evaluates and exports all 58 <c>server1/script/npclevelscript/**/*.lua</c> scripts via <see cref="Lua4Interpreter"/>
/// into <c>data/npcs/level-scripts.npcs.json</c> with SHA-256 provenance and sampled Ground-Truth curves.
/// </summary>
public sealed partial class NpcLevelScriptDataExporter
{
    private static readonly int[] SampledCurveLevels = [1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 95, 100];

    public int ExportNpcLevelScripts(
        ILegacyServerSource source,
        string outputDataDirectory,
        ExportCatalogRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDataDirectory);
        registry ??= new ExportCatalogRegistry();

        string npcsOutDir = Path.Combine(outputDataDirectory, "npcs");
        Directory.CreateDirectory(npcsOutDir);

        string[] scriptFiles = source.EnumerateServerFiles(@"script\npclevelscript", recursive: true)
            .Where(static f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sharedTokenCache = new Dictionary<string, List<Lua4Token>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Dictionary<string, object?>>(scriptFiles.Length);

        foreach (string relKey in scriptFiles)
        {
            if (!source.TryReadServerFile(relKey, out byte[]? rawBytes) || rawBytes is null)
            {
                continue;
            }

            string sha256 = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
            string legacyRelSlash = relKey.Replace('\\', '/').TrimStart('/');
            string canonicalUtf8Path = LegacyScriptPathNormalizer.NormalizeToUtf8("\\" + relKey);
            string code = NpcAndLootDataExporter.NormalizeLevelScriptCode(canonicalUtf8Path);

            string latin1Text = Encoding.Latin1.GetString(rawBytes);
            var includedPaths = new List<string>();
            foreach (Match m in IncludeRegex().Matches(latin1Text))
            {
                byte[] unescaped = HybridLegacyTextDecoder.UnescapeLuaStringBytes(Encoding.Latin1.GetBytes(m.Groups[1].Value));
                string incPath = LegacyPathResolver.NormalizeEnginePath(unescaped);
                if (!string.IsNullOrWhiteSpace(incPath) && !includedPaths.Contains(incPath, StringComparer.OrdinalIgnoreCase))
                {
                    includedPaths.Add(incPath);
                }
            }

            var interp = new Lua4Interpreter(source, sharedTokenCache, lenientMissingGlobals: true);
            interp.ExecuteFile(canonicalUtf8Path);
            bool hasKeyFn = interp.GetGlobal("GetNpcKeyData").IsFunction;
            bool hasLevelFn = interp.GetGlobal("GetNpcLevelData").IsFunction;
            bool isLibraryOnly = !hasKeyFn && !hasLevelFn;

            var doc = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["scriptPath"] = canonicalUtf8Path,
                ["legacyRelativePath"] = legacyRelSlash,
                ["sourceSha256"] = sha256,
                ["isLibraryOnly"] = isLibraryOnly,
            };

            if (includedPaths.Count > 0)
            {
                doc["includedScriptPaths"] = includedPaths;
            }

            if (!isLibraryOnly)
            {
                var curve = new List<Dictionary<string, object?>>(SampledCurveLevels.Length);
                foreach (int lv in SampledCurveLevels)
                {
                    int exp = CallKeyInt(interp, 0, lv, "Exp", 0, 10, 50);
                    int life = CallKeyInt(interp, 0, lv, "Life", 0.5, 20, 50);
                    int ar = CallKeyInt(interp, 0, lv, "AR", 0.07, 2.5, 10);
                    int def = CallKeyInt(interp, 0, lv, "Defense", 0.1, 2.5, 10);
                    int minDmg = CallKeyInt(interp, 0, lv, "MinDamage", 1, 1, 1);
                    int maxDmg = CallKeyInt(interp, 0, lv, "MaxDamage", 1, 1, 1);

                    var resistsBySeries = new List<int[]>(5);
                    for (int ser = 0; ser < 5; ser++)
                    {
                        resistsBySeries.Add([
                            CallLevelInt(interp, ser, lv, "PhysicsResist", "0|0"),
                            CallLevelInt(interp, ser, lv, "ColdResist", "0|0"),
                            CallLevelInt(interp, ser, lv, "FireResist", "0|0"),
                            CallLevelInt(interp, ser, lv, "LightResist", "0|0"),
                            CallLevelInt(interp, ser, lv, "PoisonResist", "0|0"),
                        ]);
                    }

                    curve.Add(new Dictionary<string, object?>
                    {
                        ["level"] = lv,
                        ["exp"] = exp,
                        ["life"] = life,
                        ["attackRating"] = ar,
                        ["defense"] = def,
                        ["minDamage"] = minDmg,
                        ["maxDamage"] = maxDmg,
                        ["resistsBySeries"] = resistsBySeries,
                    });
                }

                doc["defaultCurve"] = curve;
            }

            entries.Add(doc);
        }

        LegacyPathResolver.WriteCanonicalJson(
            Path.Combine(npcsOutDir, "level-scripts.npcs.json"),
            new Dictionary<string, object?>
            {
                ["$schema"] = "../../schemas/npc-catalog.schema.json",
                ["schemaVersion"] = "1.0.0",
                ["npcLevelScripts"] = entries,
            });

        registry.RecordCount("npcs.levelScripts", scriptFiles.Length, entries.Count);
        return entries.Count;
    }

    private static int CallKeyInt(
        Lua4Interpreter interp,
        int series,
        int level,
        string styleName,
        double p1,
        double p2,
        double p3)
    {
        if (!interp.GetGlobal("GetNpcKeyData").IsFunction)
        {
            return 0;
        }

        interp.ResetStepBudget(100_000);
        Lua4Value[] ret = interp.CallGlobal(
            "GetNpcKeyData",
            Lua4Value.FromNumber(series),
            Lua4Value.FromNumber(level),
            Lua4Value.FromString(styleName),
            Lua4Value.FromNumber(p1),
            Lua4Value.FromNumber(p2),
            Lua4Value.FromNumber(p3));
        return ret.Length > 0 ? (int)(long)ret[0].AsNumberOrDefault() : 0;
    }

    private static int CallLevelInt(
        Lua4Interpreter interp,
        int series,
        int level,
        string styleName,
        string paramStr)
    {
        if (!interp.GetGlobal("GetNpcLevelData").IsFunction)
        {
            return 0;
        }

        interp.ResetStepBudget(100_000);
        Lua4Value[] ret = interp.CallGlobal(
            "GetNpcLevelData",
            Lua4Value.FromNumber(series),
            Lua4Value.FromNumber(level),
            Lua4Value.FromString(styleName),
            Lua4Value.FromString(paramStr));
        return ret.Length > 0 ? (int)(long)ret[0].AsNumberOrDefault() : 0;
    }

    [GeneratedRegex(@"Include\s*\(\s*""([^""]+)""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex IncludeRegex();
}
