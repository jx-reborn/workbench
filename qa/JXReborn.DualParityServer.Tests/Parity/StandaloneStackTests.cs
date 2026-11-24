using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using JXReborn.DualParityServer.Configuration;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Reporting;
using JXReborn.DualParityServer.Session;
using JXReborn.DualParityServer.Standalone;
using Xunit;

namespace JXReborn.DualParityServer.Tests.Parity;

public sealed class StandaloneStackTests
{
    [Fact]
    public void ParseArgs_WhenStandaloneEnabled_ShouldEnableSpawnAndIsolateRebornPort()
    {
        DualParityOptions opts = DualParityOptions.ParseArgs(["--standalone", "--reuse-legacy", "--keep-containers"]);

        Assert.True(opts.Standalone);
        Assert.True(opts.SpawnClients);
        Assert.True(opts.ReuseLegacyContainer);
        Assert.True(opts.KeepContainers);
        Assert.Equal(15623, opts.RebornPort);
        Assert.Equal(16667, opts.RebornGameServerPort);
        Assert.Equal(15622, opts.LegacyPort);
        Assert.Equal(16666, opts.LegacyGameServerPort);
    }

    [Fact]
    public void ParseArgs_WhenExplicitRebornPortGiven_ShouldRespectExplicitRebornPort()
    {
        DualParityOptions opts = DualParityOptions.ParseArgs(["--standalone", "--reborn-port", "17777"]);

        Assert.True(opts.Standalone);
        Assert.Equal(17777, opts.RebornPort);
    }

    [Fact]
    public void RingLogBuffer_WhenExceedingCapacity_ShouldKeepMostRecentLines()
    {
        var buffer = new RingLogBuffer(capacity: 3);
        buffer.Append("line-1");
        buffer.Append("line-2");
        buffer.Append("line-3");
        buffer.Append("line-4");

        IReadOnlyList<string> lines = buffer.GetLines(10);
        Assert.Equal(3, lines.Count);
        Assert.Contains("line-2", lines[0]);
        Assert.Contains("line-4", lines[2]);
    }

    [Fact]
    public async Task ExportAsync_WithStandaloneOrchestrator_ShouldWriteServerLogFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"jx-dual-standalone-test-{Guid.NewGuid():N}");
        try
        {
            var options = new DualParityOptions
            {
                Standalone = true,
                ReportOutputDir = tempDir
            };

            await using var session = new DualSessionManager(options, new SemanticDiffEngine());
            await using var standalone = new StandaloneStackOrchestrator(options, session.SessionId);
            var writer = new ParityReportWriter();

            ExportedReportPaths paths = await writer.ExportAsync(session, standalone: standalone);

            Assert.True(File.Exists(Path.Combine(paths.OutputDirectory, "logs", "legacy-server.log")));
            Assert.True(File.Exists(Path.Combine(paths.OutputDirectory, "logs", "reborn-server.log")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RebornHostLauncher_CreateHostProcessStartInfo_ShouldEnableDevelopmentEnvironmentAndFreshWorkbenchScripts()
    {
        var options = new DualParityOptions { Standalone = true };
        var launcher = new RebornHostLauncher(options, "test-session");

        System.Diagnostics.ProcessStartInfo psi = launcher.CreateHostProcessStartInfo("reborn-t0.db");

        Assert.Equal("Development", psi.Environment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Development", psi.Environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("true", psi.Environment["ZoneEngine__EnableTestGmToken"]);
        Assert.Equal("true", psi.Environment["ZoneEngine__AutoGrantGmTokenOnLogin"]);

        string? scriptsDir = psi.Environment["ScriptHost__ScriptDirectories__1"];
        Assert.False(string.IsNullOrWhiteSpace(scriptsDir));
        string resolvedDll = Path.Combine(scriptsDir!, "JXReborn.Workbench.Scripts.dll");
        Assert.True(File.Exists(resolvedDll));

        // Ground truth: GmTokenEnvironmentGuard requires Debug build when available, and Workbench.Scripts must be at least as fresh as Debug bin output.
        string workbenchScriptsDebugDll = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "JXReborn.Workbench.Scripts", "bin", "Debug", "net10.0", "JXReborn.Workbench.Scripts.dll"));
        if (File.Exists(workbenchScriptsDebugDll))
        {
            Assert.True(
                File.GetLastWriteTimeUtc(resolvedDll) >= File.GetLastWriteTimeUtc(workbenchScriptsDebugDll),
                $"Expected '{resolvedDll}' to be at least as fresh as '{workbenchScriptsDebugDll}'.");
        }
    }

    [Fact]
    public void RebornHostLauncher_CreateHostProcessStartInfo_ShouldNeverSelectReleaseHostBinaryBlockedByGmTokenEnvironmentGuard()
    {
        // Ground truth: server/src/JXReborn.Host/Program.cs passes #if DEBUG -> isDebugBuild into GmTokenEnvironmentGuard.Apply,
        // which clamps EnableTestGmToken=false & AutoGrantGmTokenOnLogin=false on any Release host binary (causing mui2-verify
        // Step001_C2S_0x43 Inventory[Place3:(6,1,4258,Lv1)] | L: 1 | R: 0).
        var options = new DualParityOptions { Standalone = true };
        var launcher = new RebornHostLauncher(options, "test-session");

        ProcessStartInfo psi = launcher.CreateHostProcessStartInfo("reborn-t0.db");
        Assert.NotEmpty(psi.ArgumentList);

        string firstArg = psi.ArgumentList[0];
        if (firstArg.EndsWith("JXReborn.Host.dll", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(File.Exists(firstArg));
            DebuggableAttribute.DebuggingModes modes = ReadAssemblyDebuggingModes(firstArg);
            Assert.True(
                (modes & DebuggableAttribute.DebuggingModes.DisableOptimizations) != 0,
                $"Expected Debug host assembly for GmTokenEnvironmentGuard, but selected optimized binary '{firstArg}' (modes={modes}).");
        }
        else
        {
            Assert.Equal("run", firstArg);
            Assert.DoesNotContain("Release", psi.ArgumentList, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static DebuggableAttribute.DebuggingModes ReadAssemblyDebuggingModes(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader reader = peReader.GetMetadataReader();
        foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attr = reader.GetCustomAttribute(handle);
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                MemberReference ctor = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (ctor.Parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference typeRef = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
                    if (reader.StringComparer.Equals(typeRef.Name, nameof(DebuggableAttribute)))
                    {
                        BlobReader blob = reader.GetBlobReader(attr.Value);
                        _ = blob.ReadUInt16(); // Prolog 0x0001
                        return (DebuggableAttribute.DebuggingModes)blob.ReadInt32();
                    }
                }
            }
        }

        return DebuggableAttribute.DebuggingModes.None;
    }
}
