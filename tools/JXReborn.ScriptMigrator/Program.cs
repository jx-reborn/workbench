using System;
using System.IO;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator;

/// <summary>
/// Phase 0 migration &amp; auditing CLI for 100% faithful, zero-guessing conversion from
/// <c>legacy-server/bin</c> (<c>jx-linux</c>) to <c>pack-tieudaotu</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string command = args[0].Trim().ToLowerInvariant();
        string legacyRoot = Path.GetFullPath(Path.Combine("..", "legacy-server", "bin"));
        string packRoot = Path.GetFullPath(Path.Combine("..", "pack-tieudaotu"));
        string outRoot = Path.GetFullPath("artifacts");
        bool strict = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--legacy" when i + 1 < args.Length:
                    legacyRoot = Path.GetFullPath(args[++i]);
                    break;
                case "--pack" when i + 1 < args.Length:
                    packRoot = Path.GetFullPath(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    outRoot = Path.GetFullPath(args[++i]);
                    break;
                case "--strict":
                    strict = true;
                    break;
            }
        }

        string mirrorDir = Path.Combine(outRoot, "lua-utf8-mirror");
        string migrationDir = Path.Combine(outRoot, "migration");

        switch (command)
        {
            case "export-utf8-mirror":
                return RunUtf8Mirror(legacyRoot, mirrorDir);
            case "build-manifest":
                return RunBuildManifest(legacyRoot, migrationDir);
            case "audit-settings":
                return RunAuditSettings(legacyRoot, packRoot, migrationDir);
            case "audit-coverage":
                return RunAuditCoverage(legacyRoot, packRoot, migrationDir, strict);
            case "run-all":
                if (RunUtf8Mirror(legacyRoot, mirrorDir) != 0)
                {
                    return 1;
                }

                ScriptInventoryManifest manifest = BuildAndWriteManifest(legacyRoot, migrationDir);
                RunAuditSettings(legacyRoot, packRoot, migrationDir);
                return EvaluateAndWriteCoverage(manifest, packRoot, migrationDir, strict);
            default:
                Console.Error.WriteLine($"Unknown command '{command}'.");
                PrintUsage();
                return 1;
        }
    }

    private static int RunUtf8Mirror(string legacyRoot, string mirrorDir)
    {
        var exporter = new LuaUtf8MirrorExporter();
        int count = exporter.ExportMirror(legacyRoot, mirrorDir);
        Console.WriteLine($"[JXReborn.ScriptMigrator] Exported {count:N0} UTF-8 Lua mirror files -> '{mirrorDir}'.");
        return 0;
    }

    private static int RunBuildManifest(string legacyRoot, string migrationDir)
    {
        _ = BuildAndWriteManifest(legacyRoot, migrationDir);
        return 0;
    }

    private static ScriptInventoryManifest BuildAndWriteManifest(string legacyRoot, string migrationDir)
    {
        var builder = new ScriptInventoryBuilder();
        ScriptInventoryManifest manifest = builder.Build(legacyRoot);
        builder.WriteOutputs(manifest, migrationDir);

        var ledgerWriter = new TaskIdLedgerWriter();
        ledgerWriter.WriteLedger(manifest, Path.Combine(migrationDir, "lua-task-id-registry.md"));

        Console.WriteLine(
            $"[JXReborn.ScriptMigrator] Built 100% manifest: {manifest.TotalFiles:N0} Lua files " +
            $"(server1={manifest.Server1ActiveFiles:N0} active/{manifest.Server1ExcludedFiles:N0} excl, " +
            $"s3relay={manifest.S3RelayActiveFiles:N0} active/{manifest.S3RelayExcludedFiles:N0} excl, " +
            $"pureStaticDialogs={manifest.PureStaticDialogCandidates:N0}) -> '{migrationDir}'.");
        return manifest;
    }

    private static int RunAuditSettings(string legacyRoot, string packRoot, string migrationDir)
    {
        var auditor = new SettingsAndMapsAuditor();
        SettingsAuditReport report = auditor.Audit(legacyRoot, packRoot);
        auditor.WriteOutputs(report, migrationDir);
        Console.WriteLine(
            $"[JXReborn.ScriptMigrator] Audited {report.TotalSettingsFiles:N0} settings files & " +
            $"{report.ExportedPackMapCount:N0}/{report.MapListDeclaredCount:N0} maplist.ini maps " +
            $"(missing={report.MissingMapIds.Count:N0}) -> '{migrationDir}'.");
        return 0;
    }

    private static int RunAuditCoverage(string legacyRoot, string packRoot, string migrationDir, bool strict)
    {
        var builder = new ScriptInventoryBuilder();
        ScriptInventoryManifest manifest = builder.Build(legacyRoot);
        return EvaluateAndWriteCoverage(manifest, packRoot, migrationDir, strict);
    }

    private static int EvaluateAndWriteCoverage(
        ScriptInventoryManifest manifest,
        string packRoot,
        string migrationDir,
        bool strict)
    {
        var auditor = new PackCoverageAuditor();
        CoverageAuditReport report = auditor.Audit(manifest, packRoot);
        auditor.WriteOutputs(report, migrationDir);

        Console.WriteLine(
            $"[JXReborn.ScriptMigrator] Coverage Audit: NPC={report.ResolvedMapNpcScriptPaths}/{report.UniqueMapNpcScriptPaths}, " +
            $"Obj={report.ResolvedMapObjectScriptPaths}/{report.UniqueMapObjectScriptPaths}, " +
            $"Item={report.ResolvedActiveItemScriptPaths}/{report.UniqueActiveItemScriptPaths}, " +
            $"Broken={report.BrokenScriptPaths}, Unaccounted={report.UnaccountedLuaFiles}.");

        if (strict && (report.BrokenScriptPaths > 0 || report.UnaccountedLuaFiles > 0))
        {
            Console.Error.WriteLine("[JXReborn.ScriptMigrator] STRICT COVERAGE GATE FAILED.");
            return 1;
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: JXReborn.ScriptMigrator <command> [--legacy <dir>] [--pack <dir>] [--out <dir>] [--strict]");
        Console.WriteLine("Commands: export-utf8-mirror | build-manifest | audit-settings | audit-coverage | run-all");
    }
}
