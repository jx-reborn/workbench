using JXReborn.Adapter.Legacy.Packets;
using JXReborn.ClientDriver.Tests.DifferentialParity;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.WorldZones;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JXReborn.ClientDriver.Tests.WorldJourneys;

[Collection(HeadlessGameExeCollectionDefinition.Name)]
[Trait("Category", "Headless")]
public sealed class MonsterAttackAnimationGameExeTests
{
    private readonly ITestOutputHelper _output;

    public MonsterAttackAnimationGameExeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GameExe_KimMieuAttacksPlayer_ShouldShowAttackDoingWhileDealingDamage()
    {
        await using var sutHarness = await HeadlessTestServerHarness.StartAsync();

        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = sutHarness.BishopPort,
        });

        WorldDomSnapshot snap = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            sutHarness.BishopPort,
            account: "cat_anim_qa",
            password: "123123",
            roleName: "CatAnimQA",
            gender: 0,
            series: 0,
            nativePlaceId: 53);

        ZoneClusterRouter router = sutHarness.Services.GetRequiredService<ZoneClusterRouter>();
        Assert.True(router.TryGetWorker(53, out MapZoneWorker? map53Worker));
        Assert.NotNull(map53Worker);
        PlayerZoneState? serverPlayer = map53Worker!.PlayersByCharacterId.Values.FirstOrDefault(
            p => p.RuntimeNpcId == snap.Player.DwId);
        Assert.NotNull(serverPlayer);

        var teleportTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = map53Worker.TryEnqueueCommand(new WorkerSafeTeleportZoneCommand(
            serverPlayer!,
            TargetMapId: 53,
            TargetMpsX: 49614,
            TargetMpsY: 94072,
            TargetFightMode: 1,
            Completion: teleportTcs));
        Assert.True(enqueued);
        Assert.True(await teleportTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        snap = await client.WaitUntilAsync(
            s => s.Player.FightMode == 1 &&
                 s.Npcs.Any(n => !n.IsSelf &&
                                 n.Name.Contains("Kim Miêu", StringComparison.OrdinalIgnoreCase) &&
                                 n.Life > 0 &&
                                 n.MpsX > 0 &&
                                 n.MpsY > 0),
            TimeSpan.FromSeconds(10));

        const double engageDistanceMps = 64.0;
        (snap, NpcSnapshot cat) = await ApproachAndSettleNearKimMieuAsync(client, snap, engageDistanceMps);
        _output.WriteLine($"Found Kim Mieu: DwId={cat.DwId}, Pos=({cat.MpsX},{cat.MpsY}), Doing={cat.Doing}, Dist={double.Hypot(snap.Player.MpsX - cat.MpsX, snap.Player.MpsY - cat.MpsY):F1}");

        uint targetCatDwId = cat.DwId;
        uint lastScannedSeq = snap.RecentS2c.Count > 0 ? snap.RecentS2c.Max(static p => p.Seq) : 0u;
        int startLife = snap.Player.Life;
        int minLife = startLife;

        await client.UseSkillOnTargetAsync(53, targetCatDwId);

        var knownKimMieuIds = new HashSet<uint>();
        foreach (NpcSnapshot npc in snap.Npcs)
        {
            if (!npc.IsSelf && npc.Name.Contains("Kim Miêu", StringComparison.OrdinalIgnoreCase))
            {
                knownKimMieuIds.Add(npc.DwId);
            }
        }

        var observedDoings = new HashSet<int>();
        var observedPlayerDoings = new HashSet<int>();
        bool sawCatCast0x5A = false;
        bool sawCatDirect0x85 = false;
        bool sawPlayerHurt0x56 = false;
        bool sawLifeDrop = false;
        bool sawAttackDoing = false;
        bool sawPlayerHurtDoing = false;
        int catCastCount = 0;
        int lifeDropCount = 0;

        uint lastTick = snap.TickCount;
        int ticksSinceBind = 0;
        int ticksSinceLastCatCast = 0;
        int reengageCount = 0;
        const int maxObservationTicks = 900;
        for (int i = 0; i < maxObservationTicks; i++)
        {
            WorldDomSnapshot cur = await client.WaitUntilAsync(
                s => s.TickCount > lastTick,
                TimeSpan.FromSeconds(2),
                pollInterval: TimeSpan.FromMilliseconds(15));
            lastTick = cur.TickCount;
            ticksSinceBind++;
            ticksSinceLastCatCast++;

            foreach (NpcSnapshot npc in cur.Npcs)
            {
                if (!npc.IsSelf && npc.Name.Contains("Kim Miêu", StringComparison.OrdinalIgnoreCase))
                {
                    knownKimMieuIds.Add(npc.DwId);
                }
            }

            observedPlayerDoings.Add(cur.Player.Doing);
            if (cur.Player.Doing == 9)
            {
                sawPlayerHurtDoing = true;
            }

            if (cur.Player.Life < minLife)
            {
                minLife = cur.Player.Life;
                sawLifeDrop = true;
                lifeDropCount++;
            }

            NpcSnapshot? liveCat = cur.Npcs.FirstOrDefault(n => n.DwId == targetCatDwId);
            if (liveCat is not null)
            {
                observedDoings.Add(liveCat.Doing);
                if (liveCat.Doing is (6 or 7 or 14))
                {
                    sawAttackDoing = true;
                }
            }

            uint? activeOtherCatId = null;
            uint maxSeenSeq = lastScannedSeq;
            foreach (S2cPacketSnapshot pkt in cur.RecentS2c)
            {
                if (pkt.Seq <= lastScannedSeq)
                {
                    continue;
                }

                if (pkt.Seq > maxSeenSeq)
                {
                    maxSeenSeq = pkt.Seq;
                }

                if (pkt.Opcode == ProtocolOpcodes.s2c_skillcast &&
                    TryParseHexBytes(pkt.HexPreview, out byte[] skillCastBytes) &&
                    SkillCastS2CPacket.TryParse(skillCastBytes, out SkillCastS2CPacket castPkt))
                {
                    if (castPkt.CasterNpcId == targetCatDwId)
                    {
                        sawCatCast0x5A = true;
                        catCastCount++;
                        ticksSinceLastCatCast = 0;
                    }
                    else if (!sawCatCast0x5A &&
                             knownKimMieuIds.Contains(castPkt.CasterNpcId) &&
                             cur.Npcs.Any(n => n.DwId == castPkt.CasterNpcId && n.Life > 0))
                    {
                        activeOtherCatId = castPkt.CasterNpcId;
                    }
                }
                else if (pkt.Opcode == ProtocolOpcodes.s2c_castskilldirectly &&
                         TryParseHexBytes(pkt.HexPreview, out byte[] directBytes) &&
                         CastSkillDirectlyS2CPacket.TryParse(directBytes, out CastSkillDirectlyS2CPacket directPkt) &&
                         directPkt.CasterNpcId == targetCatDwId)
                {
                    sawCatDirect0x85 = true;
                }
                else if (pkt.Opcode == ProtocolOpcodes.s2c_broadcastnetcommand &&
                         TryParseHexBytes(pkt.HexPreview, out byte[] netCmdBytes) &&
                         BroadcastNetCommandS2CPacket.TryParse(netCmdBytes, out BroadcastNetCommandS2CPacket netCmdPkt) &&
                         netCmdPkt.NpcId == cur.Player.DwId &&
                         netCmdPkt.CmdKind == 9)
                {
                    sawPlayerHurt0x56 = true;
                }
            }

            lastScannedSeq = maxSeenSeq;

            if (!sawCatCast0x5A && activeOtherCatId is uint switchedCatId)
            {
                _output.WriteLine(
                    $"Switching tracked Kim Mieu from DwId={targetCatDwId} (Doings=[{string.Join(",", observedDoings)}], ticksSinceBind={ticksSinceBind}) to active attacker Kim Mieu DwId={switchedCatId}.");
                targetCatDwId = switchedCatId;
                startLife = cur.Player.Life;
                minLife = startLife;
                observedDoings.Clear();
                observedPlayerDoings.Clear();
                sawCatCast0x5A = false;
                sawCatDirect0x85 = false;
                sawPlayerHurt0x56 = false;
                sawLifeDrop = false;
                sawAttackDoing = false;
                sawPlayerHurtDoing = false;
                catCastCount = 0;
                lifeDropCount = 0;
                ticksSinceBind = 0;
                ticksSinceLastCatCast = 0;
                continue;
            }

            bool targetUnresponsive = (!sawCatCast0x5A && ticksSinceBind >= 36) ||
                                      (!sawPlayerHurt0x56 && ticksSinceLastCatCast >= 54);
            if (targetUnresponsive && reengageCount < 3)
            {
                reengageCount++;
                _output.WriteLine(
                    $"Re-approaching Kim Mieu (DwId={targetCatDwId}, ticksSinceBind={ticksSinceBind}, ticksSinceLastCatCast={ticksSinceLastCatCast}, Doings=[{string.Join(",", observedDoings)}], attempt={reengageCount}).");
                (cur, cat) = await ApproachAndSettleNearKimMieuAsync(client, cur, engageDistanceMps);
                targetCatDwId = cat.DwId;
                lastTick = cur.TickCount;
                lastScannedSeq = cur.RecentS2c.Count > 0 ? cur.RecentS2c.Max(static p => p.Seq) : lastScannedSeq;
                startLife = cur.Player.Life;
                minLife = startLife;
                observedDoings.Clear();
                observedPlayerDoings.Clear();
                sawCatCast0x5A = false;
                sawCatDirect0x85 = false;
                sawPlayerHurt0x56 = false;
                sawLifeDrop = false;
                sawAttackDoing = false;
                sawPlayerHurtDoing = false;
                catCastCount = 0;
                lifeDropCount = 0;
                ticksSinceBind = 0;
                ticksSinceLastCatCast = 0;
                await client.UseSkillOnTargetAsync(53, targetCatDwId);
                continue;
            }

            if (sawLifeDrop && sawAttackDoing && sawCatCast0x5A && sawPlayerHurt0x56 && sawPlayerHurtDoing)
            {
                break;
            }
        }

        _output.WriteLine($"Target Kim Mieu DwId={targetCatDwId}, casts={catCastCount}, lifeDrops={lifeDropCount}, Player Life: {startLife} -> min {minLife}, sawCatCast0x5A={sawCatCast0x5A}, sawCatDirect0x85={sawCatDirect0x85}, sawPlayerHurt0x56={sawPlayerHurt0x56}, CatDoings=[{string.Join(",", observedDoings)}], PlayerDoings=[{string.Join(",", observedPlayerDoings)}]");
        Assert.True(sawLifeDrop, $"Expected Kim Mieu DwId={targetCatDwId} to damage player (Life {startLife} -> {minLife}).");
        Assert.True(sawAttackDoing, $"Expected Kim Mieu DwId={targetCatDwId} to enter an attack/cast Doing state on game.exe, observed Doings=[{string.Join(",", observedDoings)}], 0x5A={sawCatCast0x5A}, 0x85={sawCatDirect0x85}.");
        Assert.True(sawCatCast0x5A, $"Expected 0x5A (s2c_skillcast) with CasterNpcId matching Kim Mieu DwId={targetCatDwId}.");
        Assert.True(sawPlayerHurt0x56, "Expected 0x56 (s2c_broadcastnetcommand do_hurt) targeting Player.DwId when Kim Mieu hits player.");
        Assert.True(sawPlayerHurtDoing, $"Expected Player.Doing to enter do_hurt (9) on game.exe, observed PlayerDoings=[{string.Join(",", observedPlayerDoings)}].");
    }

    private static async Task<(WorldDomSnapshot Snapshot, NpcSnapshot Cat)> ApproachAndSettleNearKimMieuAsync(
        JxHeadlessClient client,
        WorldDomSnapshot snap,
        double maxDistanceMps)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            for (int step = 0; step < 30; step++)
            {
                NpcSnapshot? liveCat = snap.Npcs
                    .Where(n => !n.IsSelf &&
                                n.Name.Contains("Kim Miêu", StringComparison.OrdinalIgnoreCase) &&
                                n.Life > 0 &&
                                n.MpsX > 0 &&
                                n.MpsY > 0)
                    .OrderBy(n => double.Hypot(n.MpsX - snap.Player.MpsX, n.MpsY - snap.Player.MpsY))
                    .FirstOrDefault();

                if (liveCat is not null)
                {
                    double dist = double.Hypot(snap.Player.MpsX - liveCat.MpsX, snap.Player.MpsY - liveCat.MpsY);
                    if (dist <= maxDistanceMps)
                    {
                        break;
                    }

                    if (snap.Player.Doing != 2 && snap.Player.Doing != 3)
                    {
                        await client.MoveToAsync(liveCat.MpsX, liveCat.MpsY, mode: 2);
                    }
                    else if (step % 3 == 0)
                    {
                        await client.MoveToAsync(liveCat.MpsX, liveCat.MpsY, mode: 2);
                    }
                }

                uint nextTick = snap.TickCount + 6u;
                snap = await client.WaitUntilAsync(
                    s =>
                    {
                        if (s.Npcs.Any(n => !n.IsSelf &&
                                            n.Name.Contains("Kim Miêu", StringComparison.OrdinalIgnoreCase) &&
                                            n.Life > 0 &&
                                            n.MpsX > 0 &&
                                            n.MpsY > 0 &&
                                            double.Hypot(s.Player.MpsX - n.MpsX, s.Player.MpsY - n.MpsY) <= maxDistanceMps))
                        {
                            return true;
                        }

                        return s.TickCount >= nextTick;
                    },
                    TimeSpan.FromSeconds(3));
            }

            snap = await client.GetStateAsync();
            if (snap.Player.Doing is 2 or 3)
            {
                await client.MoveToAsync(snap.Player.MpsX, snap.Player.MpsY, mode: 1);
            }

            // Ensure player movement stops (do_stand = 1) before verifying settled distance and capturing S2C watermark.
            snap = await client.WaitUntilAsync(
                static s => s.Player.Doing == 1,
                TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromMilliseconds(15));

            NpcSnapshot? settledCat = snap.Npcs
                .Where(n => !n.IsSelf &&
                            n.Name.Contains("Kim Miêu", StringComparison.OrdinalIgnoreCase) &&
                            n.Life > 0 &&
                            n.MpsX > 0 &&
                            n.MpsY > 0)
                .OrderBy(n => double.Hypot(n.MpsX - snap.Player.MpsX, n.MpsY - snap.Player.MpsY))
                .FirstOrDefault();

            if (settledCat is not null &&
                double.Hypot(snap.Player.MpsX - settledCat.MpsX, snap.Player.MpsY - settledCat.MpsY) <= maxDistanceMps)
            {
                return (snap, settledCat);
            }
        }

        NpcSnapshot? finalCat = snap.Npcs
            .Where(n => !n.IsSelf &&
                        n.Name.Contains("Kim Miêu", StringComparison.OrdinalIgnoreCase) &&
                        n.Life > 0 &&
                        n.MpsX > 0 &&
                        n.MpsY > 0)
            .OrderBy(n => double.Hypot(n.MpsX - snap.Player.MpsX, n.MpsY - snap.Player.MpsY))
            .FirstOrDefault();
        Assert.NotNull(finalCat);
        Assert.True(
            double.Hypot(snap.Player.MpsX - finalCat!.MpsX, snap.Player.MpsY - finalCat.MpsY) <= maxDistanceMps,
            $"Expected player to close within {maxDistanceMps} MPS of Kim Mieu DwId={finalCat.DwId} at ({finalCat.MpsX},{finalCat.MpsY}), current pos=({snap.Player.MpsX},{snap.Player.MpsY}).");
        return (snap, finalCat);
    }

    private static bool TryParseHexBytes(string? hex, out byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(hex) || (hex.Length & 1) != 0)
        {
            bytes = [];
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
