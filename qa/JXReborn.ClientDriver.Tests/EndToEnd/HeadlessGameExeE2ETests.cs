using JXReborn.Adapter.Legacy.Packets;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.WorldZones;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.SpatialGrid;
using JXReborn.ZoneEngine.WorldZones;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JXReborn.ClientDriver.Tests.EndToEnd;

[Collection(HeadlessGameExeCollectionDefinition.Name)]
[Trait("Category", "Headless")]
public sealed class HeadlessGameExeE2ETests
{
    [Fact]
    public async Task RealGameExe_LoginCreateCharacterAndEnterWorld_ShouldBindPlayerInBaLangHuyen()
    {
        await using var harness = await HeadlessTestServerHarness.StartAsync();
        await using var client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot snapshot = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "e2e_login1",
            password: "pass123",
            roleName: "KiemHiep1",
            gender: 0,
            series: 0,
            nativePlaceId: 53);

        Assert.True(snapshot.Player.InWorld);
        Assert.NotEqual(0u, snapshot.Player.DwId);
        Assert.Equal(53, snapshot.Player.MapId);
        Assert.True(snapshot.Net.GsConnected);
        Assert.False(snapshot.WindowVisible);
        Assert.True(snapshot.TickCount > 0);
    }

    [Fact]
    public async Task RealGameExe_MoveToAdjacentCoords_ShouldUpdateClientAndServerPosition()
    {
        await using var harness = await HeadlessTestServerHarness.StartAsync();
        await using var client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot initial = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "e2e_move1",
            password: "pass123",
            roleName: "LuuTinh1");

        var router = harness.Services.GetRequiredService<ZoneClusterRouter>();
        Assert.True(router.TryGetWorker(53, out MapZoneWorker? worker));
        Assert.NotNull(worker);

        PlayerZoneState? serverPlayer = worker.PlayersByCharacterId.Values.FirstOrDefault(p => p.RuntimeNpcId == initial.Player.DwId)
            ?? worker.PlayersByCharacterId.Values.FirstOrDefault();
        Assert.NotNull(serverPlayer);

        int startMpsX = initial.Player.MpsX;
        int startMpsY = initial.Player.MpsY;
        int targetMpsX = startMpsX + 88;
        int targetMpsY = startMpsY;

        _ = await client.MoveToAsync(targetMpsX, targetMpsY, mode: 1);

        WorldDomSnapshot moved = await client.WaitUntilAsync(
            s => s.Player.MpsX != startMpsX || s.Player.MpsY != startMpsY,
            TimeSpan.FromSeconds(10));

        Assert.NotEqual((startMpsX, startMpsY), (moved.Player.MpsX, moved.Player.MpsY));
        bool serverMoved = SpinWait.SpinUntil(
            () => (serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY) != (startMpsX, startMpsY),
            TimeSpan.FromSeconds(5));
        Assert.True(
            serverMoved,
            $"Server PlayerZoneState coordinates stayed at ({startMpsX}, {startMpsY}).");
    }

    [Fact]
    public async Task RealGameExe_XaPhuDialogAndTeleport_ShouldNavigateMenuAndDeductGold()
    {
        await using var harness = await HeadlessTestServerHarness.StartAsync();
        await using var client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot entered = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "e2e_xaphu1",
            password: "pass123",
            roleName: "XaPhuTester");

        var router = harness.Services.GetRequiredService<ZoneClusterRouter>();
        Assert.True(router.TryGetWorker(53, out MapZoneWorker? worker));
        Assert.NotNull(worker);

        PlayerZoneState? serverPlayer = worker.PlayersByCharacterId.Values.FirstOrDefault(p => p.RuntimeNpcId == entered.Player.DwId)
            ?? worker.PlayersByCharacterId.Values.FirstOrDefault();
        Assert.NotNull(serverPlayer);
        serverPlayer.Level = 10;
        serverPlayer.InventoryMoney = 1000;

        WorldDomSnapshot withAnyNpc = await client.WaitUntilAsync(
            s => s.Npcs.Any(n => !n.IsSelf),
            TimeSpan.FromSeconds(10));

        NpcZoneState? xaphu = withAnyNpc.Npcs
            .Where(n => !n.IsSelf)
            .Select(n => worker.StaticNpcs.GetValueOrDefault(n.DwId))
            .FirstOrDefault(n => n is not null)
            ?? worker.StaticNpcs.Values.FirstOrDefault();
        Assert.NotNull(xaphu);
        xaphu.ScriptCode = "npc-town-teleporter";

        // Place server player next to Xa Phu and ensure the client sees Xa Phu's NPC entry.
        serverPlayer.CurrentMpsX = xaphu.MpsX;
        serverPlayer.CurrentMpsY = xaphu.MpsY;

        WorldDomSnapshot withNpc = await client.WaitUntilAsync(
            s => s.Npcs.Any(n => !n.IsSelf && n.DwId == xaphu.NpcId),
            TimeSpan.FromSeconds(10));

        uint seqBefore = withNpc.Dialog.Sequence;
        _ = await client.InteractNpcByDwIdAsync(xaphu.NpcId);

        WorldDomSnapshot sayDialog = await client.WaitUntilAsync(
            s => s.Dialog.IsOpen && s.Dialog.Sequence > seqBefore,
            TimeSpan.FromSeconds(10));
        Assert.Contains("Xa Phu", sayDialog.Dialog.Prompt, StringComparison.Ordinal);

        // Select Option 1 (Những thành thị đã đi qua) -> receive station list dialog.
        uint seqMenu1 = sayDialog.Dialog.Sequence;
        _ = await client.SelectDialogAsync(1);

        WorldDomSnapshot confirmDialog = await client.WaitUntilAsync(
            s => s.Dialog.IsOpen && s.Dialog.Sequence > seqMenu1,
            TimeSpan.FromSeconds(10));
        Assert.Contains("200", confirmDialog.Dialog.Options[0], StringComparison.Ordinal);

        // Select Option 0 (Tương Dương Phủ[200 lượng]) -> deduct 200 gold and perform teleport.
        _ = await client.SelectDialogAsync(0);

        Assert.True(
            SpinWait.SpinUntil(() => serverPlayer.InventoryMoney == 800, TimeSpan.FromSeconds(5)),
            $"Expected InventoryMoney == 800 after teleport, actual={serverPlayer.InventoryMoney}.");
    }

    [Fact]
    public async Task RealGameExe_CombatOrPkAgainstSecondRealClient_ShouldObserveCombatBroadcast()
    {
        await using var harness = await HeadlessTestServerHarness.StartAsync();

        await using var clientA = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });
        await using var clientB = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot snapA = await clientA.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "e2e_pk_a",
            password: "pass123",
            roleName: "VoLamA");

        WorldDomSnapshot snapB = await clientB.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "e2e_pk_b",
            password: "pass123",
            roleName: "VoLamB");

        var router = harness.Services.GetRequiredService<ZoneClusterRouter>();
        Assert.True(router.TryGetWorker(53, out MapZoneWorker? worker));
        Assert.NotNull(worker);

        Assert.NotEmpty(worker.RegionGrid.ActiveRegions);
        int configuredPlayers = 0;
        foreach (SpatialRegionSlot slot in worker.RegionGrid.ActiveRegions)
        {
            foreach (PlayerZoneState p in slot.Players)
            {
                p.FightMode = 1;
                p.PkState = PlayerPkMode.Fight;
                if (p.RuntimeNpcId == snapA.Player.DwId)
                {
                    p.BaseCamp = EntityCamp.Justice;
                    p.CurrentCamp = EntityCamp.Justice;
                }
                else
                {
                    p.BaseCamp = EntityCamp.Evil;
                    p.CurrentCamp = EntityCamp.Evil;
                }
                configuredPlayers++;
            }
        }
        Assert.Equal(2, configuredPlayers);

        _ = await clientA.WaitUntilAsync(
            s => s.Npcs.Any(n => !n.IsSelf && n.DwId == snapB.Player.DwId),
            TimeSpan.FromSeconds(10));

        _ = await clientA.UseSkillAtPositionAsync(skillId: 53, snapB.Player.MpsX, snapB.Player.MpsY);

        WorldDomSnapshot afterCastB = await clientB.WaitUntilAsync(
            s => s.RecentS2c.Any(p => p.Opcode == ProtocolOpcodes.s2c_skillcast),
            TimeSpan.FromSeconds(10));

        Assert.Contains(afterCastB.RecentS2c, p => p.Opcode == ProtocolOpcodes.s2c_skillcast);
    }
}
