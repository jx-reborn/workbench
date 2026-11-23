using System.Diagnostics;
using System.Text;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Packaging;

public sealed class ZeroSimProfilePackagerTests
{
    [Fact]
    public void PackLegacyProfiles_ShouldIncludeSafeLsimAndSevenCityStubsWithoutSimBots()
    {
        string workbenchRoot = FindWorkbenchRoot();
        string packerPath = Path.Combine(workbenchRoot, "qa", "scripts", "pack_legacy_profiles.py");
        Assert.True(File.Exists(packerPath), $"Missing packer script: {packerPath}");

        string pyScript = """
            import importlib.util, io, tarfile, tempfile, pathlib, sys
            spec = importlib.util.spec_from_file_location("packer", sys.argv[1])
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)

            wlls_auto = b'Include("\\\\script\\\\missions\\\\leaguematch\\\\sims\\\\startup.lua")\nInclude("\\\\script\\\\missions\\\\leaguematch\\\\sims\\\\other.lua")\n'
            san_auto = mod.sanitize_lua_bytes("server1/script/missions/leaguematch/wlls_autoexec.lua", wlls_auto)
            assert b'Include("\\\\script\\\\missions\\\\leaguematch\\\\sims\\\\startup.lua")' in san_auto, "startup include should remain active"
            assert san_auto.lstrip().startswith(b'Include('), f"startup include was commented out: {san_auto!r}"
            assert b'-- [ZERO-SIM] Include("\\\\script\\\\missions\\\\leaguematch\\\\sims\\\\other.lua")' in san_auto, "other sims includes must stay disabled"

            wlls_head = b'Include("\\\\script\\\\missions\\\\leaguematch\\\\sims\\\\config.lua")\n'
            san_head = mod.sanitize_lua_bytes("server1/script/missions/leaguematch/head.lua", wlls_head)
            assert san_head.lstrip().startswith(b'Include('), f"config include was commented out: {san_head!r}"

            sim_cities = b'-- header\nInclude("\\\\script\\\\missions\\\\sevencity\\\\sim_guilds.lua")\nfunction SC_Test() return SG_Checksum("abc") end\n'
            san_cities = mod.sanitize_lua_bytes("server1/script/missions/sevencity/sim_cities.lua", sim_cities)
            for sym in (b"SG_Checksum", b"SG_Split", b"SG_OPPONENT_NAMES", b"SG_EnsureOpponentGuilds", b"SG_EnsureVirtualOnline", b"SG_Load"):
                assert sym in san_cities, f"Missing {sym!r} fallback in sanitized sim_cities.lua"

            with tempfile.TemporaryDirectory() as td:
                out_tgz = pathlib.Path(td) / "out.tar.gz"
                mod.build_profile_archive("v1", [], out_tgz)
                with tarfile.open(out_tgz, "r:gz") as tf:
                    names = set(tf.getnames())
                    assert "jxser/server1/script/missions/leaguematch/sims/config.lua" in names, "Missing LSIM config.lua stub"
                    assert "jxser/server1/script/missions/leaguematch/sims/startup.lua" in names, "Missing LSIM startup.lua stub"
                    cfg_txt = tf.extractfile("jxser/server1/script/missions/leaguematch/sims/config.lua").read().decode("utf-8")
                    assert "enabled = 0" in cfg_txt
                    assert "LSIM_IsMap" in cfg_txt
                    st_txt = tf.extractfile("jxser/server1/script/missions/leaguematch/sims/startup.lua").read().decode("utf-8")
                    assert "function LSIM_Startup()" in st_txt
            print("PACKER_STUB_OK")
            """;

        RunInlinePythonAssertion(packerPath, pyScript, "PACKER_STUB_OK");
    }

    [Fact]
    public void PackLegacyProfiles_WorldsetIni_ShouldContainAllBaLangHuyenConnectedMaps()
    {
        // Ground Truth: legacy-server/bin/server1/script/两湖区/巴陵县/trap/*.lua + 洞庭湖码头船夫1对话.lua
        // Map 53 connects directly to Nam Nhac Tran (54), Thien Vuong Bang (59), Vu Lang Son (70), Mieu Linh (74), Vu Lang Dong (199).
        string workbenchRoot = FindWorkbenchRoot();
        string packerPath = Path.Combine(workbenchRoot, "qa", "scripts", "pack_legacy_profiles.py");
        Assert.True(File.Exists(packerPath), $"Missing packer script: {packerPath}");

        string pyScript = """
            import importlib.util, re, sys
            spec = importlib.util.spec_from_file_location("packer", sys.argv[1])
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)

            ws_txt = mod.build_minimal_worldset_ini().decode("ascii")
            loaded_maps = {int(m.group(1)) for m in re.finditer(r"World_?\d+\s*=\s*(\d+)", ws_txt)}
            required_balang_maps = {53, 54, 59, 70, 74, 199}
            missing = sorted(required_balang_maps - loaded_maps)
            assert not missing, f"worldset.ini missing Ba Lang Huyen Ground-Truth connected maps: {missing}"
            print("BALANG_WORLDSET_OK")
            """;

        RunInlinePythonAssertion(packerPath, pyScript, "BALANG_WORLDSET_OK");
    }

    private static void RunInlinePythonAssertion(string packerPath, string pyScript, string expectedToken)
    {
        string tempPy = Path.Combine(Path.GetTempPath(), $"test_packer_{Guid.NewGuid():N}.py");
        File.WriteAllText(tempPy, pyScript, Encoding.UTF8);
        try
        {
            var psi = new ProcessStartInfo("python", $"\"{tempPy}\" \"{packerPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, $"Exit={proc.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains(expectedToken, stdout);
        }
        finally
        {
            if (File.Exists(tempPy))
            {
                File.Delete(tempPy);
            }
        }
    }

    private static string FindWorkbenchRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "qa", "scripts", "pack_legacy_profiles.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate workbench root.");
    }
}
