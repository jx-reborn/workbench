using System.Text;
using JXReborn.ScriptMigrator.Analyzers;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests;

public sealed class LuaUtf8MirrorExporterTests
{
    [Fact]
    public void ConvertLuaBytesToUtf8_DecodesTcvn3EscapesAndPreservesLuaSyntax()
    {
        // "B\184 L\168ng Huy\214n" -> "Bá Lăng Huyện" in TCVN3 decimal escapes
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
    public void ConvertLuaBytesToUtf8_DecodesGbkCommentWithoutCorruptingCode()
    {
        // GBK bytes for "任务" = C8 CE CE F1
        byte[] prefix = Encoding.ASCII.GetBytes("-- ");
        byte[] gbk = [0xC8, 0xCE, 0xCE, 0xF1];
        byte[] suffix = Encoding.ASCII.GetBytes("\nfunction main() return 1 end\n");
        byte[] raw = [.. prefix, .. gbk, .. suffix];

        string utf8 = LuaUtf8MirrorExporter.ConvertLuaBytesToUtf8(raw);

        Assert.Contains("-- 任务", utf8);
        Assert.Contains("function main() return 1 end", utf8);
    }

    [Fact]
    public void ConvertLuaBytesToUtf8_PreservesExistingValidUtf8Strings()
    {
        byte[] raw = Encoding.UTF8.GetBytes("function main() Msg2Player(\"Chào mừng đến Tiêu Dao Tử\") end");

        string utf8 = LuaUtf8MirrorExporter.ConvertLuaBytesToUtf8(raw);

        Assert.Contains("Chào mừng đến Tiêu Dao Tử", utf8);
    }

    [Fact]
    public void DecodePathToUtf8_RecoversNtfsCp1252AndDoubleMojibake()
    {
        // "Â·ÈË5Ê¯†´¶Ô»°.lua" where † is U+2020 (CP1252 byte 0x86) -> "路人5石喆对话.lua"
        string decodedCp1252 = ScriptInventoryBuilder.DecodePathToUtf8("server1/script/Â·ÈË5Ê¯\u2020´¶Ô»°.lua");
        Assert.Equal("server1/script/路人5石喆对话.lua", decodedCp1252);

        // "Ø¤°ï°ïÖúnpc.lua" -> "丐帮帮助npc.lua" (must decode as GBK, not Vietnamese TCVN3)
        string decodedGbk = ScriptInventoryBuilder.DecodePathToUtf8("server1/script/Ø¤°ï°ïÖúnpc.lua");
        Assert.Equal("server1/script/丐帮帮助npc.lua", decodedGbk);
    }
}
