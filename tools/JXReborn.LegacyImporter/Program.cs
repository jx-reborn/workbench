using System;
using System.Collections.Generic;
using System.IO;
using JXReborn.GameAssets.Packs;
using JXReborn.LegacyImporter.Exporters;
using JXReborn.LegacyImporter.Sources;
using JXReborn.LegacyImporter.Verification;
using JXReborn.PackCompiler.Compilation;
using JXReborn.Packs.TieuDaoTu.Hosting;

namespace JXReborn.LegacyImporter;

/// <summary>
/// Offline parity & extraction utility converting legacy server assets into canonical UTF-8 JSON
/// catalogs (<c>data/**/*.json</c>), verifying 4-layer ground-truth parity, and compiling deterministic
/// <c>MemoryPack</c> snapshots via <c>JXReborn.PackCompiler</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        ScriptCodeResolverRegistry.SetResolver(new TieuDaoTuScriptCodeResolver());

        string? explicitSource = null;
        string dataDir = Path.GetFullPath("data");
        string? schemasDirArg = null;
        string artifactsDir = Path.GetFullPath("artifacts");
        string? outputFilePath = null;
        bool exportAllMaps = false;
        bool compileOnly = false;
        bool validateSchemas = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" when i + 1 < args.Length:
                    explicitSource = args[++i];
                    break;
                case "--data" when i + 1 < args.Length:
                    dataDir = Path.GetFullPath(args[++i]);
                    break;
                case "--schemas" when i + 1 < args.Length:
                    schemasDirArg = Path.GetFullPath(args[++i]);
                    break;
                case "--artifacts" when i + 1 < args.Length:
                    artifactsDir = Path.GetFullPath(args[++i]);
                    break;
                case "--output" when i + 1 < args.Length:
                    outputFilePath = Path.GetFullPath(args[++i]);
                    break;
                case "--all-maps":
                    exportAllMaps = true;
                    break;
                case "--compile-only":
                    compileOnly = true;
                    break;
                case "--validate-schemas":
                    validateSchemas = true;
                    break;
            }
        }

        string schemasDir = ResolveSchemasDirectory(schemasDirArg);

        if (compileOnly)
        {
            if (validateSchemas)
            {
                var compileOnlyValidator = new JsonSchemaAssetValidator();
                IReadOnlyList<string> schemaErrors = compileOnlyValidator.ValidateAll(dataDir, schemasDir);
                if (schemaErrors.Count > 0)
                {
                    foreach (string err in schemaErrors)
                    {
                        Console.Error.WriteLine($"[SCHEMA/FK ERROR] {err}");
                    }

                    Console.Error.WriteLine($"[JXReborn.LegacyImporter] FAILED with {schemaErrors.Count} schema/FK error(s).");
                    return 1;
                }

                Console.WriteLine("[JXReborn.LegacyImporter] Layer 4 passed: 0 schema, UTF-8, or foreign-key errors.");
            }

            var compileOnlyCompiler = new MemoryPackSnapshotCompiler();
            string compiledPath = compileOnlyCompiler.CompileSnapshot(dataDir, artifactsDir, outputFilePath);
            Console.WriteLine($"[JXReborn.LegacyImporter] Compiled MemoryPack snapshot -> '{compiledPath}'.");
            return 0;
        }

        string resolvedSource = LegacyServerSourceFactory.ResolveDefaultSourcePath(explicitSource, artifactsDir);
        ILegacyServerSource source = LegacyServerSourceFactory.Create(resolvedSource);
        Console.WriteLine($"[JXReborn.LegacyImporter] Source: {source.DisplayName} -> '{dataDir}' (allMaps={exportAllMaps})...");

        var registry = new ExportCatalogRegistry();

        var mapExporter = new MapDataExporter();
        mapExporter.PreloadMapMetadata(source, exportAllMaps, registry);
        Console.WriteLine($"  Preloaded Active Maps: {registry.ActiveMapIds.Count}");

        var itemExporter = new ItemAndAffixDataExporter();
        var (itemCount, affixCount, objCount) = itemExporter.ExportItemsAndAffixes(source, dataDir, registry);
        Console.WriteLine($"  Items: {itemCount}, MagicAffixes: {affixCount}, GroundObjects: {objCount}");

        var skillExporter = new SkillAndMissileDataExporter();
        var (skillCount, missileCount) = skillExporter.ExportSkillsAndMissiles(source, dataDir, registry);
        Console.WriteLine($"  Skills: {skillCount}, Missiles: {missileCount}");

        var npcExporter = new NpcAndLootDataExporter();
        var (npcCount, lootCount, globalSpawns, eventSpawns) = npcExporter.ExportNpcsAndLoot(source, dataDir, registry);
        Console.WriteLine($"  Npcs: {npcCount}, LootTables: {lootCount}, GlobalSpawns: {globalSpawns}, EventSpawns: {eventSpawns}");

        var playerExporter = new PlayerDataExporter();
        int playerCount = playerExporter.ExportPlayers(source, dataDir, registry);
        Console.WriteLine($"  PlayerTemplates: {playerCount}");

        var shopExporter = new ShopDataExporter();
        var (shopCount, goodsCount) = shopExporter.ExportShops(source, dataDir, registry);
        Console.WriteLine($"  Shops: {shopCount}, Goods: {goodsCount}");

        var systemSettingsExporter = new SystemSettingsDataExporter();
        int systemSettingsFiles = systemSettingsExporter.ExportCoreSystemSettings(source, dataDir);
        Console.WriteLine($"  CoreSystemSettings: {systemSettingsFiles} files -> data/system/");

        var questExporter = new QuestDataExporter();
        var (treaCount, milestoneCount, basicCount, buyCount, findCount, upCount, killCount) =
            questExporter.ExportQuests(source, dataDir, registry);
        Console.WriteLine($"  Quests (DaTau): Treasure={treaCount}, Milestones={milestoneCount}, Basic={basicCount}, Buy={buyCount}, Find={findCount}, Up={upCount}, Kill={killCount}");

        int mapCount = mapExporter.ExportMaps(source, dataDir, exportAllMaps, registry);
        Console.WriteLine($"  Exported Maps: {mapCount}");

        var validator = new JsonSchemaAssetValidator();
        IReadOnlyList<string> errors = validator.ValidateAll(dataDir, schemasDir);
        if (errors.Count > 0)
        {
            foreach (string err in errors)
            {
                Console.Error.WriteLine($"[SCHEMA/FK ERROR] {err}");
            }
        }
        else
        {
            Console.WriteLine("[JXReborn.LegacyImporter] Layer 4 passed: 0 schema, UTF-8, or foreign-key errors.");
        }

        var parityVerifier = new MigrationParityVerifier();
        var (totalParityErrors, reportPath) = parityVerifier.VerifyAndWriteReport(
            source,
            dataDir,
            artifactsDir,
            registry,
            errors);
        Console.WriteLine($"[JXReborn.LegacyImporter] Wrote 4-Layer Parity Report -> '{reportPath}' (totalErrors={totalParityErrors}).");

        if (totalParityErrors > 0)
        {
            Console.Error.WriteLine($"[JXReborn.LegacyImporter] FAILED with {totalParityErrors} parity/schema error(s). Inspect '{reportPath}'.");
            return 1;
        }

        return 0;
    }

    private static string ResolveSchemasDirectory(string? explicitSchemasDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitSchemasDir))
        {
            return explicitSchemasDir;
        }

        string localSchemas = Path.GetFullPath("schemas");
        if (Directory.Exists(localSchemas))
        {
            return localSchemas;
        }

        DirectoryInfo? probe = new(AppContext.BaseDirectory);
        while (probe is not null)
        {
            string siblingServerSchemas = Path.Combine(probe.FullName, "server", "schemas");
            if (Directory.Exists(siblingServerSchemas))
            {
                return siblingServerSchemas;
            }

            probe = probe.Parent;
        }

        return localSchemas;
    }
}
