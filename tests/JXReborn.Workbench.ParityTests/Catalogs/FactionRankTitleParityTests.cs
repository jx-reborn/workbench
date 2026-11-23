using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JXReborn.LegacyImporter.Exporters;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Primitives.Encodings;
using Xunit;

namespace JXReborn.Workbench.ParityTests.Catalogs;

public sealed class FactionRankTitleParityTests
{
    [Fact]
    public void PlayerDataExporter_WithLegacyFactionIniFactionTitleAndRankSetting_ShouldExportHuaShanTitles82To89AndDisjointFactionRanks()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"jx-rank-parity-{Guid.NewGuid():N}");
        string serverSettingsDir = Path.Combine(tempRoot, "server1", "settings");
        string factionDir = Path.Combine(serverSettingsDir, "faction");
        string outputDir = Path.Combine(tempRoot, "out-data");
        Directory.CreateDirectory(factionDir);
        Directory.CreateDirectory(outputDir);

        try
        {
            var iniText = new StringBuilder();
            (int Id, string RawName, string ShowName, string Series, string Camp)[] factions =
            [
                (0, "shaolin", "Thiếu Lâm phái", "S_GOLD", "C_JUSTICE"),
                (1, "tianwang", "Thiên Vương Bang", "S_GOLD", "C_BALANCE"),
                (2, "tangmen", "Đường Môn", "S_WOOD", "C_BALANCE"),
                (3, "wudu", "Ngũ Độc Giáo", "S_WOOD", "C_EVIL"),
                (4, "emei", "Nga My phái", "S_WATER", "C_JUSTICE"),
                (5, "cuiyan", "Thúy Yên môn", "S_WATER", "C_BALANCE"),
                (6, "gaibang", "Cái Bang", "S_FIRE", "C_JUSTICE"),
                (7, "tianren", "Thiên Nhẫn Giáo", "S_FIRE", "C_EVIL"),
                (8, "wudang", "Võ Đang phái", "S_EARTH", "C_JUSTICE"),
                (9, "kunlun", "Côn Lôn phái", "S_EARTH", "C_BALANCE"),
                (10, "huashan", "Hoa Sơn phái", "S_WATER", "C_BALANCE"),
            ];

            Assert.NotEmpty(factions);
            foreach (var f in factions)
            {
                iniText.AppendLine($"[{f.Id}]");
                iniText.AppendLine($"Name={f.RawName}");
                iniText.AppendLine($"ShowName={f.ShowName}");
                iniText.AppendLine($"Series={f.Series}");
                iniText.AppendLine($"Camp={f.Camp}");
                iniText.AppendLine();
            }

            File.WriteAllBytes(
                Path.Combine(factionDir, "faction.ini"),
                Tcvn3Encoding.EncodeBytes(iniText.ToString()));

            var factionTitleText = new StringBuilder();
            factionTitleText.AppendLine("RANKID\tRANKSTR\tFACTION");
            factionTitleText.AppendLine("1\tTảo Địa Tăng\t1");
            factionTitleText.AppendLine("37\tVô Đại Đệ Tử\t7");
            File.WriteAllBytes(
                Path.Combine(serverSettingsDir, "factiontitle.txt"),
                Tcvn3Encoding.EncodeBytes(factionTitleText.ToString()));

            var rankSettingText = new StringBuilder();
            rankSettingText.AppendLine("RANKID\tRANKSTR");
            rankSettingText.AppendLine("1\tTảo Địa Tăng");
            rankSettingText.AppendLine("37\tVô Đại Đệ Tử");
            rankSettingText.AppendLine("82\tThư Đồng");
            rankSettingText.AppendLine("83\tĐệ tử phụng trà");
            rankSettingText.AppendLine("84\tĐệ tử Tư Mặc");
            rankSettingText.AppendLine("85\tĐệ tử chấp kiếm");
            rankSettingText.AppendLine("86\tĐan Thanh Sứ");
            rankSettingText.AppendLine("87\tDịch Thu Sứ");
            rankSettingText.AppendLine("88\tLục Nghệ Truyền Nhân");
            rankSettingText.AppendLine("89\tThái Học Các Chủ");
            File.WriteAllBytes(
                Path.Combine(serverSettingsDir, "ranksetting.txt"),
                Tcvn3Encoding.EncodeBytes(rankSettingText.ToString()));

            ILegacyServerSource source = LegacyServerSourceFactory.Create(tempRoot);
            var registry = new ExportCatalogRegistry();
            _ = new PlayerDataExporter().ExportPlayers(source, outputDir, registry);

            string jsonPath = Path.Combine(outputDir, "players", "progression.players.json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
            JsonElement factionsArr = doc.RootElement.GetProperty("factions");

            Assert.Equal(11, factionsArr.GetArrayLength());
            Dictionary<int, List<(int RankId, string TitleName)>> titlesByFaction = [];
            foreach (JsonElement f in factionsArr.EnumerateArray())
            {
                int id = f.GetProperty("id").GetInt32();
                var list = new List<(int RankId, string TitleName)>();
                if (f.TryGetProperty("titles", out JsonElement tArr))
                {
                    Assert.NotEmpty(tArr.EnumerateArray());
                    foreach (JsonElement t in tArr.EnumerateArray())
                    {
                        list.Add((t.GetProperty("rankId").GetInt32(), t.GetProperty("titleName").GetString() ?? string.Empty));
                    }
                }

                titlesByFaction[id] = list;
            }

            Assert.Equal([(1, "Tảo Địa Tăng")], titlesByFaction[0]);
            Assert.Equal([(37, "Vô Đại Đệ Tử")], titlesByFaction[6]);
            Assert.DoesNotContain(titlesByFaction[6], t => t.RankId == 1);

            (int RankId, string TitleName)[] expectedHuaShan =
            [
                (82, "Thư Đồng"),
                (83, "Đệ tử phụng trà"),
                (84, "Đệ tử Tư Mặc"),
                (85, "Đệ tử chấp kiếm"),
                (86, "Đan Thanh Sứ"),
                (87, "Dịch Thu Sứ"),
                (88, "Lục Nghệ Truyền Nhân"),
                (89, "Thái Học Các Chủ"),
            ];
            Assert.Equal(expectedHuaShan, titlesByFaction[10]);

            Assert.Equal(
                registry.LegacyRecordCounts["players.factionTitles"],
                registry.ExportedRecordCounts["players.factionTitles"]);
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
