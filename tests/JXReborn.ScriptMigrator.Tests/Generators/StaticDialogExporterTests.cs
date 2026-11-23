using System.Collections.Generic;
using System.IO;
using System.Text;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Generators;

public sealed class StaticDialogExporterTests
{
    [Fact]
    public void TryExtractDialog_FromRealLinAnTenPageTalkLua_ShouldExtractAllTenPagesInOrder()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string filePath = Path.Combine(
            legacyBin,
            "server1",
            "script",
            "½­ÄÏÇø",
            "ÁÙ°²",
            "ÁÙ°²",
            "npc",
            "Â·ÈË_¸»ÉÙ&Ð¡Èï.lua");
        Assert.True(File.Exists(filePath), $"Expected ground-truth Lua file at '{filePath}'.");

        bool ok = StaticDialogExporter.TryExtractDialog(File.ReadAllBytes(filePath), out string mode, out List<string> lines);

        Assert.True(ok);
        Assert.Equal("talk", mode);
        Assert.Equal(10, lines.Count);
        Assert.StartsWith("Tiểu Nhụy! Bổn Công Tử đặc biệt đến đây để nghe ngươi hát đó!", lines[0]);
        Assert.Contains("Vậy thì em sẽ hát đoạn: 'Ngọc thanh đình - Miêu Dung' nhé!", lines[3]);
        Assert.StartsWith("Trên tay cầm chiếc quạt 'tương kỷ'", lines[9]);
    }

    [Fact]
    public void TryExtractDialog_FromRealXiangyangFourPageTalkLua_ShouldExtractFourPagesInOrder()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string filePath = Path.Combine(
            legacyBin,
            "server1",
            "script",
            "ÖÐÔ­ÄÏÇø",
            "ÏåÑô",
            "ÏåÑô",
            "npc",
            "ÏåÑô_Â·ÈË14¼ÖÉñËã¶Ô»°.lua");
        Assert.True(File.Exists(filePath), $"Expected ground-truth Lua file at '{filePath}'.");

        bool ok = StaticDialogExporter.TryExtractDialog(File.ReadAllBytes(filePath), out string mode, out List<string> lines);

        Assert.True(ok);
        Assert.Equal("talk", mode);
        Assert.Equal(4, lines.Count);
        Assert.Equal("Ta đoán chuyện như thần, ngươi muốn hỏi chuyện chi?", lines[0]);
        Assert.StartsWith("Ha..ha.., cụ thể phải tốn bao nhiêu tiền", lines[3]);
    }

    [Fact]
    public void TryExtractDialog_FromRealBaLangSayLua_ShouldExtractExactVietnameseSayLine()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string filePath = Path.Combine(
            legacyBin,
            "server1",
            "script",
            "Á½ºþÇø",
            "°ÍÁêÏØ",
            "npc",
            "°ÍÁêÏØ_Â·ÈË1¾Å½ïÀÏµù¶Ô»°.lua");
        Assert.True(File.Exists(filePath), $"Expected ground-truth Lua file at '{filePath}'.");

        bool ok = StaticDialogExporter.TryExtractDialog(File.ReadAllBytes(filePath), out string mode, out List<string> lines);

        Assert.True(ok);
        Assert.Equal("say", mode);
        Assert.Single(lines);
        Assert.Equal(
            "Giang hồ lưỡng hại, thập niên cửu tai! Mỗi năm cứ đến mùa mưa, Động Đình hồ lại dâng nước, Ba Lăng huyện biến thành đê giữa biển nước, mỗi năm không biết có bao nhiêu người chết!",
            lines[0]);
    }

    [Fact]
    public void TryExtractDialog_WithTableRandomSay_ShouldExtractAllTableLines()
    {
        byte[] lua = Encoding.UTF8.GetBytes("""
            function main()
                local tb = { "Câu một", "Câu hai" }
                Say(tb[random(1, getn(tb))], 0)
            end
            """);

        bool ok = StaticDialogExporter.TryExtractDialog(lua, out string mode, out List<string> lines);

        Assert.True(ok);
        Assert.Equal("say", mode);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Câu một", lines[0]);
        Assert.Equal("Câu hai", lines[1]);
    }
}
