using System;
using System.IO;
using System.Text.Json;
using JXReborn.LegacyImporter.Exporters;
using JXReborn.LegacyImporter.Sources;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Exporters;

public sealed class SystemSettingsDataExporterTests
{
    [Fact]
    public void ExportCoreSystemSettings_FromRealLegacyServer_ShouldExportAllEightTablesWithoutCommentKeys()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string tempDataDir = Path.Combine(Path.GetTempPath(), $"jx-sys-settings-test-{Guid.NewGuid():N}");

        try
        {
            ILegacyServerSource source = LegacyServerSourceFactory.Create(legacyBin);
            var exporter = new SystemSettingsDataExporter();
            int count = exporter.ExportCoreSystemSettings(source, tempDataDir);

            Assert.Equal(8, count);

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
            ];
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

            // 3. progressconfig.txt -> 25 progress bars
            using (JsonDocument progDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "progress-bars.json"))))
            {
                JsonElement entries = progDoc.RootElement.GetProperty("entries");
                Assert.Equal(25, entries.GetArrayLength());
                Assert.Equal(1, entries[0].GetProperty("id").GetInt32());
                Assert.Equal(3, entries[0].GetProperty("durationSeconds").GetInt32());
            }

            // 4. weapon-physics-skills.json -> 92 rows (-1, 0 -> 53)
            using (JsonDocument weaponDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "weapon-physics-skills.json"))))
            {
                JsonElement entries = weaponDoc.RootElement.GetProperty("entries");
                Assert.Equal(92, entries.GetArrayLength());
                Assert.Equal(-1, entries[0].GetProperty("detailType").GetInt32());
                Assert.Equal(0, entries[0].GetProperty("particularType").GetInt32());
                Assert.Equal(53, entries[0].GetProperty("physicsSkillId").GetInt32());
            }

            // 5. tongname.flt -> 4 patterns
            using (JsonDocument tongDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "tong-name-filter.json"))))
            {
                JsonElement entries = tongDoc.RootElement.GetProperty("entries");
                Assert.Equal(4, entries.GetArrayLength());
                Assert.Equal("@+GM", entries[0].GetString());
            }

            // 6. map-item-rules.json
            using (JsonDocument rulesDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "map-item-rules.json"))))
            {
                JsonElement data = rulesDoc.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("mapTypes").GetArrayLength() > 0);
                Assert.True(data.GetProperty("itemTypes").GetArrayLength() > 0);
                Assert.True(data.GetProperty("itemDetails").GetArrayLength() > 0);
            }

            // 7. taxrates.ini
            using (JsonDocument taxDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "tax-rates.json"))))
            {
                JsonElement main = taxDoc.RootElement.GetProperty("data").GetProperty("Main");
                Assert.Equal(10, main.GetProperty("CommonRate").GetInt32());
                Assert.Equal(10, main.GetProperty("MinPlayerLevelAllowed").GetInt32());
            }

            // 8. gamesetting.ini — Lua-style '--' comments must NOT leak into JSON keys
            using (JsonDocument gsDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sysDir, "game-settings.json"))))
            {
                JsonElement data = gsDoc.RootElement.GetProperty("data");
                Assert.Equal(50, data.GetProperty("SYSTEM").GetProperty("STUNID").GetInt32());
                JsonElement shop = data.GetProperty("SHOP");
                Assert.Equal(3, shop.GetProperty("nCurrtype").GetInt32());

                foreach (JsonProperty section in data.EnumerateObject())
                {
                    foreach (JsonProperty prop in section.Value.EnumerateObject())
                    {
                        Assert.False(
                            prop.Name.StartsWith("--", StringComparison.Ordinal),
                            $"INI comment leaked as JSON key '{section.Name}.{prop.Name}'.");
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDataDir))
            {
                Directory.Delete(tempDataDir, recursive: true);
            }
        }
    }
}
