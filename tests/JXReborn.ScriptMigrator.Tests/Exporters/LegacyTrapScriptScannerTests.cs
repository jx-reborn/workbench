using System;
using System.Collections.Frozen;
using System.IO;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Primitives.Encodings;
using JXReborn.Primitives.WorldZones;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Exporters;

public sealed class LegacyTrapScriptScannerTests
{
    [Fact]
    public void ScanServerRoot_AgainstRealLegacyCorpus_ShouldResolveConnectingTrapsCustomScriptTrapsAndEmitCleanUtf8Paths()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        ILegacyServerSource source = LegacyServerSourceFactory.Create(legacyBin);

        FrozenDictionary<uint, PortalTrapBindingTemplate> bindings = LegacyTrapScriptScanner.ScanServerRoot(source);
        Assert.NotEmpty(bindings);

        // 1. Connecting trap under \连接trap\ (previously skipped because folder name is \Á¬½Ótrap\, not \trap\)
        const string connectingTrapUtf8 = @"\script\西北南区\凤翔\连接trap\凤翔to永乐镇.lua";
        uint connectingTrapHash = PakFilePathHasher.ComputeHash(GbkEncoding.Instance.GetBytes(connectingTrapUtf8));
        Assert.True(
            bindings.TryGetValue(connectingTrapHash, out PortalTrapBindingTemplate? connectingBinding),
            $"Expected connecting trap hash 0x{connectingTrapHash:X8} for '{connectingTrapUtf8}' to be resolved.");
        Assert.Equal(connectingTrapUtf8, connectingBinding!.ScriptPath);
        Assert.Equal(PortalTrapActionKind.ChangeMapPortal, connectingBinding.ActionKind);

        // 2. Include/custom trap (Song-Jin signup from Xiangyang — no literal NewWorld(d,d,d) constant call)
        const string songJinSignupUtf8 = @"\script\中原南区\襄阳\襄阳\trap\襄阳to宋金战场.lua";
        uint songJinHash = PakFilePathHasher.ComputeHash(GbkEncoding.Instance.GetBytes(songJinSignupUtf8));
        Assert.True(
            bindings.TryGetValue(songJinHash, out PortalTrapBindingTemplate? songJinBinding),
            $"Expected custom trap hash 0x{songJinHash:X8} for '{songJinSignupUtf8}' to be resolved instead of dropped.");
        Assert.Equal(songJinUtf8Expected(songJinSignupUtf8), songJinBinding!.ScriptPath);
        Assert.Equal(PortalTrapActionKind.CustomScript, songJinBinding.ActionKind);

        // 3. Changbaishan trap must emit clean UTF-8, zero Latin1/CP1252 mojibake
        const string changbaiUtf8 = @"\script\东北区\长白山\长白山北麓\trap\长白山北麓to长白山脚1.lua";
        uint changbaiHash = PakFilePathHasher.ComputeHash(GbkEncoding.Instance.GetBytes(changbaiUtf8));
        Assert.True(bindings.TryGetValue(changbaiHash, out PortalTrapBindingTemplate? changbaiBinding));
        Assert.Equal(changbaiUtf8, changbaiBinding!.ScriptPath);

        foreach (PortalTrapBindingTemplate entry in bindings.Values)
        {
            Assert.False(
                ContainsLatin1MojibakeRun(entry.ScriptPath),
                $"TrapId 0x{entry.TrapId:X8} emitted Latin1 mojibake scriptPath '{entry.ScriptPath}'.");
        }
    }

    private static string songJinUtf8Expected(string path) => path;

    private static bool ContainsLatin1MojibakeRun(string text)
    {
        int latin1HighCount = 0;
        foreach (char ch in text)
        {
            if (ch is >= '\u00A1' and <= '\u00FF')
            {
                latin1HighCount++;
                if (latin1HighCount >= 2)
                {
                    return true;
                }
            }
            else
            {
                latin1HighCount = 0;
            }
        }

        return false;
    }
}
