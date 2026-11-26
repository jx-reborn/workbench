using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Verifiers;

/// <summary>
/// Ground-Truth verification tests for Phase H1 mechanical harness scripts:
/// <c>parity_ledger.py</c>, <c>audit_feature_parity.py</c>, and <c>verify_tdd_gate.py</c>.
/// Anchored strictly against real <c>legacy-server/bin/server1</c> and <c>pack-tieudaotu</c> artifacts.
/// </summary>
public sealed class ParityHarnessVerifiersTests
{
    [Fact]
    public void ParityLedger_SyncAndVerifyLock_ShouldExtractRealWorldsetMapsAndRejectIncompleteEvidence()
    {
        string root = ResolveEcosystemRoot();
        string script = Path.Combine(root, "workbench", "qa", "scripts", "parity_ledger.py");
        Assert.True(File.Exists(script), $"Missing {script}");

        string tempDir = Path.Combine(Path.GetTempPath(), $"jxr-ledger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string ledgerFile = Path.Combine(tempDir, "test_ledger.json");
            (int syncCode, string syncOut) = RunPython(script, "--ledger", ledgerFile, "sync", "--json");
            Assert.True(syncCode == 0, $"parity_ledger.py sync failed:\n{syncOut}");
            Assert.True(File.Exists(ledgerFile));

            using JsonDocument ledgerDoc = JsonDocument.Parse(File.ReadAllText(ledgerFile));
            JsonElement domains = ledgerDoc.RootElement.GetProperty("domains");

            JsonElement maps = domains.GetProperty("maps_world");
            Assert.Equal(136, maps.GetArrayLength());

            JsonElement factions = domains.GetProperty("factions_skills");
            Assert.Equal(10, factions.GetArrayLength());

            JsonElement engine = domains.GetProperty("engine_mechanics");
            Assert.True(engine.GetArrayLength() >= 50);

            // Map 078 has 6,161 CriticalFindings in baseline report -> verify-and-lock MUST reject!
            (int lock78Code, string lock78Out) = RunPython(
                script,
                "--ledger",
                ledgerFile,
                "verify-and-lock",
                "--domain",
                "maps_world",
                "--id",
                "map_078",
                "--test-project",
                "workbench/qa/JXReborn.WireParityProxy.Tests/JXReborn.WireParityProxy.Tests.csproj");
            Assert.NotEqual(0, lock78Code);
            Assert.Contains("CriticalFindings", lock78Out, StringComparison.Ordinal);
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
    public void AuditFeatureParity_OnRealLegacyLuaAndPackScripts_ShouldVerifyRecursiveIncludesAndCatchSabotagedBranches()
    {
        string root = ResolveEcosystemRoot();
        string script = Path.Combine(root, "workbench", "qa", "scripts", "audit_feature_parity.py");
        Assert.True(File.Exists(script), $"Missing {script}");

        // 1. Real Ba Lang A Phuong quest (Task 48, Earring (4,181), Headgear reward) + Lua4Interpreter --exec-vectors!
        (int code1, string out1) = RunPython(
            script,
            "audit",
            "--lua",
            @"\script\两湖区\巴陵县\npc\巴陵县_路人9阿芳对话.lua",
            "--target",
            "pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Quests/BaLangHuyen/BaLangAPhuongDialogScript.cs",
            "--exec-vectors",
            "--strict",
            "--json");
        Assert.True(code1 == 0, $"Expected A Phuong script audit + vector equivalence PASS, got:\n{out1}");
        using (JsonDocument doc1 = JsonDocument.Parse(out1))
        {
            JsonElement fp = doc1.RootElement.GetProperty("legacy_fingerprint");
            int[] tasks = fp.GetProperty("task_ids").EnumerateArray().Select(static e => e.GetInt32()).ToArray();
            Assert.Contains(48, tasks);
            Assert.NotEmpty(doc1.RootElement.GetProperty("target_capabilities").GetProperty("verified_legacy_sources").EnumerateArray());
            Assert.NotEmpty(doc1.RootElement.GetProperty("behavioral_vector_verification").EnumerateArray());
        }

        // 1b. Real Ba Lang Co Lao Thai + Du Khach + Tieu Ngu quests with Lua4Interpreter --exec-vectors!
        foreach ((string luaRel, string csRel) in new[]
        {
            (@"\script\两湖区\巴陵县\npc\巴陵县_路人2古老太对话.lua", "pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Quests/BaLangHuyen/BaLangCoLaoThaiDialogScript.cs"),
            (@"\script\两湖区\巴陵县\npc\巴陵县_路人4小渔对话.lua", "pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Quests/BaLangHuyen/BaLangTieuNguDialogScript.cs"),
            (@"\script\两湖区\巴陵县\npc\巴陵县_路人5游客对话.lua", "pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Quests/BaLangHuyen/BaLangDuKhachDialogScript.cs"),
        })
        {
            (int qCode, string qOut) = RunPython(script, "audit", "--lua", luaRel, "--target", csRel, "--exec-vectors", "--strict");
            Assert.True(qCode == 0, $"Expected vector equivalence PASS for {luaRel}, got:\n{qOut}");
        }

        // 2. Real Wooden Post wrapper -> recursive Include `\script\global\各派接引弟子\轻功_木桩模板.lua` (Task 32 + OnDeath) + --exec-vectors!
        (int code2, string out2) = RunPython(
            script,
            "audit",
            "--lua",
            @"\script\江南区\龙泉村\npc\战斗_木桩.lua",
            "--target",
            "pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Combat/TrainingDummyCombatScript.cs",
            "--exec-vectors",
            "--strict",
            "--json");
        Assert.True(code2 == 0, $"Expected Wooden Post recursive include audit PASS, got:\n{out2}");
        using (JsonDocument doc2 = JsonDocument.Parse(out2))
        {
            JsonElement fp = doc2.RootElement.GetProperty("legacy_fingerprint");
            Assert.NotEmpty(fp.GetProperty("included_impl_files").EnumerateArray());
            int[] tasks = fp.GetProperty("task_ids").EnumerateArray().Select(static e => e.GetInt32()).ToArray();
            Assert.Contains(32, tasks);
        }

        // 3. Real Casino Boss wrapper -> recursive Include `\script\global\dicegame.lua` (CreateNewSayEx + AskClientForNumber + OpenDice)
        (int code3, string out3) = RunPython(
            script,
            "audit",
            "--lua",
            @"\script\江南区\临安\临安\npc\职能_赌场老板.lua",
            "--target",
            "pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Dialogs/CasinoDiceBossDialogScript.cs",
            "--strict",
            "--json");
        Assert.True(code3 == 0, $"Expected Casino Boss recursive include audit PASS, got:\n{out3}");
        using (JsonDocument doc3 = JsonDocument.Parse(out3))
        {
            string[] modals = doc3.RootElement
                .GetProperty("legacy_fingerprint")
                .GetProperty("ui_modals")
                .EnumerateArray()
                .Select(static e => e.GetString()!)
                .ToArray();
            Assert.Contains("AskClientForNumber", modals);
            Assert.Contains("OpenDice", modals);
            Assert.Contains("Describe", modals);
        }

        // 4. Negative Ground-Truth test: sabotage copy of BaLangAPhuongDialogScript (corrupt SHA-256, drop Task 48, add no-op stub)
        string tempDir = Path.Combine(Path.GetTempPath(), $"jxr-feat-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string sabotagedCs = Path.Combine(tempDir, "SabotagedAPhuong.cs");
            File.WriteAllText(
                sabotagedCs,
                """
                using System.Threading;
                using System.Threading.Tasks;
                using JXReborn.ZoneEngine.Scripting;
                [LegacySource(@"\script\两湖区\巴陵县\npc\巴陵县_路人9阿芳对话.lua", "0000000000000000000000000000000000000000000000000000000000000000")]
                public sealed class SabotagedAPhuong : INpcDialogScript
                {
                    public string ScriptCode => "sabotaged";
                    public ValueTask OnDialogAsync(IScriptDialogContext context, CancellationToken ct = default) => ValueTask.CompletedTask;
                }
                """,
                Encoding.UTF8);

            (int badCode, string badOut) = RunPython(
                script,
                "audit",
                "--lua",
                @"\script\两湖区\巴陵县\npc\巴陵县_路人9阿芳对话.lua",
                "--target",
                sabotagedCs,
                "--strict");
            Assert.NotEqual(0, badCode);
            Assert.Contains("[EmptyHandlerStub]", badOut, StringComparison.Ordinal);
            Assert.Contains("[Sha256Mismatch]", badOut, StringComparison.Ordinal);
            Assert.Contains("[MissingTaskIDs]", badOut, StringComparison.Ordinal);
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
    public void VerifyTddGate_OnIsolatedWorkspace_ShouldRejectJudgeTamperingAntiPatternsAndCleanRoomLeaks()
    {
        string root = ResolveEcosystemRoot();
        string script = Path.Combine(root, "workbench", "qa", "scripts", "verify_tdd_gate.py");
        Assert.True(File.Exists(script), $"Missing {script}");

        string sandbox = Path.Combine(Path.GetTempPath(), $"jxr-tdd-gate-{Guid.NewGuid():N}");
        string judgeDir = Path.Combine(sandbox, "workbench", "qa", "JXReborn.WireParityProxy", "Diffing");
        string serverSrcDir = Path.Combine(sandbox, "server", "src", "Zone");
        string serverTestsDir = Path.Combine(sandbox, "server", "tests", "ZoneTests");
        Directory.CreateDirectory(judgeDir);
        Directory.CreateDirectory(serverSrcDir);
        Directory.CreateDirectory(serverTestsDir);

        try
        {
            string judgeFile = Path.Combine(judgeDir, "ActionWindowWireDiffer.cs");
            string prodFile = Path.Combine(serverSrcDir, "CombatCalc.cs");
            string testFile = Path.Combine(serverTestsDir, "CombatCalcTests.cs");
            File.WriteAllText(judgeFile, "public sealed class ActionWindowWireDiffer {}", Encoding.UTF8);
            File.WriteAllText(prodFile, "public sealed class CombatCalc {}", Encoding.UTF8);
            File.WriteAllText(
                testFile,
                "public sealed class CombatCalcTests { [Fact] public void Calc_WhenZero_ShouldReturnZero() { Assert.Equal(0, 0); } }",
                Encoding.UTF8);

            string snapJson = Path.Combine(sandbox, "snap.json");
            (int snapCode, string snapOut) = RunPython(script, "--root", sandbox, "snapshot", "--out", snapJson, "--repos", "server,workbench");
            Assert.True(snapCode == 0, snapOut);

            // Tamper with Judge file + add unsealed test with Task.Delay -> check-red MUST reject before running dotnet!
            File.WriteAllText(judgeFile, "// tampered judge", Encoding.UTF8);
            File.WriteAllText(
                testFile,
                "public class BadTests { [Fact] public async Task BadMethod() { await Task.Delay(10); Assert.True(1 == 1 || 2 == 2); } }",
                Encoding.UTF8);

            string redLock = Path.Combine(sandbox, "red_lock.json");
            (int redCode, string redOut) = RunPython(
                script,
                "--root",
                sandbox,
                "check-red",
                "--snapshot",
                snapJson,
                "--project",
                "dummy.csproj",
                "--filter",
                "BadMethod",
                "--save-red-lock",
                redLock);
            Assert.NotEqual(0, redCode);
            Assert.Contains("[ImmutableJudgeViolation]", redOut, StringComparison.Ordinal);
            Assert.Contains("[TestHygiene]", redOut, StringComparison.Ordinal);
            Assert.Contains("[TestNaming]", redOut, StringComparison.Ordinal);
            Assert.Contains("[UnsealedTestClass]", redOut, StringComparison.Ordinal);

            // Restore Judge, forge a synthetic red_lock, then mutate test + leak ELF 0x080925f0 into server/src -> check-green MUST reject!
            File.WriteAllText(judgeFile, "public sealed class ActionWindowWireDiffer {}", Encoding.UTF8);
            (int snap2Code, _) = RunPython(script, "--root", sandbox, "snapshot", "--out", snapJson, "--repos", "server,workbench");
            Assert.Equal(0, snap2Code);
            using JsonDocument snapDoc = JsonDocument.Parse(File.ReadAllText(snapJson));
            JsonElement h = snapDoc.RootElement.GetProperty("hashes");
            File.WriteAllText(
                redLock,
                JsonSerializer.Serialize(new
                {
                    repos = new[] { "server", "workbench" },
                    hashes = new
                    {
                        judge = h.GetProperty("judge"),
                        tests = h.GetProperty("tests"),
                        prod_at_red = h.GetProperty("prod"),
                    },
                }),
                Encoding.UTF8);

            File.AppendAllText(testFile, "\n// weakened assertion in GREEN", Encoding.UTF8);
            File.WriteAllText(prodFile, "// leaked KNpc.cpp @ 0x080925f0 from jx_linux_y", Encoding.UTF8);

            (int greenCode, string greenOut) = RunPython(
                script,
                "--root",
                sandbox,
                "check-green",
                "--red-lock",
                redLock,
                "--project",
                "dummy.csproj",
                "--filter",
                "Calc");
            Assert.NotEqual(0, greenCode);
            Assert.Contains("[TestTamperingViolation]", greenOut, StringComparison.Ordinal);
            Assert.Contains("[CleanRoomViolation]", greenOut, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    [Fact]
    public void PreToolParityGuard_WhenGivenHookPayloads_ShouldDenyCaptureEditsCleanRoomLeaksAndProtectedContainerStops()
    {
        string root = ResolveEcosystemRoot();
        string guardScript = Path.Combine(root, "workbench", "qa", "scripts", "pre_tool_parity_guard.py");
        Assert.True(File.Exists(guardScript));

        foreach (string repoName in new[] { "workbench", "server", "pack-tieudaotu" })
        {
            string hookPath = Path.Combine(root, repoName, ".agents", "hooks.json");
            Assert.True(File.Exists(hookPath), $"Missing hooks.json in {repoName}");
            using JsonDocument hDoc = JsonDocument.Parse(File.ReadAllText(hookPath));
            Assert.NotEmpty(hDoc.RootElement.EnumerateObject());
        }

        // 1. Deny manual edit on Baselines/*.capture.json
        string denyCapOut = RunPythonStdin(
            guardScript,
            """{"toolCall":{"name":"write_file","args":{"Path":"d:/Projects/jx-reborn/server/tests/JXReborn.Integration.Tests/WireCaptures/Baselines/W01.capture.json","Content":"{}"}}}""");
        using (JsonDocument d1 = JsonDocument.Parse(denyCapOut))
        {
            Assert.Equal("deny", d1.RootElement.GetProperty("decision").GetString());
        }

        // 2. Deny Clean-Room leak into server/src/*.cs
        string denyCleanOut = RunPythonStdin(
            guardScript,
            """{"toolCall":{"name":"edit_file","args":{"Path":"d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/Calc.cs","Replacement":"// KNpc.cpp @ 0x080925f0"}}}""");
        using (JsonDocument d2 = JsonDocument.Parse(denyCleanOut))
        {
            Assert.Equal("deny", d2.RootElement.GetProperty("decision").GetString());
        }

        // 3. Deny stopping protected jx-mariadb-* container, allow legacy-oracle-test
        string denyDockerOut = RunPythonStdin(
            guardScript,
            """{"toolCall":{"name":"run_command","args":{"CommandLine":"wslc rm -f jx-mariadb-97057d87"}}}""");
        using (JsonDocument d3 = JsonDocument.Parse(denyDockerOut))
        {
            Assert.Equal("deny", d3.RootElement.GetProperty("decision").GetString());
        }

        string allowSandboxOut = RunPythonStdin(
            guardScript,
            """{"toolCall":{"name":"run_command","args":{"CommandLine":"wslc remove -f legacy-oracle-test"}}}""");
        using (JsonDocument d4 = JsonDocument.Parse(allowSandboxOut))
        {
            Assert.Equal("allow", d4.RootElement.GetProperty("decision").GetString());
        }
    }

    [Fact]
    public void ParitySkillsAndWorkflowScripts_OnDisk_ShouldParseWithTopLevelAwaitAndValidFrontmatter()
    {
        string root = ResolveEcosystemRoot();
        string workflowsDir = Path.Combine(root, "workbench", ".agents", "workflows");
        string[] expectedWorkflows =
        [
            "wf_lua_compiler_wave.py",
            "wf_map_convergence.py",
            "wf_mechanic_convergence.py",
            "wf_journey_calibration.py",
            "wf_campaign_batch.py",
        ];

        Assert.NotEmpty(expectedWorkflows);
        foreach (string wf in expectedWorkflows)
        {
            string wfPath = Path.Combine(workflowsDir, wf);
            Assert.True(File.Exists(wfPath), $"Missing workflow script: {wfPath}");

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "import ast, pathlib, sys; src = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8-sig'); " +
                "tree = compile(src, sys.argv[1], 'exec', flags=ast.PyCF_ALLOW_TOP_LEVEL_AWAIT); print('OK')");
            psi.ArgumentList.Add(wfPath);
            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, $"Syntax error in {wf}:\n{stderr}\n{stdout}");
        }

        string[] expectedSkills =
        [
            "port-legacy-feature",
            "khao-sat-co-che",
            "port-s3relay-system",
            "khao-sat-chien-truong-pho-ban",
            "dong-bo-hien-thi-client",
        ];
        Assert.NotEmpty(expectedSkills);
        foreach (string skill in expectedSkills)
        {
            string skillMd = Path.Combine(root, "workbench", ".agents", "skills", skill, "SKILL.md");
            Assert.True(File.Exists(skillMd), $"Missing SKILL.md: {skillMd}");
            string content = File.ReadAllText(skillMd, Encoding.UTF8);
            Assert.StartsWith("---", content.TrimStart(), StringComparison.Ordinal);
            Assert.Contains($"name: {skill}", content, StringComparison.Ordinal);
            Assert.Contains("description:", content, StringComparison.Ordinal);
        }

        string agentsDir = Path.Combine(root, "workbench", ".agents", "plugins", "jx-parity-army", "agents");
        string[] expectedAgents =
        [
            "jx-binary-archaeologist",
            "jx-contract-architect",
            "jx-blind-qa",
            "jx-blind-coder",
            "jx-dispute-referee",
            "jx-migrator-engineer",
        ];
        Assert.NotEmpty(expectedAgents);
        foreach (string agentName in expectedAgents)
        {
            string agentPath = Path.Combine(agentsDir, $"{agentName}.md");
            Assert.True(File.Exists(agentPath), $"Missing specialist subagent: {agentPath}");
            string agentText = File.ReadAllText(agentPath, Encoding.UTF8);
            Assert.Contains($"name: {agentName}", agentText, StringComparison.Ordinal);
            Assert.Contains("subagent: true", agentText, StringComparison.Ordinal);
            Assert.Contains("tools:", agentText, StringComparison.Ordinal);
        }

        foreach (string siblingRepo in new[] { "server", "pack-tieudaotu" })
        {
            Assert.True(File.Exists(Path.Combine(root, siblingRepo, ".agents", "skills.json")));
            Assert.True(File.Exists(Path.Combine(root, siblingRepo, ".agents", "plugins.json")));
        }
    }

    [Fact]
    public void DoubleBlindReadBarrier_ForBlindQaAndBlindCoderRoles_ShouldEnforceAsymmetricInformationBarriers()
    {
        string root = ResolveEcosystemRoot();
        string guardScript = Path.Combine(root, "workbench", "qa", "scripts", "pre_tool_parity_guard.py");
        string lockFile = Path.Combine(root, "workbench", "artifacts", "tdd-locks", "active_phase.json");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);

        try
        {
            // 1. Role = blind_qa: DENY reading production src implementation, ALLOW reading public interface & legacy-server
            File.WriteAllText(lockFile, """{"phase":"red","role":"blind_qa"}""", new UTF8Encoding(false));

            using (JsonDocument qaDenyImpl = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"toolCall":{"name":"read_file","args":{"Path":"d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.Movement.cs"}}}""")))
            {
                Assert.Equal("deny", qaDenyImpl.RootElement.GetProperty("decision").GetString());
            }

            using (JsonDocument qaAllowContract = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"toolCall":{"name":"read_file","args":{"Path":"d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Scripting/IScriptDialogContext.cs"}}}""")))
            {
                Assert.Equal("allow", qaAllowContract.RootElement.GetProperty("decision").GetString());
            }

            using (JsonDocument qaAllowLegacy = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"toolCall":{"name":"read_file","args":{"Path":"d:/Projects/jx-reborn/legacy-server/discovery/traps/KNpc_And_KObj_Traps.cpp"}}}""")))
            {
                Assert.Equal("allow", qaAllowLegacy.RootElement.GetProperty("decision").GetString());
            }

            // 2. Role = blind_coder: DENY reading tests/, legacy-server/, holdout vault, and shell commands probing tests/
            File.WriteAllText(lockFile, """{"phase":"green","role":"blind_coder"}""", new UTF8Encoding(false));

            using (JsonDocument coderDenyTest = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"toolCall":{"name":"read_file","args":{"Path":"d:/Projects/jx-reborn/server/tests/JXReborn.ZoneEngine.Tests/WorldZones/NpcCheckTrapLatchingParityTests.cs"}}}""")))
            {
                Assert.Equal("deny", coderDenyTest.RootElement.GetProperty("decision").GetString());
            }

            using (JsonDocument coderDenyLegacy = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"toolCall":{"name":"grep_search","args":{"SearchPath":"d:/Projects/jx-reborn/legacy-server/discovery"}}}""")))
            {
                Assert.Equal("deny", coderDenyLegacy.RootElement.GetProperty("decision").GetString());
            }

            using (JsonDocument coderDenyShellPeek = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"toolCall":{"name":"run_command","args":{"CommandLine":"Get-Content server/tests/FooTests.cs"}}}""")))
            {
                Assert.Equal("deny", coderDenyShellPeek.RootElement.GetProperty("decision").GetString());
            }

            using (JsonDocument coderAllowSrc = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"toolCall":{"name":"read_file","args":{"Path":"d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.Movement.cs"}}}""")))
            {
                Assert.Equal("allow", coderAllowSrc.RootElement.GetProperty("decision").GetString());
            }

            // 3. Per-Conversation Isolation: excluded Coordinator conversationId is NEVER locked out while blind_coder lock is active,
            //    and intrinsic [HARNESS_ROLE:blind_coder] transcript marker isolates parallel workers even with zero active_phase.json!
            File.WriteAllText(
                lockFile,
                """{"phase":"green","role":"blind_coder","excluded_conversation_ids":["coord-conv-001"]}""",
                new UTF8Encoding(false));

            using (JsonDocument coordAllowTests = JsonDocument.Parse(RunPythonStdin(
                guardScript,
                """{"conversationId":"coord-conv-001","toolCall":{"name":"read_file","args":{"Path":"d:/Projects/jx-reborn/server/tests/JXReborn.ZoneEngine.Tests/WorldZones/NpcCheckTrapLatchingParityTests.cs"}}}""")))
            {
                Assert.Equal("allow", coordAllowTests.RootElement.GetProperty("decision").GetString());
            }

            File.Delete(lockFile);
            string fakeTranscript = Path.Combine(Path.GetTempPath(), $"jxr-transcript-{Guid.NewGuid():N}.jsonl");
            try
            {
                File.WriteAllText(
                    fakeTranscript,
                    """{"step_index":0,"type":"USER_INPUT","content":"You are [HARNESS_ROLE:blind_coder] worker."}""" + "\n",
                    new UTF8Encoding(false));

                string escapedTp = fakeTranscript.Replace("\\", "/");
                using JsonDocument intrDeny = JsonDocument.Parse(RunPythonStdin(
                    guardScript,
                    $$$$"""{"conversationId":"worker-conv-99","transcriptPath":"{{{{escapedTp}}}}","toolCall":{"name":"read_file","args":{"Path":"d:/Projects/jx-reborn/legacy-server/discovery/traps/KNpc_And_KObj_Traps.cpp"}}}"""));
                Assert.Equal("deny", intrDeny.RootElement.GetProperty("decision").GetString());
            }
            finally
            {
                if (File.Exists(fakeTranscript))
                {
                    File.Delete(fakeTranscript);
                }
            }
        }
        finally
        {
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
    }

    [Fact]
    public void HoldoutVault_StashAndRestore_ShouldPhysicallyRemoveHoldoutTestsFromWorkspaceDuringCoderPhase()
    {
        string root = ResolveEcosystemRoot();
        string script = Path.Combine(root, "workbench", "qa", "scripts", "verify_tdd_gate.py");

        string sandbox = Path.Combine(Path.GetTempPath(), $"jxr-vault-sandbox-{Guid.NewGuid():N}");
        string externalVault = Path.Combine(Path.GetTempPath(), $"jxr-holdout-vault-{Guid.NewGuid():N}");
        string testDir = Path.Combine(sandbox, "server", "tests", "ZoneTests");
        Directory.CreateDirectory(testDir);

        try
        {
            string holdoutTestFile = Path.Combine(testDir, "SecretHoldoutTests.cs");
            File.WriteAllText(
                holdoutTestFile,
                "public sealed class SecretHoldoutTests { [Fact] public void Secret_WhenCalled_ShouldEqual42() {} }",
                new UTF8Encoding(false));

            string redLock = Path.Combine(sandbox, "red_lock.json");
            File.WriteAllText(
                redLock,
                JsonSerializer.Serialize(new
                {
                    repos = new[] { "server" },
                    red_modified_tests = new[] { holdoutTestFile },
                    new_test_files = new[] { holdoutTestFile },
                    pre_red_test_backups_b64 = new Dictionary<string, string>(),
                    hashes = new
                    {
                        judge = new Dictionary<string, string>(),
                        tests = new Dictionary<string, string>(),
                        prod_at_red = new Dictionary<string, string>(),
                    },
                }),
                new UTF8Encoding(false));

            // Stash holdout test -> file MUST physically vanish from workspace testDir and exist inside externalVault!
            (int stashCode, string stashOut) = RunPython(
                script,
                "--root",
                sandbox,
                "stash-holdout",
                "--red-lock",
                redLock,
                "--vault-dir",
                externalVault);
            Assert.True(stashCode == 0, stashOut);
            Assert.False(File.Exists(holdoutTestFile), "Holdout test file should be physically removed from workspace during GREEN!");
            Assert.True(File.Exists(Path.Combine(externalVault, "vault_manifest.json")));

            // Evaluate against missing csproj -> fails AND re-stashes so holdoutTestFile remains physically absent!
            (int evalFailCode, _) = RunPython(
                script,
                "--root",
                sandbox,
                "evaluate-holdout",
                "--red-lock",
                redLock,
                "--vault-dir",
                externalVault,
                "--project",
                "nonexistent.csproj",
                "--filter",
                "Secret");
            Assert.NotEqual(0, evalFailCode);
            Assert.False(File.Exists(holdoutTestFile), "Holdout test file must be re-quarantined when evaluation fails!");
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }

            if (Directory.Exists(externalVault))
            {
                Directory.Delete(externalVault, recursive: true);
            }
        }
    }

    private static string ResolveEcosystemRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "workbench")) &&
                Directory.Exists(Path.Combine(dir.FullName, "legacy-server")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve jx-reborn ecosystem root.");
    }

    private static (int ExitCode, string Output) RunPython(string scriptPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(scriptPath);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start python.");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        return (proc.ExitCode, $"{stdout}\n{stderr}".Trim());
    }

    private static string RunPythonStdin(string scriptPath, string stdinJson)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(scriptPath);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start python.");
        proc.StandardInput.Write(stdinJson);
        proc.StandardInput.Close();
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return stdout.Trim();
    }
}
