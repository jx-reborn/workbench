using JXReborn.ClientDriver;
using JXReborn.ClientDriver.Encodings;

namespace JXReborn.DualParityServer.Tests.Fixtures;

internal static class TestSnapshotFactory
{
    public static string ToTcvn3Hex(string unicode)
    {
        byte[] bytes = Tcvn3Encoding.EncodeBytes(unicode);
        return Convert.ToHexString(bytes);
    }

    public static WorldDomSnapshot Create(
        uint pid = 1000,
        uint tick = 500,
        uint playerDwId = 10,
        int mapId = 53,
        int mpsX = 51150,
        int mpsY = 102713,
        int level = 10,
        long exp = 1200,
        int money = 5000,
        DialogSnapshot? dialog = null,
        IReadOnlyList<NpcSnapshot>? npcs = null,
        IReadOnlyList<InventoryItemSnapshot>? items = null,
        IReadOnlyList<SystemMessageSnapshot>? systemMessages = null,
        IReadOnlyList<S2cPacketSnapshot>? recentS2c = null)
    {
        var net = new NetworkStateSnapshot(
            GwConnected: true,
            GsConnected: true,
            Disconnecting: false);

        var login = new LoginStateSnapshot(
            Status: 3,
            Result: 0,
            NumRoles: 1,
            SelectedRoleTcvn3Hex: ToTcvn3Hex("ParityHero01"),
            Roles: []);

        var player = new PlayerSnapshot(
            InWorld: true,
            NpcIndex: 1,
            DwId: playerDwId,
            NameTcvn3Hex: ToTcvn3Hex("ParityHero01"),
            Level: level,
            Kind: 0,
            Series: 0,
            Camp: 0,
            CurrentCamp: 0,
            Doing: 1,
            Life: 200,
            LifeMax: 200,
            Mana: 100,
            ManaMax: 100,
            Stamina: 100,
            StaminaMax: 100,
            Money: money,
            RunStatus: 1,
            PkFlag: 0,
            PkValue: 0,
            LeadExp: 0,
            AttrPoints: 5,
            WorldRank: 0,
            FightMode: 0,
            RideHorse: 0,
            MapId: mapId,
            SubWorldIndex: 0,
            RegionIndex: 0,
            MapX: 10,
            MapY: 20,
            MpsX: mpsX,
            MpsY: mpsY,
            Exp: exp);

        DialogSnapshot dlg = dialog ?? new DialogSnapshot(
            IsOpen: false,
            Sequence: 0,
            OpenedTick: 0,
            PromptTcvn3Hex: string.Empty,
            OptionsTcvn3Hex: []);

        return new WorldDomSnapshot(
            Pid: pid,
            TickCount: tick,
            Hwnd: 0x1234,
            WindowVisible: false,
            Net: net,
            Login: login,
            Player: player,
            Npcs: npcs ?? [],
            Dialog: dlg,
            SystemMessages: systemMessages ?? [],
            MessageBoxes: [],
            RecentS2c: recentS2c ?? [],
            Items: items ?? []);
    }

    public static NpcSnapshot CreateDialogNpc(uint dwId, string name, int mpsX, int mpsY) =>
        new(
            ArrayIndex: (int)(dwId % 200) + 2,
            IsSelf: false,
            DwId: dwId,
            NameTcvn3Hex: ToTcvn3Hex(name),
            Kind: 3,
            Level: 1,
            Series: 0,
            Camp: 0,
            CurrentCamp: 0,
            Doing: 1,
            Life: 100,
            LifeMax: 100,
            PkFlag: 0,
            FightMode: 0,
            MpsX: mpsX,
            MpsY: mpsY);

    public static DialogSnapshot CreateDialog(string prompt, params string[] options) =>
        new(
            IsOpen: true,
            Sequence: 1,
            OpenedTick: 100,
            PromptTcvn3Hex: ToTcvn3Hex(prompt),
            OptionsTcvn3Hex: options.Select(ToTcvn3Hex).ToArray());

    public static InventoryItemSnapshot CreateItem(uint serverItemId, int place, int genre, int detail, int particular, int stack = 1) =>
        new(
            ArrayIndex: (int)(serverItemId % 100) + 1,
            ServerItemId: serverItemId,
            Place: place,
            GridX: 0,
            GridY: 0,
            Genre: genre,
            DetailType: detail,
            ParticularType: particular,
            Level: 1,
            Series: 0,
            Width: 1,
            Height: 1,
            CurDurability: 0,
            MaxDurability: 0,
            StackCount: stack,
            NameTcvn3Hex: ToTcvn3Hex(genre == 6 && detail == 1 && particular == 4258 ? "Lenh Bai GM" : "Kim Sang Duoc"));
}
