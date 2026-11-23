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
}
