using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using JXReborn.LegacyImporter.Exporters;
using JXReborn.LegacyImporter.Sources;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Exporters;

public sealed class SystemSettingsDataExporterTests
{
    [Fact]
    public void ExportCoreSystemSettings_FromRealLegacyServer_ShouldExportAllSixteenTablesWithoutCommentKeysAndZeroUncoveredRootSettings()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();
        string tempDataDir = Path.Combine(Path.GetTempPath(), $"jx-sys-settings-test-{Guid.NewGuid():N}");

        try
        {
            ILegacyServerSource source = LegacyServerSourceFactory.Create(legacyBin);
            var exporter = new SystemSettingsDataExporter();
            int count = exporter.ExportCoreSystemSettings(source, tempDataDir);

            Assert.Equal(16, count);

            string sysDir = Path.Combine(tempDataDir, "system");
            string[] expectedFiles =
            [
                "timer-tasks.json",
                "player-titles.json",
                "tax-rates.json",
                "map-item-rules.json",
                "game-settings.json",
                "progress-bars.json",
                "weapon-physics-skills.json",
                "tong-name-filter.json",
                "golden-boss-schedules.json",
                "killer-boss-settings.json",
                "citywar-settings.json",
                "map-navigation.json",
                "thief-skills.json",
                "lottery-settings.json",
                "fatigue-settings.json",
                "market-and-dialog-permits.json",
            ];
            Assert.NotEmpty(expectedFiles);
            foreach (string f in expectedFiles)
            {
                Assert.True(File.Exists(Path.Combine(sysDir, f)), $"Missing exported file '{f}'.");
            }

            // 1. timertask.txt -> 114 rows
            using (JsonDocument timerDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "timer-tasks.json"))))
            {
                JsonElement entries = timerDoc.RootElement.GetProperty("entries");
                Assert.Equal(114, entries.GetArrayLength());
                Assert.Equal(1, entries[0].GetProperty("timerTaskId").GetInt32());
                Assert.Contains("tw_ontime.lua", entries[0].GetProperty("scriptPath").GetString(), StringComparison.OrdinalIgnoreCase);
            }

            // 2. playertitle.txt -> 383 titles
            using (JsonDocument titleDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "player-titles.json"))))
            {
                JsonElement entries = titleDoc.RootElement.GetProperty("entries");
                Assert.Equal(383, entries.GetArrayLength());
                Assert.Equal(1, entries[0].GetProperty("titleId").GetInt32());
                Assert.Equal("Binh sĩ", entries[0].GetProperty("name").GetString());
            }

            // 3. gamesetting.ini — Lua-style '--' or quoted '";' comments must NOT leak into JSON keys
            using (JsonDocument gsDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "game-settings.json"))))
            {
                JsonElement data = gsDoc.RootElement.GetProperty("data");
                Assert.Equal(50, data.GetProperty("SYSTEM").GetProperty("STUNID").GetInt32());
                Assert.Equal(3, data.GetProperty("SHOP").GetProperty("nCurrtype").GetInt32());
                AssertNoLeakedCommentKeys(data);
            }

            // 4. goldboss.txt -> Trương Tông Chính + verified SHA-256
            using (JsonDocument gbDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "golden-boss-schedules.json"))))
            {
                AssertExpectedSha256(legacyBin, "goldboss.txt", gbDoc.RootElement.GetProperty("sourceSha256").GetString());
                JsonElement entries = gbDoc.RootElement.GetProperty("entries");
                Assert.True(entries.GetArrayLength() >= 20);
                Assert.Equal("Trương Tông Chính", entries[0].GetProperty("name").GetString());
            }

            // 5. killer.ini + adventure.txt -> MoneyPerHour=10000, MinTargetLevel=50, 1038 spawn coordinates, zero '";' comment keys
            using (JsonDocument killerDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "killer-boss-settings.json"))))
            {
                JsonElement data = killerDoc.RootElement.GetProperty("data");
                JsonElement ini = data.GetProperty("config");
                AssertNoLeakedCommentKeys(ini);
                Assert.Equal(10000, ini.GetProperty("Main").GetProperty("MoneyPerHour").GetInt32());
                Assert.Equal(50, ini.GetProperty("Main").GetProperty("MinTargetLevel").GetInt32());
                // adventure.txt has 1 header line + 1,037 coordinate rows on disk
                JsonElement positions = data.GetProperty("adventurePositions");
                Assert.Equal(1037, positions.GetArrayLength());
                Assert.Equal(43, positions[0].GetProperty("mapId").GetInt32());
                Assert.Equal(1675, positions[0].GetProperty("cellX").GetInt32());
                Assert.Equal(2708, positions[0].GetProperty("cellY").GetInt32());
            }

            // 6. thiefskill.txt -> 4 skills (400..403)
            using (JsonDocument thiefDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "thief-skills.json"))))
            {
                AssertExpectedSha256(legacyBin, "thiefskill.txt", thiefDoc.RootElement.GetProperty("sourceSha256").GetString());
                JsonElement entries = thiefDoc.RootElement.GetProperty("entries");
                Assert.Equal(4, entries.GetArrayLength());
                Assert.Equal(400, entries[0].GetProperty("skillId").GetInt32());
                Assert.Equal("Kiếp Phú Tế Bần", entries[0].GetProperty("name").GetString());
            }

            // 7. fatigue-settings.json (player_limittime.ini + tiredwarning.ini)
            using (JsonDocument fatDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "fatigue-settings.json"))))
            {
                JsonElement data = fatDoc.RootElement.GetProperty("data");
                Assert.Equal(28800, data.GetProperty("tiredWarning").GetProperty("Config").GetProperty("TiredTime").GetInt32());
            }

            // 8. market-and-dialog-permits.json (auction.ini + stallscript_permit.ini + permitdialognpc_info.txt)
            using (JsonDocument mktDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "market-and-dialog-permits.json"))))
            {
                JsonElement data = mktDoc.RootElement.GetProperty("data");
                Assert.Equal(1000000, data.GetProperty("auction").GetProperty("Main").GetProperty("MinBasePrice").GetInt32());
                JsonElement permittedNpcs = data.GetProperty("permittedDialogNpcs");
                Assert.True(permittedNpcs.GetArrayLength() >= 140);
                Assert.Equal("Tiền sứ Vương Tá", permittedNpcs[0].GetProperty("npcName").GetString());
                Assert.Equal(60, permittedNpcs[0].GetProperty("mapId").GetInt32());
            }

            // 9. SettingsAndMapsAuditor must report ZERO uncovered root settings files
            var auditor = new SettingsAndMapsAuditor();
            SettingsAuditReport auditReport = auditor.Audit(legacyBin, packRoot);
            Assert.Empty(auditReport.UncoveredRootSettingsFiles);
        }
        finally
        {
            if (Directory.Exists(tempDataDir))
            {
                Directory.Delete(tempDataDir, recursive: true);
            }
        }
    }

    private static void AssertExpectedSha256(string legacyBin, string rootSettingsFileName, string? actualHex)
    {
        string path = Path.Combine(legacyBin, "server1", "settings", rootSettingsFileName);
        Assert.True(File.Exists(path));
        string expectedHex = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(expectedHex, actualHex);
    }

    private static void AssertNoLeakedCommentKeys(JsonElement sectionsObj)
    {
        var sections = sectionsObj.EnumerateObject();
        bool anySection = false;
        foreach (JsonProperty section in sections)
        {
            anySection = true;
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty prop in section.Value.EnumerateObject())
            {
                Assert.False(
                    prop.Name.StartsWith("--", StringComparison.Ordinal) ||
                    prop.Name.StartsWith(';') ||
                    prop.Name.StartsWith("\";", StringComparison.Ordinal),
                    $"INI comment leaked as JSON key '{section.Name}.{prop.Name}'.");
            }
        }

        Assert.True(anySection);
    }
}
