#!/usr/bin/env python3
"""
Zero-Sim Profile Packager for Legacy Reference Oracle (`v1`).

Produces up to three deterministic `.tar.gz` archives under `artifacts/packages/`:
  1. `jxser-vanilla.tar.gz`      : Full 139-map worldset, Lv1 vanilla, Zero-Sim, GM Token disabled.
  2. `jxser-test-minimal.tar.gz` : Compact 26-map worldset, Lv1 vanilla starter bags, Zero-Sim,
                                   GM Token (6,1,4258) granted only to `gm*` and `j05/j06/j08/j09..j16*`.
  3. `jxser-test-kit.tar.gz`     : Compact 26-map worldset, Zero-Sim, GM Token (6,1,4258) auto-granted
                                   to ALL accounts on login with full deterministic Preset Kits menu.

Preserves exact raw Linux ext4 GBK filename bytes (`surrogateescape` + `GNU_FORMAT`) and `0755` modes.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import os
import re
import sys
import tarfile
import time
from pathlib import Path

# Exact 1-to-1 TCVN3 mapping table matching `JXReborn.Primitives.Encodings.Tcvn3Encoding`
TCVN3_MAP: dict[str, int] = {
    "Ă": 0xA1, "Â": 0xA2, "Ê": 0xA3, "Ô": 0xA4, "Ơ": 0xA5, "Ư": 0xA6, "Đ": 0xA7,
    "ă": 0xA8, "â": 0xA9, "ê": 0xAA, "ô": 0xAB, "ơ": 0xAC, "ư": 0xAD, "đ": 0xAE,
    "à": 0xB5, "ả": 0xB6, "ã": 0xB7, "á": 0xB8, "ạ": 0xB9,
    "ằ": 0xBB, "ẳ": 0xBC, "ẵ": 0xBD, "ắ": 0xBE, "ặ": 0xC6,
    "ầ": 0xC7, "ẩ": 0xC8, "ẫ": 0xC9, "ấ": 0xCA, "ậ": 0xCB,
    "è": 0xCC, "ẻ": 0xCE, "ẽ": 0xCF, "é": 0xD0, "ẹ": 0xD1,
    "ề": 0xD2, "ể": 0xD3, "ễ": 0xD4, "ế": 0xD5, "ệ": 0xD6,
    "ì": 0xD7, "ỉ": 0xD8, "ĩ": 0xDC, "í": 0xDD, "ị": 0xDE,
    "ò": 0xDF, "ỏ": 0xE1, "õ": 0xE2, "ó": 0xE3, "ọ": 0xE4,
    "ồ": 0xE5, "ổ": 0xE6, "ỗ": 0xE7, "ố": 0xE8, "ộ": 0xE9,
    "ờ": 0xEA, "ở": 0xEB, "ỡ": 0xEC, "ớ": 0xED, "ợ": 0xEE,
    "ù": 0xEF, "ủ": 0xF1, "ũ": 0xF2, "ú": 0xF3, "ụ": 0xF4,
    "ừ": 0xF5, "ử": 0xF6, "ữ": 0xF7, "ứ": 0xF8, "ự": 0xF9,
    "ỳ": 0xFA, "ỷ": 0xFB, "ỹ": 0xFC, "ý": 0xFD, "ỵ": 0xFE,
}

# Compact 26-map test worldset covering J01-J16 journeys (villages, cities, portals, sects, missions)
MINIMAL_TEST_MAPS: list[int] = [
    53,   # Ba Lang Huyen (Starter village)
    54,   # Nam Nhac Tran (Ba Lang South-East portal / station)
    59,   # Thien Vuong Bang (Dong Dinh wharf boat destination)
    70,   # Vu Lang Son (Ba Lang West-North portal)
    74,   # Mieu Linh / Than Nong Gia (Ba Lang West-South portal)
    101,  # Dao Huong Thon (Starter village)
    78,   # Tuong Duong Phu (Major city)
    80,   # Duong Chau Phu (Major city)
    199,  # Vu Lang Dong (J02B cross-map portal handoff)
    200,  # Co Duong Dong
    57,   # Hoanh Son Dai Dinh
    79,   # Mat Dao Nha Mon Tuong Duong
    90,   # Phuc Nguu Son Dong
    92,   # Thuc Cuong Son
    122,  # Hoang Ha Nguyen Dau
    341,  # Mac Bac Thao Nguyen
    81,   # Vo Dang Phai (J05, J06, J08)
    82,   # Me Cung Day Gieng
    84,   # Tu Tieu Dai Dien
    103,  # Thieu Lam Phai
    106,  # Dai Hung Bao Dien
    111,  # Moc Nhan Hang
    115,  # Cai Bang
    116,  # Tuong Van Dong tang 1
    324,  # Tong Kim Bao Danh
    327,  # Tong Kim Binh Nguyen
    336,  # Phong Lang Do
    345,  # Tong Kim Thanh Tran
    468,  # Vuot Ai So Cap
    512,  # Van Trung Dao
]

DROPPED_EXACT_REL_PATHS: set[str] = {
    "server1/vdk.so",
    "server1/libliendau_sims.so",
    "server1/canxi_dotim_bayban.so",
    "server1/canxi_dotim_bayban.ini",
    "server1/hang_sap_bot.so",
    "server1/hang_sap_bot.ini",
    "server1/aig_nhat.so",
    "server1/aig_nhat.ini",
    "server1/libpreloadclean.so",
    "server1/script/battles/marshal/simtk.lua",
    "server1/script/missions/sevencity/bosswar.lua",
    "server1/script/missions/sevencity/simsevencity.lua",
    "server1/script/missions/sevencity/sim_guilds.lua",
    "server1/script/missions/sevencity/sim_siege_names.lua",
    "server1/script/item/tuicankhon_tudong.lua",
    "gateway/s3relay/script/simcity.lua",
    "gateway/bishop_livetime.dat",
    "gateway/statdata.dat",
    "inject_envoys.py",
    "inspect_sects.py",
    "kiem_bang_sims.sh",
    "seh_stress_probe.obj",
    "summary_flow.py",
    "symlink_sects.py",
}

DROPPED_PREFIXES: tuple[str, ...] = (
    "backup/",
    "gateway/Logs/",
    "gateway/Backup/",
    "gateway/roleback/",
    "server1/Logs/",
    "server1/dulieu/",
    "server1/settings/global/vdk/",
    "server1/script/global/nobitaxd/vdk/simcity/",
    "server1/script/script/global/nobitaxd/vdk/simcity/",
    "server1/script/missions/leaguematch/sims/",
    "server1/script/global/autoingame/",
    "server1/script/item/autoingame/",
    "server1/script/item/vsd/",
    "server1/script/global/vanhung1/vsd/",
)

EXECUTABLE_REL_PATHS: set[str] = {
    "gateway/goddess_y",
    "gateway/bishop_y",
    "gateway/backupdaemon",
    "gateway/s3relay/s3relay_y",
    "gateway/libfiltertext.so",
    "gateway/libgoddesscfg.so",
    "gateway/libheaven.so",
    "gateway/librainbow.so",
    "server1/jx_linux_y",
    "server1/libheaven.so",
    "server1/librainbow.so",
}

SIM_INCLUDE_RE = re.compile(
    rb"^(\s*Include\s*\((?![^)\r\n]*leaguematch[\\/]+sims[\\/]+(?:startup|config)\.lua)[^)\r\n]*(?:simcity|\\sims\\|/sims/|autoingame|\\vsd\\|/vsd/|bosswar\.lua|sim_guilds\.lua|sim_siege_names\.lua|simsevencity\.lua|simtk\.lua|tuicankhon_tudong\.lua).*)$",
    re.IGNORECASE | re.MULTILINE,
)

LSIM_CONFIG_STUB = b"""-- [ZERO-SIM] Safe no-op config for LeagueMatch Sims (no virtual bots spawned)
LSIM_Config = {
    enabled = 0,
    hallMap = -1,
    waitMap = -1,
    fightMap = -1,
    fallbackTown = { 78, 1577, 3238 },
}

function LSIM_IsMap(nMapId)
    return nil
end
"""

LSIM_STARTUP_STUB = b"""Include("\\\\script\\\\missions\\\\leaguematch\\\\sims\\\\config.lua")

function LSIM_Startup()
end
"""

SEVENCITY_SG_FALLBACK_STUB = b"""-- [ZERO-SIM] Safe Lua 4.0 fallbacks when sim_guilds.lua is omitted
SG_OPPONENT_NAMES = SG_OPPONENT_NAMES or { "", "" }
if not SG_Split then
    function SG_Split(s, sep)
        local out = {}
        if not s then return out end
        local first = 1
        while 1 do
            local a, b = strfind(s, sep, first, 1)
            if not a then
                tinsert(out, strsub(s, first))
                return out
            end
            tinsert(out, strsub(s, first, a - 1))
            first = b + 1
        end
    end
end
if not SG_Checksum then
    function SG_Checksum(s)
        if not s then return 0 end
        local n = 0
        for i = 1, strlen(s) do
            n = mod(n * 31 + strbyte(s, i), 2147483647)
        end
        return n
    end
end
if not SG_EnsureOpponentGuilds then function SG_EnsureOpponentGuilds() end end
if not SG_EnsureVirtualOnline then function SG_EnsureVirtualOnline() end end
if not SG_Load then
    function SG_Load()
        return { name = "", master = "", added = 0, ranks = {}, ids = {} }
    end
end
if not SG_FinalizeDissolve then function SG_FinalizeDissolve(x) return 0 end end
"""

CFG_ZERO_FLAGS: dict[bytes, bytes] = {
    b"CFG_HoTroTanThu": b"0",
    b"CFG_SkillNhapMon": b"1",
    b"CFG_ChayThuNghiem": b"0",
    b"CFG_CongThanhGDB": b"0",
    b"CFG_BotTrainTheoCap": b"0",
    b"CFG_BotTongKim3Cap": b"0",
    b"CFG_SimsRoiNhuNguoi": b"0",
    b"CFG_LenhBaiAuto": b"0",
    b"CFG_LenhBaiChiHuy": b"0",
    b"CFG_TamPhapToDoi": b"0",
    b"CFG_SuKienNam": b"0",
    b"CFG_PetTuGoiLai": b"0",
    b"CFG_BNM_RaoPhienBan": b"0",
    b"CFG_TayLuyenDoXanh": b"0",
    b"CFG_NpcBanTrangBiDong": b"0",
}


def lua_tcvn3_literal(text: str) -> str:
    """Encodes Unicode string into a pure 7-bit ASCII Lua 4 string literal using TCVN3 decimal escapes."""
    out: list[str] = ['"']
    for ch in text:
        if ch in TCVN3_MAP:
            out.append(f"\\{TCVN3_MAP[ch]:03d}")
        else:
            code = ord(ch)
            if ch == '"':
                out.append('\\"')
            elif ch == "\\":
                out.append("\\\\")
            elif 32 <= code < 127:
                out.append(ch)
            else:
                raise ValueError(f"Unsupported non-TCVN3 character {ch!r} (U+{code:04X}) in {text!r}")
    out.append('"')
    return "".join(out)


def build_camnang_gm_lua(enabled: bool) -> bytes:
    """Generates `server1/script/global/nobitaxd/item/camnang_gm.lua` (Lua 4.0) — 1-to-1 MECE Atomic parity with TestGmTokenItemScript."""
    if not enabled:
        return (
            b"-- Vanilla profile: GM Token (6, 1, 4258) is disabled.\n"
            b"function main(nItemIdx)\n"
            b"    return 1\n"
            b"end\n"
        )

    L = lua_tcvn3_literal
    script = f"""-- Deterministic MECE + Atomic Test Harness GM Token (6, 1, 4258) for Legacy & JXReborn Parity
IncludeLib("SETTING")
IncludeLib("ITEM")
IncludeLib("NPCINFO")

GMT_TMP = GMT_TMP or {{}}

function GMT_SetTmp(szKey, nVal)
    if not GMT_TMP[PlayerIndex] then
        GMT_TMP[PlayerIndex] = {{}}
    end
    GMT_TMP[PlayerIndex][szKey] = nVal
end

function GMT_GetTmp(szKey)
    if not GMT_TMP[PlayerIndex] then
        return 0
    end
    return GMT_TMP[PlayerIndex][szKey] or 0
end

function main(nItemIdx)
    GMT_OpenRootMenu()
    return 1
end

function GMT_NoOp()
end

function GMT_OpenRootMenu()
    Say({L("Cẩm nang Kiểm Thử (GM Token - MECE Atomic) - Chọn nhóm:")}, 9,
        {L("Nhân vật & Chỉ số")} .. "/GMT_MenuCharacter",
        {L("Môn phái, Phe & Kỹ năng")} .. "/GMT_MenuFactionAndSkill",
        {L("Kinh tế & Rương")} .. "/GMT_MenuEconomy",
        {L("Vật phẩm & Hành trang")} .. "/GMT_MenuItems",
        {L("Chiến đấu & PK")} .. "/GMT_MenuCombat",
        {L("Bản đồ & Di chuyển")} .. "/GMT_MenuTeleport",
        {L("Thế giới & NPC")} .. "/GMT_MenuWorldEntities",
        {L("Biến Hệ thống (Task) & Chẩn đoán")} .. "/GMT_MenuTasksAndDiag",
        {L("Đóng")} .. "/GMT_NoOp")
end

-- ==================== [0] CHARACTER & STATS ====================
function GMT_MenuCharacter()
    Say({L("Nhân vật & Chỉ số (Chọn trục trạng thái):")}, 6,
        {L("Cấp độ")} .. "/GMT_MenuLevel",
        {L("Tiềm năng")} .. "/GMT_MenuAttributes",
        {L("Sinh lực - Nội lực - Thể lực")} .. "/GMT_MenuVitals",
        {L("Điểm hoạt động (Phúc duyên, Tống Kim, Liên đấu)")} .. "/GMT_MenuActivityPoints",
        {L("Danh hiệu - Quân hàm (Rank)")} .. "/GMT_MenuRank",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

function GMT_MenuLevel()
    Say({L("Điều chỉnh Cấp độ (Atomic):")}, 10,
        {L("Tăng +1 cấp")} .. "/GMT_LvPlus1",
        {L("Lên cấp 10")} .. "/GMT_Lv10",
        {L("Lên cấp 20")} .. "/GMT_Lv20",
        {L("Lên cấp 30")} .. "/GMT_Lv30",
        {L("Lên cấp 50")} .. "/GMT_Lv50",
        {L("Lên cấp 80")} .. "/GMT_Lv80",
        {L("Lên cấp 120")} .. "/GMT_Lv120",
        {L("Nhập cấp độ mục tiêu (1..200)")} .. "/GMT_AskLevel",
        {L("Nhận điểm Kinh nghiệm (nhập số)")} .. "/GMT_AskExp",
        {L("Quay lại")} .. "/GMT_MenuCharacter")
end

function GMT_EnsureLevel(nTarget)
    local nCur = GetLevel()
    if nCur < nTarget then
        ST_LevelUp(nTarget - nCur)
    end
    RestoreLife()
    RestoreMana()
    RestoreStamina()
end

function GMT_LvPlus1()
    local nTarget = GetLevel() + 1
    if nTarget > 200 then nTarget = 200 end
    GMT_EnsureLevel(nTarget)
    Msg2Player({L("Đã tăng cấp độ lên ")} .. GetLevel() .. ".")
end

function GMT_Lv10()
    GMT_EnsureLevel(10)
    Msg2Player({L("Đã điều chỉnh cấp độ lên 10.")})
end

function GMT_Lv20()
    GMT_EnsureLevel(20)
    Msg2Player({L("Đã điều chỉnh cấp độ lên 20.")})
end

function GMT_Lv30()
    GMT_EnsureLevel(30)
    Msg2Player({L("Đã điều chỉnh cấp độ lên 30.")})
end

function GMT_Lv50()
    GMT_EnsureLevel(50)
    Msg2Player({L("Đã điều chỉnh cấp độ lên 50.")})
end

function GMT_Lv80()
    GMT_EnsureLevel(80)
    Msg2Player({L("Đã điều chỉnh cấp độ lên 80.")})
end

function GMT_Lv120()
    GMT_EnsureLevel(120)
    Msg2Player({L("Đã điều chỉnh cấp độ lên 120.")})
end

function GMT_AskLevel()
    AskClientForNumber("GMT_OnLevelInput", 1, 200, {L("Nhập cấp độ mục tiêu (1..200):")})
end

function GMT_OnLevelInput(nValue)
    if nValue and nValue >= 1 and nValue <= 200 then
        GMT_EnsureLevel(nValue)
        Msg2Player({L("Đã điều chỉnh cấp độ lên ")} .. GetLevel() .. ".")
    end
end

function GMT_AskExp()
    AskClientForNumber("GMT_OnExpInput", 1, 100000000, {L("Nhập điểm Kinh nghiệm (1..100000000):")})
end

function GMT_OnExpInput(nValue)
    if nValue and nValue >= 1 then
        if nValue > 100000000 then nValue = 100000000 end
        AddOwnExp(nValue)
        Msg2Player({L("Đã nhận điểm Kinh nghiệm.")})
    end
end

function GMT_MenuAttributes()
    Say({L("Điều chỉnh Điểm Tiềm năng (Atomic):")}, 5,
        {L("Cộng 30 điểm Tiềm năng")} .. "/GMT_Prop30",
        {L("Cộng 50 điểm Tiềm năng")} .. "/GMT_Prop50",
        {L("Nhập điểm Tiềm năng (1..500)")} .. "/GMT_AskProp",
        {L("Tẩy tủy Tiềm năng (Reset)")} .. "/GMT_WashAttributes",
        {L("Quay lại")} .. "/GMT_MenuCharacter")
end

function GMT_Prop30()
    AddProp(30)
    Msg2Player({L("Đã cộng 30 điểm Tiềm năng.")})
end

function GMT_Prop50()
    AddProp(50)
    Msg2Player({L("Đã cộng 50 điểm Tiềm năng.")})
end

function GMT_AskProp()
    AskClientForNumber("GMT_OnPropInput", 1, 500, {L("Nhập số điểm Tiềm năng (1..500):")})
end

function GMT_OnPropInput(nValue)
    if nValue and nValue >= 1 and nValue <= 500 then
        AddProp(nValue)
        Msg2Player({L("Đã cộng điểm Tiềm năng.")})
    end
end

GMT_BASE_ATTRS = {{
    {{35, 25, 25, 15}},
    {{20, 35, 20, 25}},
    {{25, 25, 25, 25}},
    {{30, 20, 30, 20}},
    {{20, 15, 25, 40}},
}}

function GMT_WashAttributes()
    if GetLevel() < 50 then
        Msg2Player({L("Cần đạt cấp 50 trước khi tẩy tủy.")})
        return
    end
    local nSeries = GetSeries() + 1
    local tbBase = GMT_BASE_ATTRS[nSeries]
    if not tbBase then return end
    local nT88 = GetTask(88)
    AddStrg(tbBase[1] - GetStrg(1) + GetByte(nT88, 1))
    AddDex(tbBase[2] - GetDex(1) + GetByte(nT88, 2))
    AddVit(tbBase[3] - GetVit(1) + GetByte(nT88, 3))
    AddEng(tbBase[4] - GetEng(1) + GetByte(nT88, 4))
    Msg2Player({L("Đã tẩy tủy Tiềm năng.")})
end

function GMT_MenuVitals()
    Say({L("Điều chỉnh Sinh lực / Nội lực / Thể lực (Atomic):")}, 3,
        {L("Hồi đầy Sinh lực - Nội lực - Thể lực")} .. "/GMT_RestoreVitals",
        {L("Đưa Sinh lực về thấp (10% HP)")} .. "/GMT_LowHp10",
        {L("Quay lại")} .. "/GMT_MenuCharacter")
end

function GMT_RestoreVitals()
    RestoreLife()
    RestoreMana()
    RestoreStamina()
    Msg2Player({L("Đã hồi đầy Sinh lực, Nội lực và Thể lực.")})
end

function GMT_LowHp10()
    local nNpc = PIdx2NpcIdx(PlayerIndex)
    if nNpc and nNpc > 0 then
        local nMax = NPCINFO_GetNpcCurrentMaxLife(nNpc)
        if nMax and nMax > 0 then
            local nLow = floor(nMax / 10)
            if nLow < 1 then nLow = 1 end
            NPCINFO_SetNpcCurrentLife(nNpc, nLow)
            Msg2Player({L("Đã đặt Sinh lực về 10%.")})
        end
    end
end

function GMT_MenuActivityPoints()
    Say({L("Điểm hoạt động (Phúc duyên, Tống Kim, Liên đấu):")}, 4,
        {L("Cộng 1,000 Phúc duyên")} .. "/GMT_AddFuYuan1000",
        {L("Nhận điểm Tống Kim (nhập số)")} .. "/GMT_AskSongJin",
        {L("Nhận điểm Vinh dự Liên đấu (nhập số)")} .. "/GMT_AskHonor",
        {L("Quay lại")} .. "/GMT_MenuCharacter")
end

function GMT_AddFuYuan1000()
    local nNext = GetTask(151) + 1000
    SetTask(151, nNext)
    SyncTaskValue(151)
    Msg2Player({L("Đã cộng 1,000 Phúc duyên.")})
end

function GMT_AskSongJin()
    AskClientForNumber("GMT_OnSongJinInput", 1, 1000000, {L("Nhập điểm Tống Kim (1..1000000):")})
end

function GMT_OnSongJinInput(nValue)
    if nValue and nValue >= 1 then
        if nValue > 1000000 then nValue = 1000000 end
        local nNext = GetTask(747) + nValue
        SetTask(747, nNext)
        SetTask(751, GetTask(751) + nValue)
        SyncTaskValue(747)
        SyncTaskValue(751)
        Msg2Player({L("Đã cộng điểm Tống Kim.")})
    end
end

function GMT_AskHonor()
    AskClientForNumber("GMT_OnHonorInput", 1, 1000000, {L("Nhập điểm Vinh dự (1..1000000):")})
end

function GMT_OnHonorInput(nValue)
    if nValue and nValue >= 1 then
        if nValue > 1000000 then nValue = 1000000 end
        SetTask(2501, GetTask(2501) + nValue)
        SyncTaskValue(2501)
        Msg2Player({L("Đã cộng điểm Vinh dự.")})
    end
end

function GMT_MenuRank()
    Say({L("Danh hiệu / Quân hàm (Rank):")}, 3,
        {L("Nhập Rank ID (0..255)")} .. "/GMT_AskRank",
        {L("Xóa Rank (0)")} .. "/GMT_ClearRank",
        {L("Quay lại")} .. "/GMT_MenuCharacter")
end

function GMT_AskRank()
    AskClientForNumber("GMT_OnRankInput", 0, 255, {L("Nhập Rank ID (0..255):")})
end

function GMT_OnRankInput(nValue)
    if nValue and nValue >= 0 and nValue <= 255 then
        SetRank(nValue)
        Msg2Player({L("Đã đặt Rank ID.")})
    end
end

function GMT_ClearRank()
    SetRank(0)
    Msg2Player({L("Đã xóa Rank về 0.")})
end

-- ==================== [1] FACTION, CAMP & SKILLS ====================
function GMT_MenuFactionAndSkill()
    Say({L("Môn phái, Phe & Kỹ năng (Chọn trục trạng thái):")}, 7,
        {L("Phe (Camp)")} .. "/GMT_MenuCamp",
        {L("Điểm Kỹ năng")} .. "/GMT_MenuSkillPoints",
        {L("Học Kỹ năng")} .. "/GMT_MenuLearnSkill",
        {L("Danh vọng & Tài lãnh đạo")} .. "/GMT_MenuReputeLead",
        {L("Trùng sinh")} .. "/GMT_MenuTransmigration",
        {L("Gia nhập Môn phái & Xuất sư")} .. "/GMT_MenuJoinFaction",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

GMT_FACTION_DEFS = {{
    {{ code = "shaolin",  camp = 1, rank = 1,  series = 0, sex = -1, base = {{10,14,4,6,8,15,16,20,11,19,271,21,273}},                  adv = {{318,319,321,709}} }},
    {{ code = "tianwang", camp = 3, rank = 43, series = 0, sex = -1, base = {{29,30,34,23,24,26,33,31,35,37,40,42,32,36,41,324}},       adv = {{322,323,325,708}} }},
    {{ code = "tangmen",  camp = 3, rank = 25, series = 1, sex = -1, base = {{45,43,347,303,47,50,54,343,345,349,48,58,249,341}},       adv = {{302,339,342,351,710}} }},
    {{ code = "wudu",     camp = 2, rank = 49, series = 1, sex = -1, base = {{63,65,60,62,67,66,70,64,68,69,384,73,356,72,71,74,75}},    adv = {{353,355,390,711}} }},
    {{ code = "emei",     camp = 1, rank = 13, series = 2, sex = 1,  base = {{80,85,77,79,93,82,89,385,86,92,88,91,252,282}},          adv = {{328,380,332,712}} }},
    {{ code = "cuiyan",   camp = 3, rank = 31, series = 2, sex = 1,  base = {{99,102,95,97,269,105,113,100,109,108,111,114}},           adv = {{336,337,713}} }},
    {{ code = "gaibang",  camp = 1, rank = 37, series = 3, sex = -1, base = {{119,122,115,116,129,124,274,277,125,128,130,360}},        adv = {{357,359,714}} }},
    {{ code = "tianren",  camp = 2, rank = 55, series = 3, sex = -1, base = {{135,145,131,132,136,137,138,140,141,364,143,142,148,150}}, adv = {{361,362,391,715}} }},
    {{ code = "wudang",   camp = 1, rank = 7,  series = 4, sex = -1, base = {{153,155,151,152,159,158,164,160,157,165,166,267}},        adv = {{365,368,716}} }},
    {{ code = "kunlun",   camp = 3, rank = 19, series = 4, sex = -1, base = {{169,179,167,168,171,392,174,172,173,178,393,175,181,90,176,182,275,630}}, adv = {{372,375,394,717}} }},
    {{ code = "huashan",  camp = 3, rank = 82, series = 2, sex = -1, base = {{1347,1372,1349,1374,1350,1375,1351,1376,1354,1378,1355,1379,1358,1360,1380}}, adv = {{1364,1382,1365}} }},
}}

function GMT_MenuJoinFaction()
    Say({L("Gia nhập Môn phái & Xuất sư:")}, 14,
        {L("Thiếu Lâm (0)")} .. "/GMT_JoinFac0",
        {L("Thiên Vương Bang (1)")} .. "/GMT_JoinFac1",
        {L("Đường Môn (2)")} .. "/GMT_JoinFac2",
        {L("Ngũ Độc Giáo (3)")} .. "/GMT_JoinFac3",
        {L("Nga My (4)")} .. "/GMT_JoinFac4",
        {L("Thúy Yên (5)")} .. "/GMT_JoinFac5",
        {L("Cái Bang (6)")} .. "/GMT_JoinFac6",
        {L("Thiên Nhẫn (7)")} .. "/GMT_JoinFac7",
        {L("Võ Đang (8)")} .. "/GMT_JoinFac8",
        {L("Côn Lôn (9)")} .. "/GMT_JoinFac9",
        {L("Hoa Sơn (10)")} .. "/GMT_JoinFac10",
        {L("Xuất sư (Graduate Faction)")} .. "/GMT_GraduateFaction",
        {L("Học toàn bộ kỹ năng môn phái hiện tại (Lv1)")} .. "/GMT_LearnAllFactionSkills",
        {L("Quay lại")} .. "/GMT_MenuFactionAndSkill")
end

function GMT_JoinFaction(nFacId)
    local def = GMT_FACTION_DEFS[nFacId + 1]
    if not def then return end
    if GetLevel() < 10 then
        Msg2Player({L("Cần đạt cấp 10 trước khi gia nhập môn phái.")})
        return
    end
    if GetFaction() ~= "" then
        Msg2Player({L("Nhân vật đã gia nhập môn phái.")})
        return
    end
    if GetSeries() ~= def.series then
        Msg2Player({L("Ngũ hành không phù hợp với môn phái này.")})
        return
    end
    if nFacId == 0 and GetSex() ~= 0 then
        Msg2Player({L("Giới tính không phù hợp với môn phái này.")})
        return
    end
    if def.sex >= 0 and GetSex() ~= def.sex then
        Msg2Player({L("Giới tính không phù hợp với môn phái này.")})
        return
    end
    SetFaction(def.code)
    SetCamp(def.camp)
    SetCurCamp(def.camp)
    SetRank(def.rank)
    SetLastFactionNumber(nFacId)
    for i = 1, getn(def.base) do
        local nSkill = def.base[i]
        if HaveMagic(nSkill) < 0 then
            AddMagic(nSkill, 0)
        end
    end
    Msg2Player({L("Đã gia nhập môn phái thành công.")})
end

function GMT_JoinFac0() GMT_JoinFaction(0) end
function GMT_JoinFac1() GMT_JoinFaction(1) end
function GMT_JoinFac2() GMT_JoinFaction(2) end
function GMT_JoinFac3() GMT_JoinFaction(3) end
function GMT_JoinFac4() GMT_JoinFaction(4) end
function GMT_JoinFac5() GMT_JoinFaction(5) end
function GMT_JoinFac6() GMT_JoinFaction(6) end
function GMT_JoinFac7() GMT_JoinFaction(7) end
function GMT_JoinFac8() GMT_JoinFaction(8) end
function GMT_JoinFac9() GMT_JoinFaction(9) end
function GMT_JoinFac10() GMT_JoinFaction(10) end

function GMT_GraduateFaction()
    SetFaction("")
    SetCamp(4)
    SetCurCamp(4)
    Msg2Player({L("Đã xuất sư.")})
end

function GMT_LearnAllFactionSkills()
    local nFac = GetLastFactionNumber()
    if GetFaction() == "" and nFac ~= 10 then
        Msg2Player({L("Nhân vật chưa gia nhập môn phái.")})
        return
    end
    local def = GMT_FACTION_DEFS[nFac + 1]
    if not def then
        Msg2Player({L("Nhân vật chưa gia nhập môn phái.")})
        return
    end
    for i = 1, getn(def.base) do
        local nSkill = def.base[i]
        if HaveMagic(nSkill) < 1 then
            AddMagic(nSkill, 1)
        end
    end
    for i = 1, getn(def.adv) do
        local nSkill = def.adv[i]
        if HaveMagic(nSkill) < 1 then
            AddMagic(nSkill, 1)
        end
    end
    Msg2Player({L("Đã học toàn bộ kỹ năng môn phái ở cấp 1.")})
end

function GMT_MenuCamp()
    Say({L("Đặt Phe (BaseCamp + CurrentCamp):")}, 6,
        {L("Tân thủ (0)")} .. "/GMT_Camp0",
        {L("Chính phái (1)")} .. "/GMT_Camp1",
        {L("Tà phái (2)")} .. "/GMT_Camp2",
        {L("Trung lập (3)")} .. "/GMT_Camp3",
        {L("Tự do (4)")} .. "/GMT_Camp4",
        {L("Quay lại")} .. "/GMT_MenuFactionAndSkill")
end

function GMT_Camp0() SetCamp(0) SetCurCamp(0) end
function GMT_Camp1() SetCamp(1) SetCurCamp(1) end
function GMT_Camp2() SetCamp(2) SetCurCamp(2) end
function GMT_Camp3() SetCamp(3) SetCurCamp(3) end
function GMT_Camp4() SetCamp(4) SetCurCamp(4) end

function GMT_MenuSkillPoints()
    Say({L("Điều chỉnh Điểm Kỹ năng (Atomic):")}, 4,
        {L("Cộng 10 điểm Kỹ năng")} .. "/GMT_SkillPt10",
        {L("Cộng 20 điểm Kỹ năng")} .. "/GMT_SkillPt20",
        {L("Nhập điểm Kỹ năng (1..100)")} .. "/GMT_AskSkillPt",
        {L("Quay lại")} .. "/GMT_MenuFactionAndSkill")
end

function GMT_SkillPt10() AddMagicPoint(10) end
function GMT_SkillPt20() AddMagicPoint(20) end
function GMT_AskSkillPt()
    AskClientForNumber("GMT_OnSkillPtInput", 1, 100, {L("Nhập số điểm Kỹ năng (1..100):")})
end
function GMT_OnSkillPtInput(nValue)
    if nValue and nValue >= 1 and nValue <= 100 then
        AddMagicPoint(nValue)
    end
end

function GMT_MenuLearnSkill()
    Say({L("Học Kỹ năng trực tiếp (Atomic):")}, 6,
        {L("Học Hàng Long Bất Vũ (14) cấp 5")} .. "/GMT_LearnSkill14",
        {L("Nhập ID Kỹ năng (học cấp 1)")} .. "/GMT_AskSkillId",
        {L("Set cấp độ Kỹ năng (Nhập ID & Cấp)")} .. "/GMT_AskSetSkillId",
        {L("Xóa kỹ năng theo ID (Hoàn điểm)")} .. "/GMT_AskDelSkill",
        {L("Tẩy tủy Kỹ năng (Reset)")} .. "/GMT_WashSkills",
        {L("Quay lại")} .. "/GMT_MenuFactionAndSkill")
end

function GMT_LearnSkill14()
    AddMagic(14, 5)
    Msg2Player({L("Đã học Hàng Long Bất Vũ (ID 14) cấp 5.")})
end

function GMT_AskSkillId()
    AskClientForNumber("GMT_OnSkillIdInput", 1, 2000, {L("Nhập ID Kỹ năng cần học (1..2000):")})
end

function GMT_OnSkillIdInput(nSkillId)
    if nSkillId and nSkillId >= 1 and nSkillId <= 2000 then
        AddMagic(nSkillId, 1)
    end
end

function GMT_AskSetSkillId()
    AskClientForNumber("GMT_OnSetSkillId", 1, 2000, {L("Nhập ID Kỹ năng (1..2000):")})
end

function GMT_OnSetSkillId(nSkillId)
    if nSkillId and nSkillId >= 1 and nSkillId <= 2000 then
        GMT_SetTmp("sk_id", nSkillId)
        AskClientForNumber("GMT_OnSetSkillLevel", 1, 63, {L("Nhập cấp độ Kỹ năng (1..63):")})
    end
end

function GMT_OnSetSkillLevel(nLevel)
    local nSkillId = GMT_GetTmp("sk_id")
    if nSkillId >= 1 and nLevel and nLevel >= 1 and nLevel <= 63 then
        AddMagic(nSkillId, nLevel)
        Msg2Player({L("Đã set cấp độ kỹ năng.")})
    end
end

function GMT_AskDelSkill()
    AskClientForNumber("GMT_OnDelSkillInput", 1, 2000, {L("Nhập ID Kỹ năng cần xóa (1..2000):")})
end

function GMT_OnDelSkillInput(nSkillId)
    if nSkillId and nSkillId >= 1 and nSkillId <= 2000 then
        local nLv = HaveMagic(nSkillId)
        if nLv >= 0 then
            DelMagic(nSkillId)
            if nLv > 0 then
                AddMagicPoint(nLv)
            end
            Msg2Player({L("Đã xóa kỹ năng theo ID.")})
        else
            Msg2Player({L("Không tìm thấy kỹ năng để xóa.")})
        end
    end
end

function GMT_WashSkills()
    if GetLevel() < 50 then
        Msg2Player({L("Cần đạt cấp 50 trước khi tẩy tủy.")})
        return
    end
    local nQg = HaveMagic(210)
    local nExp = HaveMagic(400)
    local nPts = RollbackSkill()
    local nDeduct = 0
    if nQg and nQg > 0 then nDeduct = nDeduct + nQg end
    if nExp and nExp > 0 then nDeduct = nDeduct + nExp end
    local nRefund = (nPts or 0) - nDeduct
    if nRefund + GetMagicPoint() < 0 then
        nRefund = -1 * GetMagicPoint()
    end
    if nRefund ~= 0 then
        AddMagicPoint(nRefund)
    end
    if nQg and nQg >= 0 then AddMagic(210, nQg) end
    if nExp and nExp >= 0 then AddMagic(400, nExp) end
    Msg2Player({L("Đã tẩy tủy Kỹ năng.")})
end

function GMT_MenuReputeLead()
    Say({L("Danh vọng & Tài lãnh đạo (Atomic):")}, 4,
        {L("Cộng 500 Danh vọng")} .. "/GMT_Repute500",
        {L("Cộng 3,000,000 Tài lãnh đạo")} .. "/GMT_LeadExp3M",
        {L("Nhập điểm Danh vọng (1..10000)")} .. "/GMT_AskRepute",
        {L("Quay lại")} .. "/GMT_MenuFactionAndSkill")
end

function GMT_Repute500() AddRepute(500) end
function GMT_LeadExp3M() AddLeadExp(3000000) end
function GMT_AskRepute()
    AskClientForNumber("GMT_OnReputeInput", 1, 10000, {L("Nhập điểm Danh vọng (1..10000):")})
end
function GMT_OnReputeInput(nValue)
    if nValue and nValue >= 1 and nValue <= 10000 then
        AddRepute(nValue)
    end
end

function GMT_MenuTransmigration()
    Say({L("Trùng sinh (Task 99 - Atomic):")}, 3,
        {L("Đặt Trùng sinh = 1")} .. "/GMT_Trans1",
        {L("Đặt Trùng sinh = 0")} .. "/GMT_Trans0",
        {L("Quay lại")} .. "/GMT_MenuFactionAndSkill")
end

function GMT_Trans1() SetTask(99, 1) end
function GMT_Trans0() SetTask(99, 0) end

-- ==================== [2] ECONOMY & STORAGE ====================
function GMT_MenuEconomy()
    Say({L("Kinh tế & Rương chứa đồ (Atomic):")}, 9,
        {L("Nhận 50,000 lượng (5 vạn)")} .. "/GMT_Earn50k",
        {L("Nhận 100,000 lượng (10 vạn)")} .. "/GMT_Earn100k",
        {L("Nhận 200,000 lượng (20 vạn)")} .. "/GMT_Earn200k",
        {L("Nhận 1,000,000 lượng (100 vạn)")} .. "/GMT_Earn1m",
        {L("Nhập số lượng ngân lượng")} .. "/GMT_AskMoney",
        {L("Xóa ngân lượng về 0")} .. "/GMT_ZeroMoney",
        {L("Mở Rương chứa đồ")} .. "/GMT_OpenBox",
        {L("Mở rộng Rương (1 - 2 - 3)")} .. "/GMT_MenuExpandableBox",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

function GMT_Earn50k() Earn(50000) end
function GMT_Earn100k() Earn(100000) end
function GMT_Earn200k() Earn(200000) end
function GMT_Earn1m() Earn(1000000) end
function GMT_AskMoney()
    AskClientForNumber("GMT_OnMoneyInput", 1, 100000000, {L("Nhập số lượng ngân lượng (1..100000000):")})
end
function GMT_OnMoneyInput(nAmount)
    if nAmount and nAmount >= 1 then
        Earn(nAmount)
    end
end
function GMT_ZeroMoney()
    local nCash = GetCash()
    if nCash and nCash > 0 then
        Pay(nCash)
    end
end
function GMT_OpenBox()
    OpenBox()
end

function GMT_MenuExpandableBox()
    Say({L("Mở rộng Rương chứa đồ:")}, 4,
        {L("Mở khóa Rương mở rộng 1")} .. "/GMT_UnlockBox1",
        {L("Mở khóa Rương mở rộng 2")} .. "/GMT_UnlockBox2",
        {L("Mở khóa Rương mở rộng 3")} .. "/GMT_UnlockBox3",
        {L("Quay lại")} .. "/GMT_MenuEconomy")
end

function GMT_UnlockBox(nIdx)
    OpenStoreBox(nIdx)
    if GetFightState() == 0 then
        OpenBox()
    end
    Msg2Player({L("Đã mở khóa Rương mở rộng.")})
end

function GMT_UnlockBox1() GMT_UnlockBox(1) end
function GMT_UnlockBox2() GMT_UnlockBox(2) end
function GMT_UnlockBox3() GMT_UnlockBox(3) end

-- ==================== [3] ITEMS & INVENTORY ====================
function GMT_MenuItems()
    Say({L("Vật phẩm & Hành trang (Chọn nhóm):")}, 8,
        {L("Thuốc phục hồi")} .. "/GMT_MenuMedicines",
        {L("Trang bị mẫu")} .. "/GMT_MenuEquipment",
        {L("Nguyên liệu & Tín vật")} .. "/GMT_MenuMaterials",
        {L("Độ bền trang bị")} .. "/GMT_MenuDurability",
        {L("Hành trang (Biên)")} .. "/GMT_MenuBagBoundary",
        {L("Lấy vật phẩm theo ID (G, D, P, L)")} .. "/GMT_AskItemGenre",
        {L("Thú cưỡi (Ngựa)")} .. "/GMT_MenuMounts",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

function GMT_AskItemGenre()
    AskClientForNumber("GMT_OnItemGenre", 0, 10, {L("Nhập Genre (0..10):")})
end

function GMT_OnItemGenre(nGenre)
    if nGenre and nGenre >= 0 and nGenre <= 10 then
        GMT_SetTmp("it_g", nGenre)
        AskClientForNumber("GMT_OnItemDetail", 0, 1000, {L("Nhập Detail (0..1000):")})
    end
end

function GMT_OnItemDetail(nDetail)
    if nDetail and nDetail >= 0 and nDetail <= 1000 then
        GMT_SetTmp("it_d", nDetail)
        AskClientForNumber("GMT_OnItemParticular", 0, 10000, {L("Nhập Particular (0..10000):")})
    end
end

function GMT_OnItemParticular(nParticular)
    if nParticular and nParticular >= 0 and nParticular <= 10000 then
        GMT_SetTmp("it_p", nParticular)
        AskClientForNumber("GMT_OnItemLevel", 1, 100, {L("Nhập Level (1..100):")})
    end
end

function GMT_OnItemLevel(nLevel)
    if nLevel and nLevel >= 1 and nLevel <= 100 then
        local nG = GMT_GetTmp("it_g")
        local nD = GMT_GetTmp("it_d")
        local nP = GMT_GetTmp("it_p")
        if CalcFreeItemCellCount() >= 1 then
            if nG == 4 then
                AddEventItem(nD)
            elseif nG == 0 then
                local nVer = ITEM_GetLatestItemVersion()
                AddItemEx(nVer, 0, 0, 0, nD, nP, nLevel, 0, 0, 0, 0, 0, 0, 0, 0)
            else
                AddItem(nG, nD, nP, nLevel, 0, 0)
            end
            Msg2Player({L("Đã cấp vật phẩm theo ID.")})
        end
    end
end

function GMT_MenuMounts()
    Say({L("Thú cưỡi (Ngựa):")}, 7,
        {L("Ô Vân Đạp Tuyết (0,10,5,6)")} .. "/GMT_Mount1",
        {L("Xích Thố (0,10,5,7)")} .. "/GMT_Mount2",
        {L("Tuyệt ảnh (0,10,5,8)")} .. "/GMT_Mount3",
        {L("Đích Lô (0,10,5,9)")} .. "/GMT_Mount4",
        {L("Chiếu Dạ Ngọc Sư Tử (0,10,5,10)")} .. "/GMT_Mount5",
        {L("Bôn Tiêu (0,10,6,10)")} .. "/GMT_Mount6",
        {L("Quay lại")} .. "/GMT_MenuItems")
end

function GMT_GiveMount(nParticular, nLevel)
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 6 then
        AddItemEx(nVer, 0, 0, 0, 10, nParticular, nLevel, 0, 0, 0, 0, 0, 0, 0, 0)
    end
end

function GMT_Mount1() GMT_GiveMount(5, 6) end
function GMT_Mount2() GMT_GiveMount(5, 7) end
function GMT_Mount3() GMT_GiveMount(5, 8) end
function GMT_Mount4() GMT_GiveMount(5, 9) end
function GMT_Mount5() GMT_GiveMount(5, 10) end
function GMT_Mount6() GMT_GiveMount(6, 10) end

function GMT_MenuMedicines()
    Say({L("Cấp Thuốc phục hồi (Atomic):")}, 5,
        {L("Kim Sáng Dược (tiểu) (1,0,0,1)")} .. "/GMT_MedHpSmall",
        {L("Ngưng Thần Đan (tiểu) (1,1,0,1)")} .. "/GMT_MedMpSmall",
        {L("Ngưng Thần Đan (trung) (1,1,0,2)")} .. "/GMT_MedMpMid",
        {L("5x Máu + 5x Mana (tiểu, không khóa)")} .. "/GMT_MedPack5x5",
        {L("Quay lại")} .. "/GMT_MenuItems")
end

function GMT_MedHpSmall()
    if CalcFreeItemCellCount() >= 1 then AddItem(1, 0, 0, 1, 0, 0) end
end
function GMT_MedMpSmall()
    if CalcFreeItemCellCount() >= 1 then AddItem(1, 1, 0, 1, 0, 0) end
end
function GMT_MedMpMid()
    if CalcFreeItemCellCount() >= 1 then AddItem(1, 1, 0, 2, 0, 0) end
end
function GMT_MedPack5x5()
    for i = 1, 5 do
        if CalcFreeItemCellCount() >= 1 then AddItem(1, 0, 0, 1, 0, 0) end
        if CalcFreeItemCellCount() >= 1 then AddItem(1, 1, 0, 1, 0, 0) end
    end
end

function GMT_MenuEquipment()
    Say({L("Cấp Trang bị mẫu (Atomic):")}, 7,
        {L("Thanh Đồng Kiếm trắng Lv1 (0,0,0,1)")} .. "/GMT_EqSword1",
        {L("Cang Kiếm trắng Lv2 (0,0,0,2)")} .. "/GMT_EqSword2",
        {L("Bố Y trắng Lv1 (0,2,0,1)")} .. "/GMT_EqArmor1",
        {L("Nhẫn Xanh Lv5 seed=12345 (0,3,0,5)")} .. "/GMT_EqBlueRing5",
        {L("Kiếm Tím 6 lỗ rỗng Lv5 (0,0,0,5)")} .. "/GMT_EqPurpleSword5",
        {L("Vũ khí thử nghiệm (9 loại)")} .. "/GMT_MenuTestWeapons",
        {L("Quay lại")} .. "/GMT_MenuItems")
end

function GMT_MenuTestWeapons()
    Say({L("Cấp Vũ khí thử nghiệm (9 loại Lv1):")}, 10,
        {L("Kiếm trắng Lv1 (0,0,0,1)")} .. "/GMT_Wpn0",
        {L("Đao trắng Lv1 (0,0,1,1)")} .. "/GMT_Wpn1",
        {L("Côn trắng Lv1 (0,0,2,1)")} .. "/GMT_Wpn2",
        {L("Thương trắng Lv1 (0,0,3,1)")} .. "/GMT_Wpn3",
        {L("Chùy trắng Lv1 (0,0,4,1)")} .. "/GMT_Wpn4",
        {L("Song Đao trắng Lv1 (0,0,5,1)")} .. "/GMT_Wpn5",
        {L("Phi Tiêu trắng Lv1 (0,1,0,1)")} .. "/GMT_Wpn6",
        {L("Phi Đao trắng Lv1 (0,1,1,1)")} .. "/GMT_Wpn7",
        {L("Tụ Tiễn trắng Lv1 (0,1,2,1)")} .. "/GMT_Wpn8",
        {L("Quay lại")} .. "/GMT_MenuEquipment")
end

function GMT_GiveWeapon(nDetail, nParticular, nCells)
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= nCells then
        AddItemEx(nVer, 0, 0, 0, nDetail, nParticular, 1, 0, 0, 0, 0, 0, 0, 0, 0)
    end
end

function GMT_Wpn0() GMT_GiveWeapon(0, 0, 3) end
function GMT_Wpn1() GMT_GiveWeapon(0, 1, 2) end
function GMT_Wpn2() GMT_GiveWeapon(0, 2, 6) end
function GMT_Wpn3() GMT_GiveWeapon(0, 3, 3) end
function GMT_Wpn4() GMT_GiveWeapon(0, 4, 4) end
function GMT_Wpn5() GMT_GiveWeapon(0, 5, 4) end
function GMT_Wpn6() GMT_GiveWeapon(1, 0, 1) end
function GMT_Wpn7() GMT_GiveWeapon(1, 1, 1) end
function GMT_Wpn8() GMT_GiveWeapon(1, 2, 3) end

function GMT_EqSword1()
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 3 then
        AddItemEx(nVer, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0)
    end
end
function GMT_EqSword2()
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 3 then
        AddItemEx(nVer, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0)
    end
end
function GMT_EqArmor1()
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 6 then
        AddItemEx(nVer, 0, 0, 0, 2, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0)
    end
end
function GMT_EqBlueRing5()
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 1 then
        AddItemEx(nVer, 12345, 0, 0, 3, 0, 5, 0, 10, 5, 5, 5, 5, 5, 5)
    end
end
function GMT_EqPurpleSword5()
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 3 then
        AddItemEx(nVer, 12345, 2, 0, 0, 0, 5, 0, 0, -1, -1, -1, -1, -1, -1)
    end
end

function GMT_MenuMaterials()
    Say({L("Cấp Nguyên liệu & Tín vật (Atomic):")}, 6,
        {L("3x Huyền Tinh Lv3 (6,1,147,3)")} .. "/GMT_MatHuyenTinh3x",
        {L("Huyền Thiết Nguyên Khoáng (6,1,149,1)")} .. "/GMT_MatRawOre",
        {L("Huyền Thiết Khoáng dòng 1 (6,1,200,3)")} .. "/GMT_MatAttrOre",
        {L("Nhạc Vương Kiếm (4,195,1)")} .. "/GMT_MatNhacVuongKiem",
        {L("Sát Thủ Giản Lv90 (6,1,400,90)")} .. "/GMT_MatSatThuGian",
        {L("Quay lại")} .. "/GMT_MenuItems")
end

function GMT_MatHuyenTinh3x()
    if CalcFreeItemCellCount() >= 3 then
        AddItemNoStack(6, 1, 147, 3, 0, 0)
        AddItemNoStack(6, 1, 147, 3, 0, 0)
        AddItemNoStack(6, 1, 147, 3, 0, 0)
    end
end
function GMT_MatRawOre()
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 1 then
        AddItemEx(nVer, 0, 0, 6, 1, 149, 1, 0, 0, 0)
    end
end
function GMT_MatAttrOre()
    local nVer = ITEM_GetLatestItemVersion()
    if CalcFreeItemCellCount() >= 1 then
        AddItemEx(nVer, 0, 0, 6, 1, 200, 3, 0, 0, 126, 0, 0, 0, 0, 0)
    end
end
function GMT_MatNhacVuongKiem()
    if CalcFreeItemCellCount() >= 6 then
        AddEventItem(195)
    end
end
function GMT_MatSatThuGian()
    if CalcFreeItemCellCount() >= 1 then
        AddItem(6, 1, 400, 90, 0, 0)
    end
end

function GMT_MenuDurability()
    Say({L("Chỉnh Độ bền trang bị đầu tiên trong hành trang (Atomic):")}, 3,
        {L("Giảm 5 độ bền trang bị đầu tiên")} .. "/GMT_DurMinus5",
        {L("Đặt độ bền trang bị đầu tiên = 1")} .. "/GMT_DurSet1",
        {L("Quay lại")} .. "/GMT_MenuItems")
end

function GMT_SetFirstEquipDurability(bSetToOne)
    local tbBag = GetRoomItems(0)
    if tbBag and type(tbBag) == "table" then
        for i = 1, getn(tbBag) do
            local idx = tbBag[i]
            if idx and idx > 0 then
                local g, d, p = GetItemProp(idx)
                if g == 0 then
                    local maxDur = GetMaxDurability(idx)
                    if maxDur and maxDur > 1 then
                        local newDur = 1
                        if bSetToOne == 0 then
                            newDur = maxDur - 5
                            if newDur < 1 then newDur = 1 end
                        end
                        SetCurDurability(idx, newDur)
                        SyncItem(idx)
                        return 1
                    end
                end
            end
        end
    end
    return 0
end

function GMT_DurMinus5() GMT_SetFirstEquipDurability(0) end
function GMT_DurSet1() GMT_SetFirstEquipDurability(1) end

function GMT_MenuBagBoundary()
    Say({L("Biên Hành trang (Boundary Testing - Atomic):")}, 3,
        {L("Dọn sạch hành trang (giữ GM Token)")} .. "/GMT_ClearBag",
        {L("Lấp đầy hành trang (0 ô trống)")} .. "/GMT_FillBag",
        {L("Quay lại")} .. "/GMT_MenuItems")
end

function GMT_ClearBag()
    local tbBag = GetRoomItems(0)
    if tbBag and type(tbBag) == "table" then
        for i = 1, getn(tbBag) do
            local idx = tbBag[i]
            if idx and idx > 0 then
                local g, d, p = GetItemProp(idx)
                if not (g == 6 and d == 1 and p == 4258) then
                    RemoveItemByIndex(idx)
                end
            end
        end
    end
end

function GMT_FillBag()
    local nAdded = 0
    while CalcFreeItemCellCount() > 0 and nAdded < 60 do
        AddItem(1, 0, 0, 1, 0, 0)
        nAdded = nAdded + 1
    end
end

-- ==================== [4] COMBAT & PK ====================
function GMT_MenuCombat()
    Say({L("Chiến đấu & PK (Atomic):")}, 6,
        {L("Bật FightMode = 1 (Chiến đấu)")} .. "/GMT_Fight1",
        {L("Tắt FightMode = 0 (Phi chiến đấu)")} .. "/GMT_Fight0",
        {L("Đặt Trị PK = 0 (Xóa sát khí)")} .. "/GMT_Pk0",
        {L("Đặt Trị PK = 10 (ác nhân)")} .. "/GMT_Pk10",
        {L("Nhập Trị PK (0..10)")} .. "/GMT_AskPk",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

function GMT_Fight1() SetFightState(1) end
function GMT_Fight0() SetFightState(0) end
function GMT_Pk0() SetPK(0) end
function GMT_Pk10() SetPK(10) end
function GMT_AskPk()
    AskClientForNumber("GMT_OnPkInput", 0, 10, {L("Nhập Trị PK (0..10):")})
end
function GMT_OnPkInput(nValue)
    if nValue and nValue >= 0 and nValue <= 10 then
        SetPK(nValue)
    end
end

-- ==================== [5] MAP & TELEPORT ====================
function GMT_MenuTeleport()
    Say({L("Bản đồ & Dịch chuyển nhanh (Atomic):")}, 10,
        {L("Ba Lăng Huyện - Trung tâm (53)")} .. "/GMT_Tp53",
        {L("Tương Dương Phủ (78)")} .. "/GMT_Tp78",
        {L("Dương Châu Phủ (80)")} .. "/GMT_Tp80",
        {L("Đạo Hương Thôn (101)")} .. "/GMT_Tp101",
        {L("Võ Đang Phái (81)")} .. "/GMT_Tp81",
        {L("Vũ Lăng Động - Cửa động (199)")} .. "/GMT_Tp199",
        {L("Nhập Map ID để dịch chuyển")} .. "/GMT_AskMapId",
        {L("Đặt điểm hồi sinh tại bản đồ hiện tại")} .. "/GMT_SetRevivalHere",
        {L("Dịch chuyển tọa độ (Map,X,Y)")} .. "/GMT_AskTpCoords",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

function GMT_AskTpCoords()
    AskClientForNumber("GMT_OnTpCoordMap", 1, 1000, {L("Nhập Map ID (1..1000):")})
end

function GMT_OnTpCoordMap(nMap)
    if nMap and nMap >= 1 and nMap <= 1000 then
        GMT_SetTmp("tp_map", nMap)
        AskClientForNumber("GMT_OnTpCoordX", 1, 4096, {L("Nhập Tile X (1..4096):")})
    end
end

function GMT_OnTpCoordX(nX)
    if nX and nX >= 1 and nX <= 4096 then
        GMT_SetTmp("tp_x", nX)
        AskClientForNumber("GMT_OnTpCoordY", 1, 8192, {L("Nhập Tile Y (1..8192):")})
    end
end

function GMT_OnTpCoordY(nY)
    if nY and nY >= 1 and nY <= 8192 then
        local nMap = GMT_GetTmp("tp_map")
        local nX = GMT_GetTmp("tp_x")
        if SubWorldID2Idx(nMap) >= 0 then
            NewWorld(nMap, nX, nY)
            SetFightState(0)
        else
            Msg2Player({L("Bản đồ chưa được nạp trên máy chủ này.")})
        end
    end
end

function GMT_Tp53() NewWorld(53, 1600, 3200) SetFightState(0) end
function GMT_Tp78() NewWorld(78, 1568, 3232) SetFightState(0) end
function GMT_Tp80() NewWorld(80, 1696, 3040) SetFightState(0) end
function GMT_Tp101() NewWorld(101, 1700, 3150) SetFightState(0) end
function GMT_Tp81() NewWorld(81, 1574, 3224) SetFightState(0) end
function GMT_Tp199() NewWorld(199, 1644, 2906) SetFightState(1) end

function GMT_AskMapId()
    AskClientForNumber("GMT_OnMapIdInput", 1, 1000, {L("Nhập Map ID (1..1000):")})
end

function GMT_OnMapIdInput(nMapId)
    if nMapId and nMapId >= 1 and nMapId <= 1000 then
        if SubWorldID2Idx(nMapId) >= 0 then
            NewWorld(nMapId, 1600, 3200)
            SetFightState(0)
        else
            Msg2Player({L("Bản đồ chưa được nạp trên máy chủ này.")})
        end
    end
end

GMT_REV_MAP = {{
    [1] = 1,
    [11] = 5,
    [37] = 23,
    [53] = 19,
    [78] = 29,
    [80] = 34,
    [81] = 39,
    [101] = 47,
    [162] = 63,
    [176] = 67,
}}

function GMT_SetRevivalHere()
    local nW = GetWorldPos()
    local nRev = GMT_REV_MAP[nW] or 1
    SetRevPos(nW, nRev)
    Msg2Player({L("Đã đặt điểm hồi sinh tại bản đồ hiện tại.")})
end

-- ==================== [6] WORLD & ENTITIES ====================
function GMT_MenuWorldEntities()
    Say({L("Thế giới & Thực thể NPC (Atomic):")}, 6,
        {L("Triệu hồi Mộc Nhân 1 tỷ HP (Template 586, X+64)")} .. "/GMT_SpawnDummy",
        {L("Triệu hồi NPC luyện công 100k HP (Template 576, X+96)")} .. "/GMT_SpawnTrainer576",
        {L("Mở Tiệm Dược Điếm (Shop 39)")} .. "/GMT_OpenShop39",
        {L("Triệu hồi NPC - Boss theo Template ID")} .. "/GMT_AskNpcTemplate",
        {L("Triệu hồi Cọc gỗ Lv10 (Template 413, X+64)")} .. "/GMT_SpawnPost413",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

function GMT_SpawnPost413()
    local nW, nCellX, nCellY = GetWorldPos()
    local nMapIdx = SubWorldID2Idx(nW)
    if nMapIdx and nMapIdx >= 0 then
        AddNpc(413, 10, nMapIdx, nCellX * 32 + 64, nCellY * 32, 1)
    end
end

function GMT_SpawnDummy()
    local nW, nCellX, nCellY = GetWorldPos()
    local nMapIdx = SubWorldID2Idx(nW)
    if nMapIdx and nMapIdx >= 0 then
        AddNpc(586, 90, nMapIdx, nCellX * 32 + 64, nCellY * 32, 1)
    end
end

function GMT_SpawnTrainer576()
    local nW, nCellX, nCellY = GetWorldPos()
    local nMapIdx = SubWorldID2Idx(nW)
    if nMapIdx and nMapIdx >= 0 then
        AddNpc(576, 90, nMapIdx, nCellX * 32 + 96, nCellY * 32, 1)
    end
end

function GMT_OpenShop39()
    Sale(39)
end

function GMT_AskNpcTemplate()
    AskClientForNumber("GMT_OnNpcTemplateInput", 1, 5000, {L("Nhập NPC Template ID (1..5000):")})
end

function GMT_OnNpcTemplateInput(nTplId)
    if nTplId and nTplId >= 1 and nTplId <= 5000 then
        local nW, nCellX, nCellY = GetWorldPos()
        local nMapIdx = SubWorldID2Idx(nW)
        if nMapIdx and nMapIdx >= 0 then
            AddNpc(nTplId, 90, nMapIdx, nCellX * 32 + 64, nCellY * 32, 1)
            Msg2Player({L("Đã triệu hồi NPC theo Template ID.")})
        end
    end
end

-- ==================== [7] TASKS & DIAGNOSTICS ====================
function GMT_MenuTasksAndDiag()
    Say({L("Biến Hệ thống (Task) & Chẩn đoán (Atomic):")}, 6,
        {L("In Snapshot trạng thái nhân vật")} .. "/GMT_PrintSnapshot",
        {L("Đọc giá trị Task theo ID")} .. "/GMT_AskGetTask",
        {L("Đặt Task(ID) = 1")} .. "/GMT_AskSetTask1",
        {L("Đặt Task(ID) = 0")} .. "/GMT_AskSetTask0",
        {L("Đặt Task(ID) = Giá trị bất kỳ")} .. "/GMT_AskSetTaskVal",
        {L("Quay lại")} .. "/GMT_OpenRootMenu")
end

function GMT_AskSetTaskVal()
    AskClientForNumber("GMT_OnSetTaskValId", 0, 65535, {L("Nhập Task ID (0..65535):")})
end

function GMT_OnSetTaskValId(nTaskId)
    if nTaskId and nTaskId >= 0 then
        GMT_SetTmp("tsk_id", nTaskId)
        AskClientForNumber("GMT_OnSetTaskValNum", 0, 2000000000, {L("Nhập giá trị Task:")})
    end
end

function GMT_OnSetTaskValNum(nVal)
    local nTaskId = GMT_GetTmp("tsk_id")
    if nTaskId and nTaskId >= 0 and nVal then
        SetTask(nTaskId, nVal)
        Msg2Player("[GM] Task(" .. nTaskId .. ") = " .. nVal)
    end
end

function GMT_PrintSnapshot()
    local nW, nX, nY = GetWorldPos()
    Msg2Player("[GM] Lv=" .. GetLevel() .. " Cash=" .. GetCash() .. " PK=" .. GetPK() .. " Map=" .. nW .. " (" .. nX .. "," .. nY .. ") Free=" .. CalcFreeItemCellCount())
end

function GMT_AskGetTask()
    AskClientForNumber("GMT_OnGetTask", 0, 65535, {L("Nhập Task ID (0..65535):")})
end
function GMT_OnGetTask(nTaskId)
    if nTaskId and nTaskId >= 0 then
        Msg2Player("[GM] Task(" .. nTaskId .. ") = " .. GetTask(nTaskId))
    end
end

function GMT_AskSetTask1()
    AskClientForNumber("GMT_OnSetTask1", 0, 65535, {L("Nhập Task ID cần đặt = 1 (0..65535):")})
end
function GMT_OnSetTask1(nTaskId)
    if nTaskId and nTaskId >= 0 then
        SetTask(nTaskId, 1)
    end
end

function GMT_AskSetTask0()
    AskClientForNumber("GMT_OnSetTask0", 0, 65535, {L("Nhập Task ID cần đặt = 0 (0..65535):")})
end
function GMT_OnSetTask0(nTaskId)
    if nTaskId and nTaskId >= 0 then
        SetTask(nTaskId, 0)
    end
end
"""
    return script.encode("ascii")


def build_clean_login_lua(profile: str) -> bytes:
    """Generates clean Zero-Sim `server1/script/global/login.lua` for the given base profile."""
    gm_grant_block = f"""
    -- Profile '{profile}': auto-grant MECE Atomic GM Token (6, 1, 4258) on login
    if (CalcEquiproomItemCount(6, 1, 4258, -1) == 0) then
        local nGmIdx = AddItem(6, 1, 4258, 1, 0, 0)
        if nGmIdx and nGmIdx > 0 then
            SetItemBindState(nGmIdx, -1)
        end
    end
"""
    helper_fn = ""

    content = f"""Include("\\\\script\\\\global\\\\login_head.lua")
Include("\\\\script\\\\global\\\\nobitaxd\\\\config\\\\cfg_server.lua")
Include("\\\\script\\\\global\\\\login_old.lua")
Include("\\\\script\\\\missions\\\\leaguematch\\\\wlls_login.lua")
Include("\\\\script\\\\misc\\\\extpoint_loginmsg\\\\login_msg.lua")
Include("\\\\script\\\\global\\\\offline_login.lua")
Include("\\\\script\\\\global\\\\recordplayerinfo.lua")
Include("\\\\script\\\\nationalwar\\\\login.lua")
Include("\\\\script\\\\misc\\\\daiyitoushi\\\\toushi_resetbase.lua")
Include("\\\\script\\\\activitysys\\\\playerfunlib.lua")
Include("\\\\script\\\\global\\\\playerlist.lua")
Include("\\\\script\\\\global\\\\login_hint.lua")
Include("\\\\script\\\\tasktrace\\\\tasktrace.lua")
Include("\\\\script\\\\activitysys\\\\g_activity.lua")
Include("\\\\script\\\\tong\\\\tong_login.lua")
Include("\\\\script\\\\item\\\\tianziyuxi.lua")
Include("\\\\script\\\\misc\\\\taskmanager.lua")
Include("\\\\script\\\\misc\\\\eventsys\\\\type\\\\player.lua")
Include("\\\\script\\\\bonus_onlinetime\\\\head.lua")
Include("\\\\script\\\\vng_feature\\\\resetbox.lua")
Include("\\\\script\\\\vng_feature\\\\top10\\\\vngtop10.lua")
Include("\\\\script\\\\rankexp\\\\rankexp.lua")
{helper_fn}
function main(bExchangeIn)
    SendExpRanking()
{gm_grant_block}
    PlayerList:AddPlayer(PlayerIndex)
    if (TB_LOGIN_FUN[0]) then
        for i = 1, getn(TB_LOGIN_FUN[0]) do
            local func = TB_LOGIN_FUN[0][i]
            if (func) then
                func(bExchangeIn)
            end
        end
    end

    EventSys:GetType("OnLogin"):OnPlayerEvent(0, PlayerIndex, bExchangeIn)
    G_ACTIVITY:OnMessage("OnLogin", PlayerIndex)

    for i = 1, getn(TB_LOGIN_FILEFUN) do
        local reg = TB_LOGIN_FILEFUN[i]
        DynamicExecute(reg[1], reg[2], PlayerIndex, bExchangeIn)
    end
    TaskManager:ResetUseGroup()
    LoginDelaySync(1)
end

function main_delaysync(nStep)
    if (nStep < 1 or nStep > getn(TB_LOGIN_FUN)) then
        return 1
    end
    if (TB_LOGIN_FUN[nStep]) then
        for i = 1, getn(TB_LOGIN_FUN[nStep]) do
            if (TB_LOGIN_FUN[nStep][i]) then TB_LOGIN_FUN[nStep][i]() end
        end
    end
    if (nStep < getn(TB_LOGIN_FUN)) then
        return 0
    else
        return 1
    end
end
"""
    return content.encode("ascii")


def build_minimal_worldset_ini() -> bytes:
    lines = [
        "[Init]",
        "ServerID=1",
        "",
        "[World]",
        f"Count={len(MINIMAL_TEST_MAPS)}",
    ]
    for idx, map_id in enumerate(MINIMAL_TEST_MAPS):
        lines.append(f"World{idx:02d}={map_id}")
    lines.append("")
    return "\r\n".join(lines).encode("ascii")


def sanitize_lua_bytes(rel_path: str, data: bytes) -> bytes:
    """Removes Sim/Bot/Auto/VSD hooks from existing Lua files."""
    data = SIM_INCLUDE_RE.sub(rb"-- [ZERO-SIM] \1", data)

    if rel_path == "server1/script/global/nobitaxd/config/cfg_server.lua":
        for key, val in CFG_ZERO_FLAGS.items():
            pattern = re.compile(rb"^(\s*" + re.escape(key) + rb"\s*=\s*)[^\r\n#\-]+(.*)$", re.MULTILINE)
            data = pattern.sub(rb"\g<1>" + val + rb"\t\g<2>", data)

    elif rel_path == "server1/script/global/autoexec.lua":
        for needle in (
            b'DynamicExecute("\\\\script\\\\missions\\\\sevencity\\\\bosswar.lua","BW_Start")',
            b"BNM_AddOfflineNPCs();",
            b"BTMC_AddNpc();",
            b"TCK_TD_BatDau();",
        ):
            data = data.replace(needle, b"-- [ZERO-SIM] " + needle)

    elif rel_path == "server1/script/missions/leaguematch/wlls_autoexec.lua":
        data = data.replace(
            b"if LSIM_Config.enabled == 1 and LSIM_IsMap(n_mapid) then return end",
            b"if LSIM_Config and LSIM_Config.enabled == 1 and LSIM_IsMap and LSIM_IsMap(n_mapid) then return end",
        )

    elif rel_path == "server1/script/missions/leaguematch/npc/officer.lua":
        if b"function main(" not in data:
            data = data + b"\nfunction main()\n\treturn wlls_officer_main_goc()\nend\n"

    elif rel_path == "server1/script/global/tongcreatesuccess.lua":
        data = data.replace(b"SG_OnCreateTong()", b"-- [ZERO-SIM] SG_OnCreateTong()")

    elif rel_path == "server1/script/battles/marshal/mission.lua":
        data = data.replace(b"simTK:removeSimTK(subworldid)", b"-- [ZERO-SIM] simTK:removeSimTK(subworldid)")

    elif rel_path in ("server1/script/tong/tong_mix.lua", "gateway/s3relay/script/tong/tong_mix.lua"):
        # Restore legacy default G_1 / G_2 pass-through callbacks commented out by private-server debug edits
        if b"\nMEMBER_ADD_G_1" not in data and b"\r\nMEMBER_ADD_G_1" not in data:
            data = data + b"\nMEMBER_ADD_G_1 = DefFun3\nMEMBER_ADD_G_2 = DefFun3\n"

    elif rel_path == "server1/script/missions/sevencity/sim_cities.lua":
        if b"-- [ZERO-SIM] Safe Lua 4.0 fallbacks" not in data:
            data = SEVENCITY_SG_FALLBACK_STUB + b"\n" + data

    return data


def normalize_rel_path(member_name: str) -> str:
    p = member_name.lstrip("./")
    if p == "jxser":
        return ""
    if p.startswith("jxser/"):
        p = p[len("jxser/"):]
    return p


def should_drop(rel_path: str) -> bool:
    if not rel_path:
        return True
    if rel_path in DROPPED_EXACT_REL_PATHS:
        return True
    if any(rel_path.startswith(prefix) or rel_path == prefix.rstrip("/") for prefix in DROPPED_PREFIXES):
        return True
    if not (rel_path == "gateway" or rel_path.startswith("gateway/") or rel_path == "server1" or rel_path.startswith("server1/")):
        return True
    return False


def add_bytes_entry(tf_out: tarfile.TarFile, arcname: str, payload: bytes, mode: int = 0o644, mtime: int = 1730000000) -> None:
    ti = tarfile.TarInfo(name=arcname)
    ti.size = len(payload)
    ti.mode = mode
    ti.mtime = mtime
    ti.type = tarfile.REGTYPE
    tf_out.addfile(ti, io.BytesIO(payload))


def add_dir_entry(tf_out: tarfile.TarFile, arcname: str, mtime: int = 1730000000) -> None:
    ti = tarfile.TarInfo(name=arcname.rstrip("/"))
    ti.mode = 0o755
    ti.mtime = mtime
    ti.type = tarfile.DIRTYPE
    tf_out.addfile(ti)


def recover_ntfs_mojibake_segment(seg: str) -> str:
    """Converts Windows NTFS mojibake segment back to raw surrogateescape Linux bytes."""
    if all(ord(c) < 128 for c in seg):
        return seg
    # Check double-encoded UTF-8-as-CP1252/Latin1 mojibake (e.g. 'Ã‚Â·...' or 'Â·Ã§...')
    try:
        b1 = seg.encode("cp1252")
    except UnicodeEncodeError:
        try:
            b1 = seg.encode("latin1")
        except UnicodeEncodeError:
            try:
                b1 = seg.encode("gbk")
                return b1.decode("utf-8", "surrogateescape")
            except UnicodeEncodeError:
                return seg

    # Attempt UTF-8 decode -> Latin1 raw bytes if it was UTF-8 decoded as CP1252/Latin1
    try:
        u = b1.decode("utf-8")
        if all(ord(ch) <= 0xFF for ch in u):
            raw = u.encode("latin1")
            return raw.decode("utf-8", "surrogateescape")
    except UnicodeDecodeError:
        pass

    return b1.decode("utf-8", "surrogateescape")


def iter_source_entries_from_tar(source_tar: Path):
    with tarfile.open(source_tar, "r:gz", encoding="utf-8", errors="surrogateescape") as tf_in:
        for member in tf_in:
            rel = normalize_rel_path(member.name)
            if should_drop(rel):
                continue
            if member.isdir():
                yield (rel, True, 0o755, int(member.mtime), b"")
            elif member.isfile():
                f = tf_in.extractfile(member)
                payload = f.read() if f is not None else b""
                mode = 0o755 if rel in EXECUTABLE_REL_PATHS else (member.mode or 0o644)
                yield (rel, False, mode, int(member.mtime), payload)


def iter_source_entries_from_dir(source_dir: Path):
    for sub_root in ("gateway", "server1"):
        base = source_dir / sub_root
        if not base.exists():
            raise FileNotFoundError(f"Missing required directory: {base}")
        for root, dirs, files in os.walk(base):
            rel_dir_win = Path(root).relative_to(source_dir).as_posix()
            rel_dir = "/".join(recover_ntfs_mojibake_segment(s) for s in rel_dir_win.split("/"))
            if should_drop(rel_dir):
                dirs[:] = []
                continue
            yield (rel_dir, True, 0o755, 1730000000, b"")
            for fname in sorted(files):
                rel_file = f"{rel_dir}/{recover_ntfs_mojibake_segment(fname)}"
                if should_drop(rel_file):
                    continue
                fpath = Path(root) / fname
                payload = fpath.read_bytes()
                mode = 0o755 if rel_file in EXECUTABLE_REL_PATHS else 0o644
                yield (rel_file, False, mode, int(fpath.stat().st_mtime), payload)


def build_profile_archive(
    profile: str,
    entries: list[tuple[str, bool, int, int, bytes]],
    output_file: Path,
) -> tuple[int, str]:
    output_file.parent.mkdir(parents=True, exist_ok=True)
    tmp_file = output_file.with_suffix(".tmp.tar.gz")

    overrides: dict[str, bytes] = {
        "server1/preload.txt": b"# Zero-Sim clean runtime: no custom LD_PRELOAD .so libraries\n",
        "server1/script/global/login.lua": build_clean_login_lua(profile),
        "server1/script/global/nobitaxd/item/camnang_gm.lua": build_camnang_gm_lua(enabled=True),
        "server1/maps/worldset.ini": build_minimal_worldset_ini(),
        "server1/script/missions/leaguematch/sims/config.lua": LSIM_CONFIG_STUB,
        "server1/script/missions/leaguematch/sims/startup.lua": LSIM_STARTUP_STUB,
    }

    written_paths: set[str] = set()

    with open(tmp_file, "wb") as raw_out:
        with tarfile.open(
            fileobj=raw_out,
            mode="w:gz",
            format=tarfile.GNU_FORMAT,
            encoding="utf-8",
            errors="surrogateescape",
            compresslevel=6,
        ) as tf_out:
            add_dir_entry(tf_out, "jxser")
            for rel_dir in ("gateway/Logs", "gateway/Backup", "gateway/roleback", "server1/Logs", "server1/dulieu"):
                written_paths.add(rel_dir)

            for rel, is_dir, mode, mtime, payload in entries:
                if rel in written_paths:
                    continue
                arcname = f"jxser/{rel}"
                if is_dir:
                    add_dir_entry(tf_out, arcname, mtime)
                    written_paths.add(rel)
                    continue

                if rel in overrides:
                    payload = overrides[rel]
                elif rel.lower().endswith(".lua"):
                    payload = sanitize_lua_bytes(rel, payload)

                add_bytes_entry(tf_out, arcname, payload, mode=mode, mtime=mtime)
                written_paths.add(rel)

            for req_dir in ("gateway/Logs", "gateway/Backup", "gateway/roleback", "server1/Logs", "server1/dulieu"):
                add_dir_entry(tf_out, f"jxser/{req_dir}")

            for rel, payload in overrides.items():
                if rel not in written_paths:
                    add_bytes_entry(tf_out, f"jxser/{rel}", payload, mode=0o644)
                    written_paths.add(rel)

    tmp_file.replace(output_file)
    sha256 = hashlib.sha256(output_file.read_bytes()).hexdigest()
    return output_file.stat().st_size, sha256


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Zero-Sim JX-Linux 6.0 `.tar.gz` profiles.")
    parser.add_argument(
        "--profile",
        choices=["v1"],
        default="v1",
        help="Which package profile to build (default: v1).",
    )
    parser.add_argument("--source-tar", type=str, default="", help="Clean Linux source .tar.gz archive (preferred).")
    parser.add_argument("--source-dir", type=str, default="", help="Fallback Windows jx-linux-bin directory.")
    parser.add_argument("--output-dir", type=str, required=True, help="Output directory for generated .tar.gz files.")
    args = parser.parse_args()

    source_tar = Path(args.source_tar) if args.source_tar else None
    source_dir = Path(args.source_dir) if args.source_dir else None

    t0 = time.time()
    if source_tar and source_tar.is_file():
        print(f"[PACK] Reading clean Linux tar stream: {source_tar}")
        entries = list(iter_source_entries_from_tar(source_tar))
    elif source_dir and source_dir.is_dir():
        print(f"[PACK] Reading directory source (with mojibake recovery): {source_dir}")
        entries = list(iter_source_entries_from_dir(source_dir))
    else:
        print("[PACK] ERROR: Provide a valid --source-tar or --source-dir.", file=sys.stderr)
        return 2

    print(f"[PACK] Loaded {len(entries)} sanitized entries in {time.time() - t0:.1f}s.")

    profile_names = [args.profile]
    file_map = {
        "v1": "jxser-v1.tar.gz",
    }

    out_dir = Path(args.output_dir)
    for prof in profile_names:
        t_prof = time.time()
        out_path = out_dir / file_map[prof]
        size_bytes, sha = build_profile_archive(prof, entries, out_path)
        size_mb = size_bytes / (1024 * 1024)
        print(f"[PACK] Built '{prof}' -> {out_path} ({size_mb:.2f} MB, sha256={sha[:16]}..., {time.time() - t_prof:.1f}s)")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
