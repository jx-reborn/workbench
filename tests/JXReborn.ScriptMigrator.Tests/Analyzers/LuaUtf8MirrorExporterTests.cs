using System.IO;
using System.Text;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Analyzers;

public sealed class LuaUtf8MirrorExporterTests
{
    [Fact]
    public void ConvertLuaBytesToUtf8_FromRealTieuDaoTuLuaLine111_ShouldDecodeCleanVietnameseWithoutGbkMojibake()
    {
        // Ground truth: server1/script/global/tieudaotu.lua line 111 contains TCVN3 bytes with 4 consecutive
        // high bytes (\xae\xad\xad\xee = "đưực"). HybridLegacyTextDecoder must NOT force GBK mojibake here.
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string path = Path.Combine(legacyBin, "server1", "script", "global", "tieudaotu.lua");
        Assert.True(File.Exists(path));

        string utf8 = LuaUtf8MirrorExporter.ConvertLuaBytesToUtf8(File.ReadAllBytes(path), "server1/script/global/tieudaotu.lua");

        Assert.Contains("-- [JX-LINUX SOURCE] server1/script/global/tieudaotu.lua", utf8);
        Assert.Contains("Đồ nhi, ta đã trao cho con 3 món bảo vật", utf8);
        Assert.DoesNotContain("у nhi", utf8);
    }

    [Fact]
    public void ConvertLuaBytesToUtf8_FromRealPartnerHeadMixedGbkLinkAndTcvn3Text_ShouldPreserveBothSegmentsCleanly()
    {
        // Ground truth: server1/script/task/partner/partner_head.lua embeds GBK sprite path inside <link=...>
        // followed by TCVN3 Vietnamese display text "Bạn đồng hành".
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string path = Path.Combine(legacyBin, "server1", "script", "task", "partner", "partner_head.lua");
        Assert.True(File.Exists(path));

        string utf8 = LuaUtf8MirrorExporter.ConvertLuaBytesToUtf8(File.ReadAllBytes(path), "server1/script/task/partner/partner_head.lua");

        Assert.Contains("同伴任务系统", utf8);
        Assert.Contains(">Bạn đồng hành<link>", utf8);
    }

    [Fact]
    public void ConvertLuaBytesToUtf8_WithTcvn3Escapes_ShouldDecodeAndPreserveLuaSyntax()
    {
        const string rawLua = """
            function main()
                Talk(1, "", "B\184 L\168ng Huy\214n")
            end
            """;
        byte[] rawBytes = Encoding.Latin1.GetBytes(rawLua);

        string utf8 = LuaUtf8MirrorExporter.ConvertLuaBytesToUtf8(rawBytes, "server1/script/test.lua");

        Assert.Contains("-- [JX-LINUX SOURCE] server1/script/test.lua", utf8);
        Assert.Contains("function main()", utf8);
        Assert.Contains("Bá Lăng Huyện", utf8);
        Assert.Contains("end", utf8);
    }

    [Fact]
    public void ConvertLuaBytesToUtf8_WithGbkComment_ShouldDecodeWithoutCorruptingCode()
    {
        byte[] prefix = Encoding.ASCII.GetBytes("-- ");
        byte[] gbk = [0xC8, 0xCE, 0xCE, 0xF1];
        byte[] suffix = Encoding.ASCII.GetBytes("\nfunction main() return 1 end\n");
        byte[] raw = [.. prefix, .. gbk, .. suffix];

        string utf8 = LuaUtf8MirrorExporter.ConvertLuaBytesToUtf8(raw);

        Assert.Contains("-- 任务", utf8);
        Assert.Contains("function main() return 1 end", utf8);
    }

    [Fact]
    public void ConvertLuaBytesToUtf8_WhenAlreadyValidUtf8_ShouldPreserveStrings()
    {
        byte[] raw = Encoding.UTF8.GetBytes("function main() Msg2Player(\"Chào mừng đến Tiêu Dao Tử\") end");

        string utf8 = LuaUtf8MirrorExporter.ConvertLuaBytesToUtf8(raw);

        Assert.Contains("Chào mừng đến Tiêu Dao Tử", utf8);
    }

    [Fact]
    public void DecodePathToUtf8_WithNtfsCp1252AndDoubleMojibake_ShouldRecoverOriginalCharacters()
    {
        string decodedCp1252 = ScriptInventoryBuilder.DecodePathToUtf8("server1/script/Â·ÈË5Ê¯\u2020´¶Ô»°.lua");
        Assert.Equal("server1/script/路人5石喆对话.lua", decodedCp1252);

        string decodedGbk = ScriptInventoryBuilder.DecodePathToUtf8("server1/script/Ø¤°ï°ïÖúnpc.lua");
        Assert.Equal("server1/script/丐帮帮助npc.lua", decodedGbk);
    }
}
