using JXReborn.ClientDriver.Tests.DifferentialParity;
using JXReborn.GameAssets.WorldMaps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JXReborn.ClientDriver.Tests.WorldJourneys;

[Collection(HeadlessGameExeCollectionDefinition.Name)]
[Trait("Category", "Headless")]
public sealed class GateFightModePersistenceHeadlessGameExeTests
{
    private const ushort BaLangHuyenMapId = 53;
    private const uint NorthGateTrapId = 2476324320u;

    [Fact]
    public async Task GameExe_ExitBaLangGateLogoutAndRelogin_ShouldPreserveFightMode1AndAllowMonsterCombat()
    {
        await using var sutHarness = await HeadlessTestServerHarness.StartAsync();
        IMapCatalog mapCatalog = sutHarness.Services.GetRequiredService<IMapCatalog>();
        ClientPakObstacleReader? clientPakObstacles = ClientPakObstacleReader.TryOpenFromTemplate();

        // 1. Login, walk out Ba Lang Huyen North Gate -> FightMode == 1
        await using (JxHeadlessClient client1 = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = sutHarness.BishopPort,
        }))
        {
            WorldDomSnapshot snap1 = await client1.LoginAndEnterWorldAsync(
                "127.0.0.1",
                sutHarness.BishopPort,
                "fightpersist01",
                "123123",
                "ShaolinGate",
                0,
                0,
                BaLangHuyenMapId);

            Assert.Equal(0, snap1.Player.FightMode);

            IReadOnlyList<(int MpsX, int MpsY)> gateWaypoints = MapObstaclePathfinder.FindWaypointsToTrap(
                mapCatalog,
                BaLangHuyenMapId,
                snap1.Player.MpsX,
                snap1.Player.MpsY,
                NorthGateTrapId,
                clientPakObstacles);
            Assert.NotEmpty(gateWaypoints);
            for (int i = 0; i < gateWaypoints.Count; i++)
            {
                (int gx, int gy) = gateWaypoints[i];
                _ = await client1.MoveToAsync(gx, gy, mode: 1);
                if (i < gateWaypoints.Count - 1)
                {
                    _ = await client1.WaitUntilAsync(
                        s => Math.Abs(s.Player.MpsX - gx) <= 96 && Math.Abs(s.Player.MpsY - gy) <= 96,
                        TimeSpan.FromSeconds(15));
                }
            }

            snap1 = await client1.WaitUntilAsync(s => s.Player.FightMode == 1, TimeSpan.FromSeconds(10));
            Assert.Equal(1, snap1.Player.FightMode);
        }

        // 2. Relogin on a fresh game.exe while saved outside town gate -> FightMode MUST remain 1 (jx_linux_y SavePlayerBaseInfo @ 0x080d7b60)!
        await using JxHeadlessClient client2 = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = sutHarness.BishopPort,
        });

        WorldDomSnapshot snap2 = await client2.LoginAndEnterWorldAsync(
            "127.0.0.1",
            sutHarness.BishopPort,
            "fightpersist01",
            "123123",
            "ShaolinGate",
            0,
            0,
            BaLangHuyenMapId);

        Assert.Equal(1, snap2.Player.FightMode);
        NpcSnapshot selfNpc = Assert.Single(snap2.Npcs, n => n.IsSelf);
        Assert.Equal(1, selfNpc.FightMode);

        // 3. Verify client2 can immediately attack and damage a monster outside Ba Lang Huyen without crossing a gate again!
        snap2 = await client2.WaitUntilAsync(
            s => s.Npcs.Any(n => !n.IsSelf && n.Kind == 0 && n.Life > 0),
            TimeSpan.FromSeconds(10));
        NpcSnapshot mob = snap2.Npcs
            .Where(n => !n.IsSelf && n.Kind == 0 && n.Life > 0)
            .OrderBy(n => double.Hypot(n.MpsX - snap2.Player.MpsX, n.MpsY - snap2.Player.MpsY))
            .First();

        IReadOnlyList<(int MpsX, int MpsY)> mobWps = MapObstaclePathfinder.FindWaypointsNearPosition(
            mapCatalog,
            BaLangHuyenMapId,
            snap2.Player.MpsX,
            snap2.Player.MpsY,
            mob.MpsX,
            mob.MpsY,
            stopRadiusCells: 2,
            minDistanceCells: 1,
            clientPakObstacles: clientPakObstacles);
        foreach ((int wx, int wy) in mobWps)
        {
            _ = await client2.MoveToAsync(wx, wy, mode: 2);
            _ = await client2.WaitUntilAsync(
                s => Math.Abs(s.Player.MpsX - wx) <= 96 && Math.Abs(s.Player.MpsY - wy) <= 96,
                TimeSpan.FromSeconds(10));
        }

        int lifeBefore = mob.Life;
        long expBefore = snap2.Player.Exp;
        for (int swing = 0; swing < 6; swing++)
        {
            WorldDomSnapshot cur = await client2.GetStateAsync();
            NpcSnapshot? liveMob = cur.Npcs.FirstOrDefault(n => n.DwId == mob.DwId);
            if (liveMob is null || liveMob.Life < lifeBefore || cur.Player.Exp > expBefore)
            {
                break;
            }

            if (double.Hypot(cur.Player.MpsX - liveMob.MpsX, cur.Player.MpsY - liveMob.MpsY) > 56.0)
            {
                uint moveTick = cur.TickCount;
                _ = await client2.MoveToAsync(liveMob.MpsX, liveMob.MpsY, mode: 2);
                _ = await client2.WaitUntilAsync(s => s.TickCount >= moveTick + 7, TimeSpan.FromSeconds(5));
            }

            uint castTick = (await client2.GetStateAsync()).TickCount;
            _ = await client2.UseSkillOnTargetAsync(53, liveMob.DwId);
            _ = await client2.WaitUntilAsync(s => s.TickCount >= castTick + 10, TimeSpan.FromSeconds(5));
        }

        WorldDomSnapshot afterAtk = await client2.GetStateAsync();
        NpcSnapshot? mobAfter = afterAtk.Npcs.FirstOrDefault(n => n.DwId == mob.DwId);
        bool mobDamaged = (mobAfter is not null && mobAfter.Life < lifeBefore) || afterAtk.Player.Exp > expBefore;
        Assert.True(
            mobDamaged,
            $"Expected monster HP to drop below {lifeBefore} or EXP to increase after relogin combat (LifeAfter={mobAfter?.Life}, Exp={afterAtk.Player.Exp}).");
    }
}
