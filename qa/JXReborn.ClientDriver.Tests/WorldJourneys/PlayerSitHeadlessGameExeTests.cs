using JXReborn.ClientDriver.Tests.DifferentialParity;
using JXReborn.GameAssets.WorldMaps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JXReborn.ClientDriver.Tests.WorldJourneys;

[Collection(HeadlessGameExeCollectionDefinition.Name)]
[Trait("Category", "Headless")]
public sealed class PlayerSitHeadlessGameExeTests
{
    private readonly ITestOutputHelper _output;

    public PlayerSitHeadlessGameExeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GameExe_RawOperationSit_ShouldInspectSelfDoingAndPackets()
    {
        await using var sutHarness = await HeadlessTestServerHarness.StartAsync();
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = sutHarness.BishopPort,
        });

        WorldDomSnapshot spawn = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            sutHarness.BishopPort,
            account: "sithead1",
            password: "123456",
            roleName: "SitHead01",
            gender: 0,
            series: 0,
            nativePlaceId: 53);

        NpcSnapshot? beforeSelf = spawn.Npcs.FirstOrDefault(n => n.IsSelf);
        _output.WriteLine($"Before Sit: Doing={beforeSelf?.Doing}, Life={beforeSelf?.Life}/{beforeSelf?.LifeMax}, Mps=({spawn.Player.MpsX},{spawn.Player.MpsY})");

        RawOpResult op = await client.RawOperationAsync(6u, 2u, 0);
        _output.WriteLine($"RawOp(6, 2, 0): ReturnValue={op.ReturnValue}");

        WorldDomSnapshot after = await client.WaitUntilAsync(
            s =>
            {
                NpcSnapshot? selfSnap = s.Npcs.FirstOrDefault(n => n.IsSelf);
                return selfSnap is not null && selfSnap.Doing == 8;
            },
            TimeSpan.FromSeconds(2));

        NpcSnapshot? afterSelf = after.Npcs.FirstOrDefault(n => n.IsSelf);

        _output.WriteLine($"After Sit: Doing={afterSelf?.Doing}, Life={afterSelf?.Life}/{afterSelf?.LifeMax}");
        _output.WriteLine("Recent C2S: " + string.Join(", ", (after?.RecentC2s ?? []).Select(p => $"0x{p.Opcode:X2}")));
        _output.WriteLine("Recent S2C: " + string.Join(", ", (after?.RecentS2c ?? []).Select(p => $"0x{p.Opcode:X2}")));

        Assert.NotNull(afterSelf);
        Assert.Equal(8, afterSelf!.Doing);
        Assert.Contains(after!.RecentS2c, static p => p.Opcode == 0x83);
    }

    [Fact]
    public async Task GameExe_WalkOutNorthGate_ShouldSetFightModeAndAttackMonster()
    {
        await using var sutHarness = await HeadlessTestServerHarness.StartAsync();
        var mapCatalog = sutHarness.Services.GetRequiredService<IMapCatalog>();
        ClientPakObstacleReader? clientPakObstacles = ClientPakObstacleReader.TryOpenFromTemplate();

        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = sutHarness.BishopPort,
        });

        WorldDomSnapshot snap = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            sutHarness.BishopPort,
            account: "shaolin_qa",
            password: "123123",
            roleName: "ShaolinQA",
            gender: 0,
            series: 0,
            nativePlaceId: 53);

        IReadOnlyList<(int MpsX, int MpsY)> gateWaypoints = MapObstaclePathfinder.FindWaypointsToTrap(
            mapCatalog,
            53,
            snap.Player.MpsX,
            snap.Player.MpsY,
            2476324320u,
            clientPakObstacles);
        Assert.NotEmpty(gateWaypoints);
        for (int i = 0; i < gateWaypoints.Count; i++)
        {
            (int gx, int gy) = gateWaypoints[i];
            await client.MoveToAsync(gx, gy, mode: 1);
            if (i < gateWaypoints.Count - 1)
            {
                await client.WaitUntilAsync(
                    s => Math.Abs(s.Player.MpsX - gx) <= 96 && Math.Abs(s.Player.MpsY - gy) <= 96,
                    TimeSpan.FromSeconds(15));
            }
        }

        snap = await client.WaitUntilAsync(s => s.Player.FightMode == 1, TimeSpan.FromSeconds(10));
        NpcSnapshot? self = snap.Npcs.FirstOrDefault(n => n.IsSelf);
        _output.WriteLine($"Outside NorthGate: Player.FightMode={snap.Player.FightMode}, Self.FightMode={self?.FightMode}, Self.PkFlag={self?.PkFlag}, Mps=({snap.Player.MpsX},{snap.Player.MpsY})");

        snap = await client.WaitUntilAsync(
            s => s.Npcs.Any(n => !n.IsSelf && n.Kind == 0 && n.Life > 0 && n.MpsX > 0 && n.MpsY > 0),
            TimeSpan.FromSeconds(10));
        NpcSnapshot? mob = snap.Npcs
            .Where(n => !n.IsSelf && n.Kind == 0 && n.Life > 0 && n.MpsX > 0 && n.MpsY > 0)
            .OrderBy(n => Math.Pow(n.MpsX - snap.Player.MpsX, 2) + Math.Pow(n.MpsY - snap.Player.MpsY, 2))
            .FirstOrDefault();

        _output.WriteLine($"Nearest Mob: Name={mob?.Name}, DwId={mob?.DwId}, Life={mob?.Life}/{mob?.LifeMax}, Mps=({mob?.MpsX},{mob?.MpsY})");
        Assert.NotNull(mob);

        IReadOnlyList<(int MpsX, int MpsY)> mobWps = MapObstaclePathfinder.FindWaypointsNearPosition(
            mapCatalog, 53, snap.Player.MpsX, snap.Player.MpsY, mob!.MpsX, mob.MpsY,
            stopRadiusCells: 2, minDistanceCells: 1, clientPakObstacles: clientPakObstacles);
        foreach ((int wx, int wy) in mobWps)
        {
            await client.MoveToAsync(wx, wy, mode: 1);
            await client.WaitUntilAsync(
                s => Math.Abs(s.Player.MpsX - wx) <= 96 && Math.Abs(s.Player.MpsY - wy) <= 96,
                TimeSpan.FromSeconds(10));
        }

        int lifeBefore = mob.Life;
        for (int swing = 0; swing < 6; swing++)
        {
            WorldDomSnapshot cur = await client.GetStateAsync();
            NpcSnapshot? liveMob = cur.Npcs.FirstOrDefault(n => n.DwId == mob.DwId);
            if (liveMob is null || liveMob.Life < lifeBefore || cur.Player.Exp > 0)
            {
                break;
            }

            if (double.Hypot(cur.Player.MpsX - liveMob.MpsX, cur.Player.MpsY - liveMob.MpsY) > 56.0)
            {
                await client.MoveToAsync(liveMob.MpsX, liveMob.MpsY, mode: 2);
                uint moveStartTick = cur.TickCount;
                await client.WaitUntilAsync(
                    s => s.TickCount >= moveStartTick + 6,
                    TimeSpan.FromSeconds(5));
            }

            await client.UseSkillOnTargetAsync(53, liveMob.DwId);
            uint atkStartTick = (await client.GetStateAsync()).TickCount;
            await client.WaitUntilAsync(
                s => s.TickCount >= atkStartTick + 9,
                TimeSpan.FromSeconds(5));
        }

        WorldDomSnapshot afterAtk = await client.GetStateAsync();
        NpcSnapshot? mobAfter = afterAtk.Npcs.FirstOrDefault(n => n.DwId == mob.DwId);
        _output.WriteLine($"After Attack(53): MobLife={mobAfter?.Life}/{mobAfter?.LifeMax} (was {lifeBefore}), Exp={afterAtk.Player.Exp}");
        _output.WriteLine("Recent C2S: " + string.Join(", ", afterAtk.RecentC2s.TakeLast(10).Select(p => $"0x{p.Opcode:X2}")));
        bool mobDamagedOrDefeated = (mobAfter is not null && mobAfter.Life < lifeBefore) || afterAtk.Player.Exp > 0;
        Assert.True(mobDamagedOrDefeated, $"Expected skill 53 to damage or defeat mob (LifeBefore={lifeBefore}, LifeAfter={mobAfter?.Life}, Exp={afterAtk.Player.Exp}).");
    }
}
