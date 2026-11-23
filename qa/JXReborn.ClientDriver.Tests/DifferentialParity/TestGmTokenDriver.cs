using JXReborn.Adapter.Legacy.Packets;
using Xunit;

namespace JXReborn.ClientDriver.Tests.DifferentialParity;

/// <summary>
/// Drives the MECE + Atomic in-game GM Token (<c>(6, 1, 4258)</c> / <c>Cẩm nang Tân Thủ</c>) identically through
/// <c>game.exe</c> (<c>0x59</c> <c>use_item</c> + <c>0x5F</c> <c>select_dialog</c>) on both
/// the legacy oracle (Lua 4.0) and <c>JXReborn</c> (<c>TestGmTokenItemScript</c>).
/// </summary>
public static class TestGmTokenDriver
{
    public const int PlaceEquipRoom = 3;
    public const int GmTokenGenre = 6;
    public const int GmTokenDetailType = 1;
    public const int GmTokenParticularType = 4258;

    // Root MECE Domain indices (0..8)
    public const int MenuCharacter = 0;
    public const int MenuFactionAndSkill = 1;
    public const int MenuEconomy = 2;
    public const int MenuItems = 3;
    public const int MenuCombat = 4;
    public const int MenuTeleport = 5;
    public const int MenuWorldEntities = 6;
    public const int MenuTasksAndDiagnostics = 7;
    public const int MenuClose = 8;

    // [0] Character -> [0] Level
    public static readonly int[] PathLevelPlus1 = [MenuCharacter, 0, 0];
    public static readonly int[] PathLevel10 = [MenuCharacter, 0, 1];
    public static readonly int[] PathLevel20 = [MenuCharacter, 0, 2];
    public static readonly int[] PathLevel30 = [MenuCharacter, 0, 3];
    public static readonly int[] PathLevel50 = [MenuCharacter, 0, 4];
    public static readonly int[] PathLevel80 = [MenuCharacter, 0, 5];
    public static readonly int[] PathLevel120 = [MenuCharacter, 0, 6];

    // [0] Character -> [1] Attributes / [2] Vitals
    public static readonly int[] PathAddAttr30 = [MenuCharacter, 1, 0];
    public static readonly int[] PathAddAttr50 = [MenuCharacter, 1, 1];
    public static readonly int[] PathRestoreVitals = [MenuCharacter, 2, 0];

    // [1] Faction, Camp & Skills
    public static readonly int[] PathCampNovice = [MenuFactionAndSkill, 0, 0];
    public static readonly int[] PathCampJustice = [MenuFactionAndSkill, 0, 1];
    public static readonly int[] PathCampEvil = [MenuFactionAndSkill, 0, 2];
    public static readonly int[] PathCampBalance = [MenuFactionAndSkill, 0, 3];
    public static readonly int[] PathCampFree = [MenuFactionAndSkill, 0, 4];
    public static readonly int[] PathAddSkillPoints10 = [MenuFactionAndSkill, 1, 0];
    public static readonly int[] PathAddSkillPoints20 = [MenuFactionAndSkill, 1, 1];
    public static readonly int[] PathLearnHangLongBatVu5 = [MenuFactionAndSkill, 2, 0];
    public static readonly int[] PathAddRepute500 = [MenuFactionAndSkill, 3, 0];
    public static readonly int[] PathAddLeadExp3M = [MenuFactionAndSkill, 3, 1];
    public static readonly int[] PathSetTransmigration1 = [MenuFactionAndSkill, 4, 0];

    // [2] Economy & Storage
    public static readonly int[] PathMoney50k = [MenuEconomy, 0];
    public static readonly int[] PathMoney100k = [MenuEconomy, 1];
    public static readonly int[] PathMoney200k = [MenuEconomy, 2];
    public static readonly int[] PathMoney1M = [MenuEconomy, 3];
    public static readonly int[] PathMoneyZero = [MenuEconomy, 5];
    public static readonly int[] PathOpenStoreBox = [MenuEconomy, 6];

    // [3] Items & Inventory
    public static readonly int[] PathMedHpSmall = [MenuItems, 0, 0];
    public static readonly int[] PathMedMpSmall = [MenuItems, 0, 1];
    public static readonly int[] PathMedMpMid = [MenuItems, 0, 2];
    public static readonly int[] PathMedPack5x5 = [MenuItems, 0, 3];
    public static readonly int[] PathEqSword1 = [MenuItems, 1, 0];
    public static readonly int[] PathEqSword2 = [MenuItems, 1, 1];
    public static readonly int[] PathEqArmor1 = [MenuItems, 1, 2];
    public static readonly int[] PathEqBlueRing5 = [MenuItems, 1, 3];
    public static readonly int[] PathEqPurpleSword5 = [MenuItems, 1, 4];
    public static readonly int[] PathMatHuyenTinh3x = [MenuItems, 2, 0];
    public static readonly int[] PathMatRawOre = [MenuItems, 2, 1];
    public static readonly int[] PathMatAttrOre = [MenuItems, 2, 2];
    public static readonly int[] PathMatNhacVuongKiem = [MenuItems, 2, 3];
    public static readonly int[] PathMatSatThuGian = [MenuItems, 2, 4];
    public static readonly int[] PathDurabilityMinus5 = [MenuItems, 3, 0];
    public static readonly int[] PathDurabilitySet1 = [MenuItems, 3, 1];
    public static readonly int[] PathBagClear = [MenuItems, 4, 0];
    public static readonly int[] PathBagFill = [MenuItems, 4, 1];

    // [4] Combat & PK
    public static readonly int[] PathFightMode1 = [MenuCombat, 0];
    public static readonly int[] PathFightMode0 = [MenuCombat, 1];
    public static readonly int[] PathPk0 = [MenuCombat, 2];
    public static readonly int[] PathPk10 = [MenuCombat, 3];

    // [5] Map & Teleport
    public const int TeleportBaLang53 = 0;
    public const int TeleportTuongDuong78 = 1;
    public const int TeleportDuongChau80 = 2;
    public const int TeleportDaoHuongThon101 = 3;
    public const int TeleportVoDang81 = 4;
    public const int TeleportVuLangDong199 = 5;

    // [6] World & Entities
    public static readonly int[] PathSpawnDummy413 = [MenuWorldEntities, 0];
    public static readonly int[] PathSpawnMob1 = [MenuWorldEntities, 1];
    public static readonly int[] PathOpenShop39 = [MenuWorldEntities, 2];

    public static InventoryItemSnapshot? FindGmTokenInBag(WorldDomSnapshot snap) =>
        snap.Items?.FirstOrDefault(static i =>
            i.Place == PlaceEquipRoom &&
            i.Genre == GmTokenGenre &&
            i.DetailType == GmTokenDetailType &&
            i.ParticularType == GmTokenParticularType);

    public static Task<WorldDomSnapshot> InvokeSubmenuActionAsync(
        JxHeadlessClient client,
        int rootOptionIndex,
        int subOptionIndex) =>
        InvokePathAsync(client, rootOptionIndex, subOptionIndex);

    public static async Task<WorldDomSnapshot> InvokePathAsync(
        JxHeadlessClient client,
        params int[] optionPath)
    {
        ArgumentNullException.ThrowIfNull(optionPath);
        if (optionPath.Length == 0)
        {
            throw new ArgumentException("Option path must contain at least one index.", nameof(optionPath));
        }

        WorldDomSnapshot snap = await client.GetStateAsync();
        if (snap.Dialog.IsOpen)
        {
            snap = await client.DismissSystemLoginHintIfOpenAsync();
        }

        InventoryItemSnapshot? token = FindGmTokenInBag(snap);
        if (token is null)
        {
            try
            {
                snap = await client.WaitUntilAsync(
                    static s => FindGmTokenInBag(s) is not null,
                    TimeSpan.FromSeconds(5));
                token = FindGmTokenInBag(snap);
            }
            catch (TimeoutException ex)
            {
                WorldDomSnapshot latest = await client.GetStateAsync();
                string dump = string.Join(", ", latest.Items.Select(static i =>
                    $"(idx={i.ArrayIndex},id={i.ServerItemId},place={i.Place},g={i.Genre},d={i.DetailType},p={i.ParticularType},pos=({i.GridX},{i.GridY}),name='{i.Name}')"));
                throw new InvalidOperationException($"GM Token (6,1,4258) not found in bag on map {latest.Player.MapId}. Items=[{dump}]", ex);
            }
        }

        Assert.NotNull(token);

        uint seqBeforeUse = GetMaxS2cSeq(snap);
        _ = await client.UseItemAsync(
            arrayIndex: token!.ArrayIndex,
            place: token.Place,
            gridX: token.GridX,
            gridY: token.GridY);

        snap = await client.WaitUntilAsync(
            s => s.Dialog.IsOpen && s.RecentS2c.Any(p => p.Seq > seqBeforeUse && p.Opcode == ProtocolOpcodes.s2c_scriptaction),
            TimeSpan.FromSeconds(6));

        for (int step = 0; step < optionPath.Length; step++)
        {
            bool isLast = step == optionPath.Length - 1;
            uint seqBeforeSelect = GetMaxS2cSeq(snap);
            _ = await client.SelectDialogAsync(optionPath[step]);

            if (!isLast)
            {
                snap = await client.WaitUntilAsync(
                    s => s.Dialog.IsOpen && s.RecentS2c.Any(p => p.Seq > seqBeforeSelect && p.Opcode == ProtocolOpcodes.s2c_scriptaction),
                    TimeSpan.FromSeconds(6));
            }
        }

        // Wait >= 20 ticks (> 18-tick m_nNextEatTime item cooldown) so consecutive GM Token uses never collide
        snap = await client.GetStateAsync();
        uint targetTick = snap.TickCount + 20;
        return await client.WaitUntilAsync(s => s.TickCount >= targetTick, TimeSpan.FromSeconds(5));
    }

    public static async Task<WorldDomSnapshot> EnsureLevel10Async(JxHeadlessClient client)
    {
        WorldDomSnapshot snap = await client.GetStateAsync();
        if (snap.Player.Level >= 10)
        {
            return snap;
        }

        _ = await InvokePathAsync(client, PathLevel10);
        return await client.WaitUntilAsync(s => s.Player.Level >= 10, TimeSpan.FromSeconds(6));
    }

    public static async Task<WorldDomSnapshot> TeleportToCityAsync(JxHeadlessClient client, int teleportOptionIndex, int expectedMapId)
    {
        _ = await InvokePathAsync(client, MenuTeleport, teleportOptionIndex);
        return await client.WaitUntilAsync(
            s => s.Player.InWorld && s.Player.SyncReady && s.Player.MapId == expectedMapId,
            TimeSpan.FromSeconds(10));
    }

    public static async Task<WorldDomSnapshot> TeleportToCoordsAsync(
        JxHeadlessClient client,
        int mapId,
        int tileX,
        int tileY)
    {
        _ = await InvokeNumberInputSequenceAsync(client, [MenuTeleport, 8], [mapId, tileX, tileY]);
        return await client.WaitUntilAsync(
            s => s.Player.InWorld && s.Player.SyncReady && s.Player.MapId == mapId &&
                 Math.Abs(s.Player.MpsX - (tileX * 32)) <= 64 &&
                 Math.Abs(s.Player.MpsY - (tileY * 32)) <= 64,
            TimeSpan.FromSeconds(10));
    }

    public static Task<WorldDomSnapshot> SetTaskValueAsync(
        JxHeadlessClient client,
        int taskId,
        int value) =>
        InvokeNumberInputSequenceAsync(client, [MenuTasksAndDiagnostics, 4], [taskId, value]);

    public static Task<WorldDomSnapshot> GrantItemByIdsAsync(
        JxHeadlessClient client,
        int genre,
        int detail,
        int particular,
        int level) =>
        InvokeNumberInputSequenceAsync(client, [MenuItems, 5], [genre, detail, particular, level]);

    public static async Task<WorldDomSnapshot> InvokeNumberInputSequenceAsync(
        JxHeadlessClient client,
        IReadOnlyList<int> optionPath,
        IReadOnlyList<int> numberInputs)
    {
        ArgumentNullException.ThrowIfNull(optionPath);
        ArgumentNullException.ThrowIfNull(numberInputs);

        WorldDomSnapshot snap = await client.GetStateAsync();
        if (snap.Dialog.IsOpen)
        {
            snap = await client.DismissSystemLoginHintIfOpenAsync();
        }

        InventoryItemSnapshot? token = FindGmTokenInBag(snap)
            ?? throw new InvalidOperationException("GM Token (6,1,4258) not found in bag.");

        uint seqBeforeUse = GetMaxS2cSeq(snap);
        _ = await client.UseItemAsync(token.ArrayIndex, token.Place, token.GridX, token.GridY);
        snap = await client.WaitUntilAsync(
            s => s.Dialog.IsOpen && s.RecentS2c.Any(p => p.Seq > seqBeforeUse && p.Opcode == ProtocolOpcodes.s2c_scriptaction),
            TimeSpan.FromSeconds(6));

        for (int step = 0; step < optionPath.Count; step++)
        {
            bool isLastOption = step == optionPath.Count - 1;
            uint seqBeforeSelect = GetMaxS2cSeq(snap);
            _ = await client.SelectDialogAsync(optionPath[step]);
            if (!isLastOption)
            {
                snap = await client.WaitUntilAsync(
                    s => s.Dialog.IsOpen && s.RecentS2c.Any(p => p.Seq > seqBeforeSelect && p.Opcode == ProtocolOpcodes.s2c_scriptaction),
                    TimeSpan.FromSeconds(6));
            }
            else if (numberInputs.Count > 0)
            {
                snap = await client.WaitUntilAsync(
                    s => s.RecentS2c.Any(p => p.Seq > seqBeforeSelect && p.Opcode == ProtocolOpcodes.s2c_citywarextend),
                    TimeSpan.FromSeconds(6));
            }
        }

        for (int i = 0; i < numberInputs.Count; i++)
        {
            bool isLastInput = i == numberInputs.Count - 1;
            uint seqBeforeInput = GetMaxS2cSeq(snap);
            _ = await client.SubmitInputNumberAsync(numberInputs[i]);
            if (!isLastInput)
            {
                snap = await client.WaitUntilAsync(
                    s => s.RecentS2c.Any(p => p.Seq > seqBeforeInput && p.Opcode == ProtocolOpcodes.s2c_citywarextend),
                    TimeSpan.FromSeconds(6));
            }
        }

        snap = await client.GetStateAsync();
        uint targetTick = snap.TickCount + 20;
        return await client.WaitUntilAsync(s => s.TickCount >= targetTick, TimeSpan.FromSeconds(5));
    }

    private static uint GetMaxS2cSeq(WorldDomSnapshot snap) =>
        snap.RecentS2c.Count == 0 ? 0u : snap.RecentS2c.Max(static p => p.Seq);
}
