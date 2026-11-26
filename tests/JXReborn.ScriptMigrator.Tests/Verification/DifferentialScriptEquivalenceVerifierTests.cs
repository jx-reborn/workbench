using System;
using System.IO;
using System.Threading.Tasks;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using JXReborn.ScriptMigrator.Verification;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Verification;

public sealed class DifferentialScriptEquivalenceVerifierTests
{
    private static readonly Lazy<ScriptInventoryManifest> CachedManifest = new(() =>
    {
        var builder = new ScriptInventoryBuilder();
        return builder.Build(LegacyServerTestPaths.FindLegacyBinRoot());
    });

    [Fact]
    public async Task VerifyPackAsync_AgainstRealLegacyCorpusAndPackTieuDaoTu_ShouldPassWithZeroMismatches()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        var verifier = new DifferentialScriptEquivalenceVerifier();
        EquivalenceAuditReport report = await verifier.VerifyPackAsync(
            CachedManifest.Value,
            legacyBin,
            packRoot,
            TestContext.Current.CancellationToken);

        Assert.True(
            report.VerifiedGeneratedScripts >= 1400,
            $"Expected >= 1400 verified generated C# scripts, got {report.VerifiedGeneratedScripts}.");
        Assert.True(
            report.VerifiedAmbientDialogs >= 500,
            $"Expected >= 500 verified ambient dialogs, got {report.VerifiedAmbientDialogs}.");
        Assert.True(report.TotalStateVectorsExecuted >= 2500);
        Assert.Equal(0, report.MismatchedScripts);
        Assert.Empty(report.Mismatches);
    }

    [Fact]
    public async Task VerifySingleDialogScriptAsync_WhenCSharpDivergesFromRealLegacyLua_ShouldReportExactMismatch()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string realLuaFile = Path.Combine(legacyBin, "server1", "script", "changefeature", "npc", "box.lua");
        Assert.True(File.Exists(realLuaFile));

        var verifier = new DifferentialScriptEquivalenceVerifier();
        EquivalenceMismatchRecord? mismatch = await verifier.VerifySingleDialogScriptAsync(
            realLuaFile,
            @"\script\changefeature\npc\box.lua",
            new SabotagedNpcDialogScript(),
            TestContext.Current.CancellationToken);

        Assert.NotNull(mismatch);
        Assert.Contains("999", mismatch.ActualCSharpTrace, StringComparison.Ordinal);
        Assert.NotEqual(mismatch.ExpectedLuaTrace, mismatch.ActualCSharpTrace);
    }

    [Fact]
    public async Task VerifyScriptByLuaPathAsync_OnHandwrittenAndGeneratedScriptsWithNonEmptyNoCallback_ShouldMatchAllVectors()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();
        var verifier = new DifferentialScriptEquivalenceVerifier();

        // 1. Handwritten quest script in non-Generated namespace with non-empty `function no()` (`Talk(1, "", "Thật là làm phiền cháu quá. ")`)
        var (coTotal, coMatched, coName, coMismatches) = await verifier.VerifyScriptByLuaPathAsync(
            legacyBin,
            packRoot,
            @"\script\两湖区\巴陵县\npc\巴陵县_路人2古老太对话.lua",
            TestContext.Current.CancellationToken);
        Assert.True(coTotal >= 6);
        Assert.Equal(coTotal, coMatched);
        Assert.Contains("BaLangCoLaoThaiDialogScript", coName, StringComparison.Ordinal);
        Assert.Empty(coMismatches);

        // 2. Generated NPC script in Dương Châu (`扬州_路人_婉娘.lua`) with non-empty `function no()` (`Say("Không mua cũng không sao!...", 0)`)
        var (wanTotal, wanMatched, _, wanMismatches) = await verifier.VerifyScriptByLuaPathAsync(
            legacyBin,
            packRoot,
            @"\script\中原南区\扬州\npc\扬州_路人_婉娘.lua",
            TestContext.Current.CancellationToken);
        Assert.True(wanTotal >= 2);
        Assert.Equal(wanTotal, wanMatched);
        Assert.Empty(wanMismatches);
    }
}
