using JXReborn.ClientDriver;
using JXReborn.Primitives.Encodings;
using JXReborn.TestBase.WireCaptures;

namespace JXReborn.WireParityProxy.ShadowStates;

/// <summary>
/// Converts a wire-reconstructed <see cref="ProxyShadowClientState"/> into a <see cref="WorldDomSnapshot"/>
/// so it can be fed directly into <c>SemanticDiffEngine</c>.
/// </summary>
public static class ShadowStateSnapshotAdapter
{
    public static WorldDomSnapshot Build(
        ProxyShadowClientState state,
        ShadowDialogState dialog,
        IReadOnlyList<VirtualJxNpcEntity> npcs,
        IReadOnlyList<VirtualJxItemSlot> items,
        IReadOnlyList<KeyValuePair<int, ushort>> learnedSkills,
        IReadOnlyList<SystemMessageSnapshot> systemMessages,
        IReadOnlyList<S2cPacketSnapshot> recentS2c,
        IReadOnlyList<C2sPacketSnapshot> recentC2s)
    {
        var net = new NetworkStateSnapshot(
            GwConnected: true,
            GsConnected: true,
            Disconnecting: false);

        var login = new LoginStateSnapshot(
            Status: 0,
            Result: 0,
            NumRoles: 1,
            SelectedRoleTcvn3Hex: string.Empty,
            Roles: []);

        var player = new PlayerSnapshot(
            InWorld: state.SelfNpcId != 0,
            NpcIndex: 1,
            DwId: state.SelfNpcId,
            NameTcvn3Hex: string.Empty,
            Level: state.Level,
            Kind: 0,
            Series: state.Series,
            Camp: state.Camp,
            CurrentCamp: state.CurrentCamp,
            Doing: 0,
            Life: state.Life,
            LifeMax: state.MaxLife,
            Mana: state.Mana,
            ManaMax: state.MaxMana,
            Stamina: state.Stamina,
            StaminaMax: state.MaxStamina,
            Money: state.Money,
            RunStatus: 1,
            PkFlag: state.PkFlag,
            PkValue: state.PkValue,
            LeadExp: 0,
            AttrPoints: state.AttrPoints,
            WorldRank: 0,
            FightMode: state.FightMode,
            RideHorse: 0,
            MapId: state.MapId,
            SubWorldIndex: 0,
            RegionIndex: 0,
            MapX: 0,
            MapY: 0,
            MpsX: state.MpsX,
            MpsY: state.MpsY,
            SyncReady: state.IsSyncCompleted,
            RegionLoaded: state.MapId > 0,
            SaveMoney: state.SaveMoney,
            SkillPoints: state.SkillPoints,
            Exp: state.Exp);

        var npcSnapshots = new List<NpcSnapshot>(npcs.Count);
        Span<byte> nameBuf = stackalloc byte[32];
        for (int i = 0; i < npcs.Count; i++)
        {
            VirtualJxNpcEntity n = npcs[i];
            nameBuf.Clear();
            Tcvn3Encoding.WriteFixedString(n.NameUnicode, nameBuf);
            int nameLen = nameBuf.IndexOf((byte)0);
            if (nameLen < 0)
            {
                nameLen = nameBuf.Length;
            }

            npcSnapshots.Add(new NpcSnapshot(
                ArrayIndex: i + 1,
                IsSelf: n.NpcId == state.SelfNpcId,
                DwId: n.NpcId,
                NameTcvn3Hex: Convert.ToHexString(nameBuf[..nameLen]),
                Kind: (int)n.Kind,
                Level: n.Level,
                Series: (int)n.Series,
                Camp: (int)n.Camp,
                CurrentCamp: (int)n.Camp,
                Doing: (int)n.DoingState,
                Life: n.LifePercent128,
                LifeMax: 128,
                PkFlag: 0,
                FightMode: 0,
                MpsX: n.MpsX,
                MpsY: n.MpsY));
        }

        var itemSnapshots = new List<InventoryItemSnapshot>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            VirtualJxItemSlot it = items[i];
            itemSnapshots.Add(new InventoryItemSnapshot(
                ArrayIndex: i + 1,
                ServerItemId: it.RuntimeItemId,
                Place: (int)it.ContainerPlace,
                GridX: it.GridX,
                GridY: it.GridY,
                Genre: (int)it.Genre,
                DetailType: it.DetailType,
                ParticularType: it.ParticularType,
                Level: it.Level,
                Series: (int)it.Series,
                Width: it.WidthCells,
                Height: it.HeightCells,
                CurDurability: it.CurrentDurability,
                MaxDurability: it.MaxDurability,
                StackCount: it.StackCount,
                NameTcvn3Hex: string.Empty));
        }

        var skillSnapshots = new List<SkillSlotSnapshot>(learnedSkills.Count);
        for (int i = 0; i < learnedSkills.Count; i++)
        {
            KeyValuePair<int, ushort> sk = learnedSkills[i];
            skillSnapshots.Add(new SkillSlotSnapshot(
                SlotIndex: i,
                SkillId: sk.Key,
                BaseLevel: sk.Value,
                CurLevel: sk.Value));
        }

        var dialogSnapshot = new DialogSnapshot(
            IsOpen: dialog.IsOpen,
            Sequence: dialog.Sequence,
            OpenedTick: dialog.Sequence,
            PromptTcvn3Hex: dialog.PromptTcvn3Hex,
            OptionsTcvn3Hex: dialog.OptionsTcvn3Hex,
            IsTalk: dialog.IsTalk);

        return new WorldDomSnapshot(
            Pid: 0,
            TickCount: recentS2c.Count > 0 ? recentS2c[^1].TickCount : 0,
            Hwnd: 0,
            WindowVisible: false,
            Net: net,
            Login: login,
            Player: player,
            Npcs: npcSnapshots,
            Dialog: dialogSnapshot,
            SystemMessages: systemMessages,
            MessageBoxes: [],
            RecentS2c: recentS2c,
            Items: itemSnapshots,
            Skills: skillSnapshots,
            RecentC2s: recentC2s);
    }
}
