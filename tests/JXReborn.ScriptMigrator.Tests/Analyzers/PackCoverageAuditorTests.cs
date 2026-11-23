using System;
using System.IO;
using System.Text;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Models;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Analyzers;

public sealed class PackCoverageAuditorTests
{
    [Fact]
    public void Audit_WhenPackReferencesMissingScript_ShouldDetectBrokenAndUnresolvedPaths()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "jxreborn-migrator-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string legacyBin = Path.Combine(tempRoot, "legacy");
            string packRoot = Path.Combine(tempRoot, "pack");

            Directory.CreateDirectory(Path.Combine(legacyBin, "server1", "script", "global"));
            File.WriteAllText(
                Path.Combine(legacyBin, "server1", "script", "global", "station.lua"),
                "function main() Say(\"Hi\", 0) end",
                Encoding.Latin1);

            Directory.CreateDirectory(Path.Combine(packRoot, "data", "maps"));
            File.WriteAllText(
                Path.Combine(packRoot, "data", "maps", "053_test.map.json"),
                """
                {
                  "spawnGroups": [
                    { "scriptPath": "\\script\\global\\station.lua" },
                    { "scriptPath": "\\script\\missing\\broken_npc.lua" }
                  ],
                  "mapObjects": [],
                  "portals": []
                }
                """);

            var builder = new ScriptInventoryBuilder();
            ScriptInventoryManifest manifest = builder.Build(legacyBin);

            var auditor = new PackCoverageAuditor();
            CoverageAuditReport report = auditor.Audit(manifest, packRoot);

            Assert.Equal(1, report.TotalLegacyActiveScripts);
            Assert.Equal(2, report.UniqueMapNpcScriptPaths);
            Assert.Equal(1, report.BrokenScriptPaths);
            Assert.Contains("\\script\\missing\\broken_npc.lua", report.BrokenPathsSample);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
