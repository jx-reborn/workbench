using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.Snapshots;
using JXReborn.LegacyImporter.Exporters;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Primitives.Characters;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Exporters;

public sealed class NpcLevelScriptGroundTruthTests
{
    [Fact]
    public void ExportNpcLevelScripts_AndEvaluateRuntimeScaling_ShouldMatchRealLua4InterpreterAndPreserveDoubleSubfolder()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string tempDataDir = Path.Combine(Path.GetTempPath(), $"jx-npclvl-gt-{Guid.NewGuid():N}");

        try
        {
            ILegacyServerSource source = LegacyServerSourceFactory.Create(legacyBin);
            var registry = new ExportCatalogRegistry();

            var skillExporter = new SkillAndMissileDataExporter();
            _ = skillExporter.ExportSkillsAndMissiles(source, tempDataDir, registry);

            var levelScriptExporter = new NpcLevelScriptDataExporter();
            int exportedScripts = levelScriptExporter.ExportNpcLevelScripts(source, tempDataDir, registry);
            Assert.Equal(58, exportedScripts);

            var npcExporter = new NpcAndLootDataExporter();
            _ = npcExporter.ExportNpcsAndLoot(source, tempDataDir, registry);

            string levelScriptsJson = Path.Combine(tempDataDir, "npcs", "level-scripts.npcs.json");
            Assert.True(File.Exists(levelScriptsJson));

            // 1. Verify all 58 physical files under server1/script/npclevelscript have matching SHA-256 on disk
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(levelScriptsJson)))
            {
                JsonElement entries = doc.RootElement.GetProperty("npcLevelScripts");
                Assert.Equal(58, entries.GetArrayLength());

                var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement item in entries.EnumerateArray())
                {
                    string code = item.GetProperty("code").GetString()!;
                    string relPath = item.GetProperty("legacyRelativePath").GetString()!;
                    string sha256 = item.GetProperty("sourceSha256").GetString()!;
                    Assert.True(codes.Add(code), $"Duplicate levelScript code '{code}'.");

                    string diskFile = Path.Combine(legacyBin, "server1", relPath.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(diskFile), $"Missing ground-truth file '{diskFile}'.");
                    string diskSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(diskFile)));
                    Assert.Equal(diskSha, sha256);
                }

                // Must keep distinct codes for double/animal.lua vs animal.lua
                Assert.Contains("animal", codes);
                Assert.Contains("double/animal", codes);
                Assert.Contains("double/standard", codes);
            }

            // 2. Load runtime snapshot and verify real npcs.txt templates against direct Lua4Interpreter execution
            GameAssetsSnapshotBundle bundle = GameAssetsSnapshotLoader.LoadFromCanonicalJsonDirectory(tempDataDir);
            Assert.NotEmpty(bundle.NpcTemplates);

            KTabFileTable npcsTable = Assert.IsType<KTabFileTable>(KTabFileTable.TryLoad(source, @"settings\npcs.txt"));

            // Real NPC template IDs covering animal, standard, makeboss, dragonboat_boss1, soldier1, fengzhiqi, newnpc
            int[] sampleTemplateIds = [0, 46, 455, 587, 630, 822, 925];
            int[] testLevels = [1, 30, 50, 90];
            Assert.NotEmpty(sampleTemplateIds);
            Assert.NotEmpty(testLevels);

            foreach (int tplId in sampleTemplateIds)
            {
                NpcTemplate tpl = Assert.Single(bundle.NpcTemplates, n => n.TemplateId == tplId);
                Assert.NotNull(tpl.CombatScaling);

                int row = tplId + 1;
                string rawLevelScript = npcsTable.GetEnginePath(row, 32, inheritRow2Default: true);
                var interp = new Lua4Interpreter(source, lenientMissingGlobals: true);
                Assert.True(interp.ExecuteFile(rawLevelScript), $"Failed to execute '{rawLevelScript}' in Lua4Interpreter.");

                foreach (int lv in testLevels)
                {
                    int seriesInt = Math.Clamp((int)tpl.Series, 0, 4);

                    int expectedLife = EvalLegacyKeyStat(interp, npcsTable, row, 37, "Life", seriesInt, lv, fallbackIfZero: 100);
                    int expectedExp = EvalLegacyKeyStat(interp, npcsTable, row, 33, "Exp", seriesInt, lv, fallbackIfZero: 0);
                    int expectedAr = EvalLegacyKeyStat(interp, npcsTable, row, 42, "AR", seriesInt, lv, fallbackIfZero: 100);
                    int expectedDef = EvalLegacyKeyStat(interp, npcsTable, row, 46, "Defense", seriesInt, lv, fallbackIfZero: 0);
                    int expectedMinDmg = EvalLegacyKeyStat(interp, npcsTable, row, 50, "MinDamage", seriesInt, lv, fallbackIfZero: 0);
                    int expectedMaxDmg = EvalLegacyKeyStat(interp, npcsTable, row, 54, "MaxDamage", seriesInt, lv, fallbackIfZero: 0);

                    Assert.Equal(expectedLife, tpl.CombatScaling!.EvaluateMaxLife(tpl.BaseLifeMax, lv));
                    Assert.Equal(expectedExp, tpl.CombatScaling.EvaluateExpReward(tpl.BaseExp, lv));
                    Assert.Equal(expectedAr, tpl.CombatScaling.EvaluateAttackRating(lv));
                    Assert.Equal(expectedDef, tpl.CombatScaling.EvaluateDefense(lv));

                    (int actualMin, int actualMax) = tpl.CombatScaling.EvaluatePhysicsDamage(lv);
                    Assert.Equal(expectedMinDmg, actualMin);
                    Assert.Equal(expectedMaxDmg, actualMax);
                }
            }

            // 3. Specifically verify double/animal gives exact 2x EXP compared to animal at Lv 25 and Lv 60
            var doubleAnimalScaling = new NpcCombatScalingTemplate(LevelScriptCode: "double/animal", ExpParam: 100d);
            var normalAnimalScaling = new NpcCombatScalingTemplate(LevelScriptCode: "animal", ExpParam: 100d);
            Assert.Equal(normalAnimalScaling.EvaluateExpReward(100, 25) * 2, doubleAnimalScaling.EvaluateExpReward(100, 25));
            Assert.Equal(normalAnimalScaling.EvaluateExpReward(100, 60) * 2, doubleAnimalScaling.EvaluateExpReward(100, 60));
        }
        finally
        {
            if (Directory.Exists(tempDataDir))
            {
                Directory.Delete(tempDataDir, recursive: true);
            }
        }
    }

    private static int EvalLegacyKeyStat(
        Lua4Interpreter interp,
        KTabFileTable table,
        int row,
        int startCol,
        string styleName,
        int series,
        int level,
        int fallbackIfZero)
    {
        float param = (float)table.GetDouble(row, startCol, 1.0, inheritRow2Default: true);
        double p1 = table.GetDouble(row, startCol + 1, 0.0, inheritRow2Default: true);
        double p2 = table.GetDouble(row, startCol + 2, 0.0, inheritRow2Default: true);
        double p3 = table.GetDouble(row, startCol + 3, 0.0, inheritRow2Default: true);

        Lua4Value[] ret = interp.CallGlobal(
            "GetNpcKeyData",
            Lua4Value.FromNumber(series),
            Lua4Value.FromNumber(level),
            Lua4Value.FromString(styleName),
            Lua4Value.FromNumber(p1),
            Lua4Value.FromNumber(p2),
            Lua4Value.FromNumber(p3));

        int luaInt = ret.Length > 0 ? (int)(long)ret[0].AsNumberOrDefault() : 0;
        int scaled = (int)((((float)luaInt * param) / 100.0f) + 0.1f);
        return scaled == 0 && fallbackIfZero > 0 ? fallbackIfZero : scaled;
    }
}
