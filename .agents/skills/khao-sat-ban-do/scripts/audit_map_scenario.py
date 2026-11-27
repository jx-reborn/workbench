#!/usr/bin/env python3
"""Deterministic Ground-Truth Map Extractor, Set-Cover Solver & Scenario Coverage Auditor.

Reads 100% Ground Truth directly from `legacy-server/bin/` (`server1/pak/maps.pak` Region_S.dat + .wor,
`settings/maplist.ini`, `settings/npcs.txt`, `settings/obj/ObjData.txt`, `lang/vn/replacename_npc.txt`,
`script/**/trap/*.lua`, `script/global/autoexec.lua`, `script/global/addspreadernpc.lua`,
`script/missions/leaguematch/wlls_autoexec.lua`) plus `Region_C.dat` from `headless-client` `.pak` archives.

Subcommands:
  inventory   Dump 100% active Ground-Truth entities, gates, quests, and Zero-Sim worldset status for a map.
  plan-cover  Solve Greedy Disk Set Cover + 2-opt TSP for `crawl_region_npcs` & clean `gm_teleport` anchors.
  audit       Statically simulate a `.scenario.json` and verify terrain safety + entity/branch coverage.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import math
import os
import re
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# Import encoding helpers from sibling legacy-explore skill
_SKILLS_ROOT = Path(__file__).resolve().parents[2]
_LEGACY_EXPLORE_SCRIPTS = _SKILLS_ROOT / "legacy-explore" / "scripts"
if str(_LEGACY_EXPLORE_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_LEGACY_EXPLORE_SCRIPTS))

import read_jx_file  # type: ignore  # noqa: E402


# ---------------------------------------------------------------------------
# Path Resolution
# ---------------------------------------------------------------------------

def find_repo_root() -> Path:
    """Locate parent directory containing workbench/ and legacy-server/."""
    for anchor in (Path(__file__).resolve().parent, Path.cwd().resolve()):
        for p in (anchor, *anchor.parents):
            if (p / "workbench").is_dir() and (p / "legacy-server").is_dir():
                return p
            if p.name.lower() == "workbench" and (p.parent / "legacy-server").is_dir():
                return p.parent
    raise FileNotFoundError("Could not resolve jx-reborn workspace root containing legacy-server/ and workbench/.")


def resolve_server1_dir(explicit: str | None = None) -> Path:
    if explicit:
        p = Path(explicit).expanduser().resolve()
        if (p / "settings").is_dir():
            return p
        if (p / "bin" / "server1").is_dir():
            return p / "bin" / "server1"
        if (p / "server1").is_dir():
            return p / "server1"
    env_dir = os.environ.get("LEGACY_SERVER_DIR", "").strip()
    if env_dir:
        cand = Path(env_dir).expanduser().resolve()
        for sub in (cand, cand / "bin" / "server1", cand / "server1"):
            if (sub / "settings").is_dir():
                return sub
    root = find_repo_root()
    for sub in (root / "legacy-server" / "bin" / "server1", root / "legacy-server" / "server1"):
        if (sub / "settings").is_dir():
            return sub
    raise FileNotFoundError("Could not locate legacy-server/bin/server1 directory.")


def resolve_headless_client_dir(explicit: str | None = None) -> Path | None:
    if explicit and Path(explicit).is_dir():
        return Path(explicit).resolve()
    env_dir = os.environ.get("LEGACY_HEADLESS_TEMPLATE_DIR", "").strip()
    if env_dir and Path(env_dir).is_dir():
        return Path(env_dir).resolve()
    root = find_repo_root()
    cand = root / "workbench" / "artifacts" / "headless-client"
    if cand.is_dir():
        return cand
    return None


def load_minimal_test_maps() -> set[int]:
    """Read MINIMAL_TEST_MAPS from workbench/qa/scripts/pack_legacy_profiles.py if available."""
    try:
        packer = find_repo_root() / "workbench" / "qa" / "scripts" / "pack_legacy_profiles.py"
        if not packer.is_file():
            return set()
        txt = packer.read_text(encoding="utf-8", errors="replace")
        m = re.search(r"MINIMAL_TEST_MAPS\s*:\s*list\[int\]\s*=\s*\[(.*?)\]", txt, re.S)
        if not m:
            return set()
        body = re.sub(r"#.*", "", m.group(1))
        return {int(x) for x in re.findall(r"\b\d+\b", body)}
    except Exception:
        return set()


# ---------------------------------------------------------------------------
# Kingsoft XPack (.pak) + UCL NRV2B_8 Decompressor
# ---------------------------------------------------------------------------

def hash_pak_path(raw_bytes: bytes) -> int:
    """Exact port of Kingsoft g_FileName2Id / PakFilePathHasher."""
    h = 0
    for i, b in enumerate(raw_bytes):
        if 0x41 <= b <= 0x5A:  # 'A'..'Z' -> lowercase
            b += 0x20
        c = ctypes.c_int8(b).value
        term = (h + ((i + 1) * c)) & 0xFFFFFFFF
        h = ((term % 0x8000000B) * 0xFFFFFFEF) & 0xFFFFFFFF
    return (h ^ 0x12345678) & 0xFFFFFFFF


def decompress_ucl_nrv2b8(src: bytes, dst_len: int) -> bytes:
    """Exact port of UCL NRV2B_8 decompressor used in Kingsoft .pak archives."""
    dst = bytearray(dst_len)
    si = 0
    di = 0
    bb = 0
    bc = 0
    last_m_off = 1

    def get_bit() -> int:
        nonlocal si, bb, bc
        if bc == 0:
            bb = src[si]
            si += 1
            bc = 8
        bc -= 1
        return (bb >> bc) & 1

    while True:
        while get_bit() == 1:
            dst[di] = src[si]
            di += 1
            si += 1

        m_off = 1
        while True:
            m_off = (m_off << 1) + get_bit()
            if get_bit() != 0:
                break

        if m_off == 2:
            m_off = last_m_off
        else:
            m_off = ((m_off - 3) << 8) + src[si]
            si += 1
            if m_off == 0xFFFFFFFF:
                break
            m_off += 1
            last_m_off = m_off

        m_len = (get_bit() << 1) | get_bit()
        if m_len == 0:
            m_len = 1
            while True:
                m_len = (m_len << 1) + get_bit()
                if get_bit() != 0:
                    break
            m_len += 2

        if m_off > 0xD00:
            m_len += 1

        m_len += 1
        copy_src = di - m_off
        for _ in range(m_len):
            dst[di] = dst[copy_src]
            di += 1
            copy_src += 1

    return bytes(dst)


class XPackGroup:
    """Mounted list of Kingsoft .pak archives following package.ini priority."""

    def __init__(self, pak_paths: list[Path]) -> None:
        self._mounts: list[tuple[Path, dict[int, tuple[int, int, int, int]]]] = []
        for p in pak_paths:
            if p.is_file():
                self._mounts.append((p, self._read_index(p)))

    @classmethod
    def from_root(cls, root_dir: Path, fallback_subdirs: tuple[str, ...] = ("pak", "data", "data1")) -> "XPackGroup":
        pkg_ini = root_dir / "package.ini"
        pak_sub = None
        ordered: list[tuple[int, str]] = []
        if pkg_ini.is_file():
            for raw in pkg_ini.read_bytes().splitlines():
                line = raw.decode("latin1").strip()
                if not line or line.startswith(";") or line.startswith("[") or "=" not in line:
                    continue
                k, v = [x.strip() for x in line.split("=", 1)]
                if k.lower() == "path":
                    pak_sub = v.lstrip("\\/")
                elif k.isdigit() and v:
                    ordered.append((int(k), v))
        candidates: list[Path] = []
        folders = ([root_dir / pak_sub] if pak_sub else []) + [root_dir / s for s in fallback_subdirs]
        pak_folder = next((f for f in folders if f.is_dir()), None)
        if pak_folder is not None:
            for _, name in sorted(ordered, key=lambda x: x[0]):
                fp = pak_folder / name
                if fp.is_file():
                    candidates.append(fp)
            if not candidates and (pak_folder / "maps.pak").is_file():
                candidates.append(pak_folder / "maps.pak")
        return cls(candidates)

    @staticmethod
    def _read_index(pak_path: Path) -> dict[int, tuple[int, int, int, int]]:
        idx: dict[int, tuple[int, int, int, int]] = {}
        with pak_path.open("rb") as f:
            hdr = f.read(20)
            if len(hdr) < 20:
                return idx
            count, index_off = struct.unpack_from("<II", hdr, 4)
            f.seek(index_off)
            buf = f.read(count * 16)
            for i in range(count):
                entry_id, offset, ulen, clen_flag = struct.unpack_from("<IIII", buf, i * 16)
                idx[entry_id] = (offset, ulen, clen_flag & 0x00FFFFFF, (clen_flag >> 24) & 0xFF)
        return idx

    def read_entry(self, internal_path: bytes) -> bytes | None:
        h = hash_pak_path(internal_path)
        for pak_path, idx in self._mounts:
            entry = idx.get(h)
            if entry is None:
                continue
            offset, ulen, clen, method = entry
            with pak_path.open("rb") as f:
                f.seek(offset)
                raw = f.read(ulen if method == 0 else clen)
            if method == 0:
                return raw
            if method in (1, 0x20):
                return decompress_ucl_nrv2b8(raw, ulen)
            return decompress_ucl_nrv2b8(raw, ulen)
        return None


# ---------------------------------------------------------------------------
# Ground-Truth Data Models & Binary Readers
# ---------------------------------------------------------------------------

@dataclass
class NpcEntity:
    source: str  # "Region_S" | "autoexec" | "addspreadernpc" | "wlls_autoexec"
    template_id: int
    display_name: str
    kind: int  # 3 = DialogNpc, 0 = Monster
    mps_x: int
    mps_y: int
    script_path: str
    level_gates: list[int] = field(default_factory=list)
    shop_ids: list[int] = field(default_factory=list)
    tasks_referenced: list[int] = field(default_factory=list)
    uses_input_number: bool = False

    @property
    def tile_x(self) -> int:
        return self.mps_x // 32

    @property
    def tile_y(self) -> int:
        return self.mps_y // 32


@dataclass
class ObjEntity:
    data_id: int
    display_name: str
    mps_x: int
    mps_y: int
    script_path: str
    opens_box: bool = False

    @property
    def tile_x(self) -> int:
        return self.mps_x // 32

    @property
    def tile_y(self) -> int:
        return self.mps_y // 32


@dataclass
class TrapEntity:
    trap_id: int
    cells: list[tuple[int, int]]  # (tileX, tileY)
    script_path: str
    is_bidirectional_gate: bool = False
    target_map_id: int | None = None
    min_level: int | None = None
    in_minimal_worldset: bool = True

    @property
    def sample_tile(self) -> tuple[int, int]:
        mid = len(self.cells) // 2
        return self.cells[mid]


@dataclass
class MapGroundTruth:
    map_id: int
    map_name: str
    sub_path_display: str
    rect: tuple[int, int, int, int]  # left, top, right, bottom regions
    in_minimal_worldset: bool
    srv_obstacles: dict[tuple[int, int], list[int]]  # (rx, ry) -> 512 ints
    cli_obstacles: dict[tuple[int, int], list[int]]  # (rx, ry) -> 512 ints
    cell_traps: dict[tuple[int, int], int]  # (tileX, tileY) -> trapId
    dialog_npcs: list[NpcEntity]
    scripted_monsters: list[NpcEntity]
    monster_summary: dict[str, int]
    objects: list[ObjEntity]
    traps: list[TrapEntity]
    skipped_spawners: list[str]

    def get_srv_obs(self, tx: int, ty: int) -> int:
        rx, lx = divmod(tx, 16)
        ry, ly = divmod(ty, 32)
        arr = self.srv_obstacles.get((rx, ry))
        if arr is None:
            return 1
        return arr[lx * 32 + ly] & 0x0F

    def get_cli_obs(self, tx: int, ty: int) -> int:
        if not self.cli_obstacles:
            return 0
        rx, lx = divmod(tx, 16)
        ry, ly = divmod(ty, 32)
        arr = self.cli_obstacles.get((rx, ry))
        if arr is None:
            return 0
        return arr[lx * 32 + ly] & 0x0F

    def get_trap_id(self, tx: int, ty: int) -> int:
        return self.cell_traps.get((tx, ty), 0)

    def is_clean_tile(self, tx: int, ty: int) -> bool:
        rx, ry = tx // 16, ty // 32
        if (rx, ry) not in self.srv_obstacles:
            return False
        return self.get_srv_obs(tx, ty) == 0 and self.get_cli_obs(tx, ty) == 0 and self.get_trap_id(tx, ty) == 0

    def is_roomy_clean_tile(self, tx: int, ty: int) -> bool:
        """Clean tile with at least 3 walkable cardinal neighbors so the player isn't boxed in."""
        if not self.is_clean_tile(tx, ty):
            return False
        open_neighbors = sum(
            1
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))
            if self.get_srv_obs(tx + dx, ty + dy) == 0 and self.get_cli_obs(tx + dx, ty + dy) == 0
        )
        return open_neighbors >= 3


# ---------------------------------------------------------------------------
# Loaders from legacy-server/bin/
# ---------------------------------------------------------------------------

def _trim_cstr(raw: bytes) -> bytes:
    nul = raw.find(0)
    return raw[:nul] if nul >= 0 else raw


def decode_smart(raw: bytes) -> str:
    raw = _trim_cstr(raw).strip()
    if not raw:
        return ""
    return read_jx_file._decode_segment_auto(raw).strip()


def load_replace_names(server1_dir: Path) -> tuple[dict[bytes, str], dict[str, str]]:
    by_bytes: dict[bytes, str] = {}
    by_str: dict[str, str] = {}
    fp = server1_dir / "lang" / "vn" / "replacename_npc.txt"
    if not fp.is_file():
        return by_bytes, by_str
    lines = fp.read_bytes().splitlines()
    for raw in lines[1:]:
        if b"\t" not in raw:
            continue
        parts = raw.split(b"\t")
        src_b = _trim_cstr(parts[0]).strip()
        dst_b = _trim_cstr(parts[1]).strip()
        if not src_b or not dst_b:
            continue
        dst_str = read_jx_file.decode_tcvn3(dst_b).strip()
        if not dst_str:
            continue
        by_bytes[src_b] = dst_str
        for enc in ("gb18030", "latin1"):
            try:
                by_str[src_b.decode(enc).strip()] = dst_str
            except Exception:
                pass
        by_str[read_jx_file.decode_tcvn3(src_b).strip()] = dst_str
    return by_bytes, by_str


def unescape_lua_bytes(raw: bytes) -> bytes:
    """Resolve Lua decimal byte escapes like \\251, \\242, \\241 into raw single bytes."""
    def _rep(m: re.Match[bytes]) -> bytes:
        val = int(m.group(1))
        return bytes([val & 0xFF])
    return re.sub(rb"\\(\d{1,3})", _rep, raw)


def translate_npc_name(raw_bytes: bytes, default_name: str, rep_b: dict[bytes, str], rep_s: dict[str, str]) -> str:
    clean = _trim_cstr(unescape_lua_bytes(raw_bytes)).strip()
    if clean:
        if clean in rep_b:
            return rep_b[clean]
        dec = decode_smart(clean)
        if dec in rep_s:
            return rep_s[dec]
        return dec
    if default_name in rep_s:
        return rep_s[default_name]
    return default_name


def load_npc_templates(server1_dir: Path, rep_b: dict[bytes, str], rep_s: dict[str, str]) -> dict[int, tuple[str, int]]:
    """Return {tpl_id: (display_name, kind)} from settings/npcs.txt (row 2 = id 0 with row-2 default inheritance)."""
    out: dict[int, tuple[str, int]] = {}
    fp = server1_dir / "settings" / "npcs.txt"
    lines = fp.read_bytes().splitlines()
    if len(lines) < 2:
        return out
    row2_cols = lines[1].split(b"\t")
    default_kind = int(row2_cols[1]) if len(row2_cols) > 1 and row2_cols[1].strip().lstrip(b"-").isdigit() else 0
    for idx, raw in enumerate(lines[1:]):
        cols = raw.split(b"\t")
        raw_name = cols[0] if cols else b""
        name = translate_npc_name(raw_name, "", rep_b, rep_s) or f"Npc_{idx}"
        kind_b = cols[1].strip() if len(cols) > 1 else b""
        kind = int(kind_b) if kind_b.lstrip(b"-").isdigit() else default_kind
        out[idx] = (name, kind)
    return out


def load_obj_templates(server1_dir: Path) -> dict[int, tuple[str, str]]:
    """Return {data_id: (name, default_script)} from settings/obj/ObjData.txt."""
    out: dict[int, tuple[str, str]] = {}
    fp = server1_dir / "settings" / "obj" / "ObjData.txt"
    if not fp.is_file():
        return out
    lines = fp.read_bytes().splitlines()
    for raw in lines[1:]:
        cols = raw.split(b"\t")
        if len(cols) < 4:
            continue
        did_b = cols[1].strip()
        if not did_b.isdigit():
            continue
        did = int(did_b)
        name = decode_smart(cols[0])
        scr = normalize_script_display(cols[3])
        out[did] = (name, scr)
    return out


def normalize_script_display(raw_path: bytes) -> str:
    raw_path = _trim_cstr(raw_path).strip().replace(b"/", b"\\")
    if not raw_path:
        return ""
    if not raw_path.startswith(b"\\"):
        raw_path = b"\\" + raw_path
    try:
        return raw_path.decode("gb18030")
    except UnicodeDecodeError:
        return decode_smart(raw_path)


def resolve_script_on_disk(server1_dir: Path, script_display_or_raw: str | bytes) -> Path | None:
    if not script_display_or_raw:
        return None
    if isinstance(script_display_or_raw, bytes):
        rel_b = _trim_cstr(script_display_or_raw).strip().replace(b"/", b"\\").lstrip(b"\\")
        variants = [rel_b.decode("latin1", errors="ignore")]
        try:
            variants.append(rel_b.decode("gb18030"))
        except UnicodeDecodeError:
            pass
    else:
        s = script_display_or_raw.strip().replace("/", "\\").lstrip("\\")
        variants = [s]
        try:
            variants.append(s.encode("gb18030").decode("latin1"))
        except UnicodeEncodeError:
            pass
    for v in variants:
        cand = server1_dir / v
        if cand.is_file():
            return cand
    return None


_CONNECT_TRAP_DIRS = frozenset({
    "trap",
    "连接trap",
    b"\xc1\xac\xbd\xd3trap".decode("latin1").lower(),
})


def _has_trap_dir_segment(path_obj: Path) -> bool:
    return any(part.lower() in _CONNECT_TRAP_DIRS for part in path_obj.parts)


def inspect_lua_semantics(lua_file: Path | None) -> dict[str, Any]:
    info: dict[str, Any] = {
        "level_gates": [],
        "shop_ids": [],
        "tasks_referenced": [],
        "uses_input_number": False,
        "opens_box": False,
        "is_bidirectional_gate": False,
        "target_map_id": None,
        "min_level": None,
    }
    if lua_file is None or not lua_file.is_file():
        return info

    text_lines: list[str] = []
    for raw in lua_file.read_bytes().splitlines():
        line = read_jx_file.decode_line(raw)
        stripped = line.strip()
        if not stripped.startswith("--"):
            text_lines.append(line)
    body = "\n".join(text_lines)

    is_trap_file = _has_trap_dir_segment(lua_file)
    if is_trap_file:
        levels = {int(m.group(2)) for m in re.finditer(r"GetLevel\(\)\s*(>=|>|<|<=|==)\s*(\d+)", body)}
    else:
        # Extract entry-level gates in main() / yes() (up to Lv 40 starter/travel gates), ignoring deep giftcode sub-menus
        entry_chunk_m = re.search(r"function\s+main\s*\(.*?\)(.*?)(?=\nfunction\s+(?!yes\b)|\Z)", body, re.S)
        entry_chunk = entry_chunk_m.group(1) if entry_chunk_m else body[:1200]
        levels = {
            int(m.group(2))
            for m in re.finditer(r"GetLevel\(\)\s*(>=|>|<|<=|==)\s*(\d+)", entry_chunk)
            if int(m.group(2)) <= 40
        }
        if "NewcomerStationCommon" in body or "WharfFun" in body:
            levels.add(5)
        if "seasonnpc" in str(lua_file).lower():
            levels.add(10)
        if "轻功_武师" in str(lua_file) or "Çá¹¦_ÎäÊ¦" in str(lua_file):
            levels.add(2)
    info["level_gates"] = sorted(levels)
    if levels:
        info["min_level"] = min(levels)

    info["shop_ids"] = sorted({int(m.group(1)) for m in re.finditer(r"\bSale\(\s*(\d+)", body)})
    info["tasks_referenced"] = sorted({int(m.group(1)) for m in re.finditer(r"\b(?:GetTask|SetTask|nt_getTask|nt_setTask)\(\s*(\d+)", body)})
    info["uses_input_number"] = "AskClientForNumber" in body
    info["opens_box"] = "OpenBox(" in body

    has_f1 = re.search(r"SetFightState\(\s*1\s*\)", body) is not None
    has_f0 = re.search(r"SetFightState\(\s*0\s*\)", body) is not None
    nw = re.search(r"NewWorld\(\s*(\d+)", body)
    if nw:
        info["target_map_id"] = int(nw.group(1))
    elif has_f1 and has_f0:
        info["is_bidirectional_gate"] = True

    return info


def scan_trap_scripts(server1_dir: Path) -> dict[int, tuple[str, Path]]:
    """Map trapId (g_FileName2Id hash) -> (displayScriptPath, diskPath)."""
    out: dict[int, tuple[str, Path]] = {}
    script_root = server1_dir / "script"
    for dp, _, fns in os.walk(script_root):
        if not _has_trap_dir_segment(Path(dp)):
            continue
        for fn in fns:
            if not fn.lower().endswith(".lua"):
                continue
            full = Path(dp) / fn
            rel_str = "\\" + str(full.relative_to(server1_dir)).replace("/", "\\")
            latin_bytes = rel_str.encode("latin1", errors="ignore")
            h1 = hash_pak_path(latin_bytes)
            disp = normalize_script_display(latin_bytes)
            out[h1] = (disp, full)
            try:
                gbk_bytes = disp.encode("gb18030")
                out[hash_pak_path(gbk_bytes)] = (disp, full)
            except UnicodeEncodeError:
                pass
    # Also scan settings/maps/**/trap*.txt dynamic bindings if any
    return out


def parse_region_s_bytes(data: bytes, rx: int, ry: int) -> tuple[list[int], list[tuple[int, int, int, int]], list[tuple[int, int, int, bytes, int, bytes]], list[tuple[int, int, int, bytes]]]:
    obstacles = [0] * 512
    traps: list[tuple[int, int, int, int]] = []
    npcs: list[tuple[int, int, int, bytes, int, bytes]] = []
    objs: list[tuple[int, int, int, bytes]] = []

    if len(data) < 4:
        return obstacles, traps, npcs, objs
    (sec_count,) = struct.unpack_from("<I", data, 0)
    if sec_count == 0 or sec_count > 32:
        return obstacles, traps, npcs, objs
    hdr_size = 4 + sec_count * 8
    if len(data) < hdr_size:
        return obstacles, traps, npcs, objs
    (sec0_off,) = struct.unpack_from("<I", data, 4)
    rel_offsets = sec0_off < hdr_size

    def get_sec(idx: int) -> bytes | None:
        if idx >= sec_count:
            return None
        raw_off, raw_len = struct.unpack_from("<II", data, 4 + idx * 8)
        if raw_len == 0:
            return None
        off = (hdr_size + raw_off) if rel_offsets else raw_off
        if off < hdr_size or off + raw_len > len(data):
            return None
        return data[off : off + raw_len]

    s0 = get_sec(0)
    if s0 and len(s0) >= 2048:
        obstacles = list(struct.unpack_from("<512i", s0, 0))

    s1 = get_sec(1)
    if s1 and len(s1) >= 12:
        (num_traps,) = struct.unpack_from("<I", s1, 0)
        pos = 12 if 12 + num_traps * 8 <= len(s1) else 4
        for _ in range(num_traps):
            if pos + 8 > len(s1):
                break
            cx, cy, cspan, _, trap_id = struct.unpack_from("<BBBBI", s1, pos)
            pos += 8
            if trap_id != 0 and cy < 32 and cx + max(1, cspan) - 1 < 16:
                traps.append((cx, cy, max(1, cspan), trap_id))

    s2 = get_sec(2)
    if s2 and len(s2) >= 12:
        (num_npcs,) = struct.unpack_from("<I", s2, 0)
        pos = 12
        for _ in range(num_npcs):
            if pos + 60 > len(s2):
                break
            tpl_id, px, py = struct.unpack_from("<iii", s2, pos)
            raw_name = s2[pos + 16 : pos + 48]
            kind = struct.unpack_from("<h", s2, pos + 54)[0]
            scr_len = struct.unpack_from("<H", s2, pos + 58)[0]
            pos += 60
            if pos + scr_len > len(s2):
                break
            scr_bytes = s2[pos : pos + scr_len]
            pos += scr_len
            if tpl_id >= 0:
                npcs.append((tpl_id, px, py, raw_name, kind, scr_bytes))

    s3 = get_sec(3)
    if s3 and len(s3) >= 12:
        (num_objs,) = struct.unpack_from("<I", s3, 0)
        pos = 12
        for _ in range(num_objs):
            if pos + 24 > len(s3):
                break
            data_id = struct.unpack_from("<i", s3, pos)[0]
            mx, my = struct.unpack_from("<ii", s3, pos + 8)
            scr_len = struct.unpack_from("<H", s3, pos + 22)[0]
            pos += 24
            if pos + scr_len > len(s3):
                break
            scr_bytes = s3[pos : pos + scr_len]
            pos += scr_len
            if data_id >= 1:
                objs.append((data_id, mx, my, scr_bytes))

    return obstacles, traps, npcs, objs


def scan_global_npc_spawns(
    server1_dir: Path,
    map_id: int,
    npc_templates: dict[int, tuple[str, int]],
    rep_b: dict[bytes, str],
    rep_s: dict[str, str],
) -> tuple[list[NpcEntity], list[str]]:
    """Extract active runtime DialogNpcs injected onto `map_id` by autoexec.lua & addspreadernpc.lua."""
    spawns: list[NpcEntity] = []
    skipped: list[str] = []

    # Check cfg_server.lua flags if present
    cfg_flags: dict[str, int] = {}
    cfg_fp = server1_dir / "script" / "global" / "pgaming" / "configserver" / "cfg_server.lua"
    if cfg_fp.is_file():
        for raw in cfg_fp.read_bytes().splitlines():
            line = read_jx_file.decode_line(raw).strip()
            if line.startswith("--"):
                continue
            m = re.match(r"^(CFG_\w+)\s*=\s*(\d+)", line)
            if m:
                cfg_flags[m.group(1)] = int(m.group(2))

    # 1. autoexec.lua tables: {tplId, mapId, tileX, tileY, script, name} and {tplId, lv, series, mapId, tileX, tileY, 0, name, 0, script}
    for source_label, rel in (
        ("autoexec", Path("script/global/autoexec.lua")),
        ("addspreadernpc", Path("script/global/addspreadernpc.lua")),
    ):
        fp = server1_dir / rel
        if not fp.is_file():
            continue
        for lineno, raw in enumerate(fp.read_bytes().splitlines(), 1):
            code_b, _ = read_jx_file._split_lua_comment_bytes(raw)
            code_s = code_b.decode("latin1").strip()
            if not code_s or code_s.startswith("--"):
                continue

            # Pattern A (6-tuple nw_npctab): {1783, 53, 1593, 3188, "\\script\\...", "Name"}
            ma = re.search(
                r"\{\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*\"([^\"]+)\"\s*,\s*\"([^\"]*)\"\s*\}",
                code_s,
            )
            if ma and int(ma.group(2)) == map_id:
                tpl_id = int(ma.group(1))
                tx, ty = int(ma.group(3)), int(ma.group(4))
                scr_raw = ma.group(5).encode("latin1").replace(b"\\\\", b"\\")
                name_raw = ma.group(6).encode("latin1")
                if tpl_id not in npc_templates:
                    skipped.append(f"{source_label}:L{lineno} tplId={tpl_id} >= max ({len(npc_templates)}) -> NpcTemplate Error on Legacy")
                    continue
                # Check if gated behind disabled CFG flag (e.g. shitouernpc / event inactive)
                disp_scr = normalize_script_display(scr_raw)
                if "zhongqiu_jieri" in disp_scr:
                    skipped.append(f"{source_label}:L{lineno} '{disp_scr}' seasonal event NPC skipped")
                    continue
                def_name, def_kind = npc_templates[tpl_id]
                name = translate_npc_name(name_raw, def_name, rep_b, rep_s)
                sem = inspect_lua_semantics(resolve_script_on_disk(server1_dir, scr_raw))
                spawns.append(
                    NpcEntity(
                        source=source_label,
                        template_id=tpl_id,
                        display_name=name,
                        kind=3 if disp_scr else def_kind,
                        mps_x=tx * 32,
                        mps_y=ty * 32,
                        script_path=disp_scr,
                        level_gates=sem["level_gates"],
                        shop_ids=sem["shop_ids"],
                        tasks_referenced=sem["tasks_referenced"],
                        uses_input_number=sem["uses_input_number"],
                    )
                )
                continue

            # Pattern B (10-tuple AddNpcEx table): {tplId, lv, ser, mapId, tileX, tileY, 0, "Name", 0, "\\script\\..."}
            mb = re.search(
                r"\{\s*(\d+)\s*,\s*\d+\s*,\s*-?\d+\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*\d+\s*,\s*\"([^\"]*)\"\s*,\s*\d+\s*,\s*\"([^\"]+)\"",
                code_s,
            )
            if mb and int(mb.group(2)) == map_id:
                tpl_id = int(mb.group(1))
                tx, ty = int(mb.group(3)), int(mb.group(4))
                name_raw = mb.group(5).encode("latin1")
                scr_raw = mb.group(6).encode("latin1").replace(b"\\\\", b"\\")
                disp_scr = normalize_script_display(scr_raw)
                if "路人_龙五" in disp_scr and cfg_flags.get("CFG_NhiemVuLongNgu", 1) == 0:
                    skipped.append(f"{source_label}:L{lineno} Long Ngu disabled by CFG_NhiemVuLongNgu=0")
                    continue
                if tpl_id not in npc_templates:
                    skipped.append(f"{source_label}:L{lineno} tplId={tpl_id} NpcTemplate Error")
                    continue
                def_name, def_kind = npc_templates[tpl_id]
                name = translate_npc_name(name_raw, def_name, rep_b, rep_s)
                sem = inspect_lua_semantics(resolve_script_on_disk(server1_dir, scr_raw))
                spawns.append(
                    NpcEntity(
                        source=source_label,
                        template_id=tpl_id,
                        display_name=name,
                        kind=3 if disp_scr else def_kind,
                        mps_x=tx * 32,
                        mps_y=ty * 32,
                        script_path=disp_scr,
                        level_gates=sem["level_gates"],
                        shop_ids=sem["shop_ids"],
                        tasks_referenced=sem["tasks_referenced"],
                        uses_input_number=sem["uses_input_number"],
                    )
                )

    # 2. wlls_autoexec.lua WLLS_NPC_CITY spawns (for 7 major cities)
    wlls_fp = server1_dir / "script" / "missions" / "leaguematch" / "wlls_autoexec.lua"
    if wlls_fp.is_file():
        for raw in wlls_fp.read_bytes().splitlines():
            code_b, _ = read_jx_file._split_lua_comment_bytes(raw)
            code_s = code_b.decode("latin1").strip()
            mh = re.match(r"\{\s*(\d+)\s*,\s*\"([^\"]+)\"\s*,\s*\"([^\"]+)\"\s*,\s*\{(.+)\}\s*\}", code_s)
            if not mh:
                continue
            tpl_id = int(mh.group(1))
            name_raw = mh.group(2).encode("latin1")
            scr_raw = mh.group(3).encode("latin1").replace(b"\\\\", b"\\")
            for pm in re.finditer(r"\{\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\}", mh.group(4)):
                if int(pm.group(1)) == map_id and tpl_id in npc_templates:
                    tx, ty = int(pm.group(2)), int(pm.group(3))
                    def_name, _ = npc_templates[tpl_id]
                    name = translate_npc_name(name_raw, def_name, rep_b, rep_s)
                    disp_scr = normalize_script_display(scr_raw)
                    sem = inspect_lua_semantics(resolve_script_on_disk(server1_dir, scr_raw))
                    spawns.append(
                        NpcEntity(
                            source="wlls_autoexec",
                            template_id=tpl_id,
                            display_name=name,
                            kind=3,
                            mps_x=tx * 32,
                            mps_y=ty * 32,
                            script_path=disp_scr,
                            level_gates=sem["level_gates"],
                            shop_ids=sem["shop_ids"],
                            tasks_referenced=sem["tasks_referenced"],
                            uses_input_number=sem["uses_input_number"],
                        )
                    )

    return spawns, skipped


def load_map_ground_truth(map_id: int, server1_dir: Path | None = None, client_dir: Path | None = None) -> MapGroundTruth:
    srv_dir = server1_dir or resolve_server1_dir()
    cli_dir = client_dir if client_dir is not None else resolve_headless_client_dir()
    minimal_maps = load_minimal_test_maps()

    # 1. Read maplist.ini
    ml_bytes = (srv_dir / "settings" / "maplist.ini").read_bytes()
    sub_path_bytes: bytes | None = None
    map_name = f"Map_{map_id}"
    for raw in ml_bytes.splitlines():
        if raw.startswith(f"{map_id}=".encode("ascii")):
            sub_path_bytes = raw.split(b"=", 1)[1].strip()
        elif raw.startswith(f"{map_id}_name=".encode("ascii")):
            map_name = decode_smart(raw.split(b"=", 1)[1])
    if sub_path_bytes is None:
        raise KeyError(f"MapId {map_id} not found in {srv_dir / 'settings' / 'maplist.ini'}")

    sub_path_disp = normalize_script_display(sub_path_bytes).lstrip("\\")

    # 2. Open server maps.pak & client maps.pak
    srv_paks = XPackGroup.from_root(srv_dir, fallback_subdirs=("pak",))
    cli_paks = XPackGroup.from_root(cli_dir, fallback_subdirs=("data", "data1")) if cli_dir else None

    wor_bytes = srv_paks.read_entry(b"\\maps\\" + sub_path_bytes + b".wor")
    if wor_bytes is None:
        raise FileNotFoundError(f"Could not read \\maps\\{sub_path_disp}.wor from server maps.pak")
    m_rect = re.search(rb"rect\s*=\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", wor_bytes, re.I)
    if not m_rect:
        raise ValueError(f"Invalid .wor rect in Map {map_id}")
    left, top, right, bottom = (int(m_rect.group(i)) for i in range(1, 5))

    rep_b, rep_s = load_replace_names(srv_dir)
    npc_templates = load_npc_templates(srv_dir, rep_b, rep_s)
    obj_templates = load_obj_templates(srv_dir)
    trap_script_lookup = scan_trap_scripts(srv_dir)

    srv_obstacles: dict[tuple[int, int], list[int]] = {}
    cli_obstacles: dict[tuple[int, int], list[int]] = {}
    cell_traps: dict[tuple[int, int], int] = {}
    trap_cells_by_id: dict[int, list[tuple[int, int]]] = {}
    dialog_npcs: list[NpcEntity] = []
    scripted_monsters: list[NpcEntity] = []
    monster_summary: dict[str, int] = {}
    objects: list[ObjEntity] = []

    for ry in range(top, bottom + 1):
        for rx in range(left, right + 1):
            suffix_s = f"\\v_{ry:03d}\\{rx:03d}_region_s.dat".encode("ascii")
            rs_bytes = srv_paks.read_entry(b"\\maps\\" + sub_path_bytes + suffix_s)
            if rs_bytes is not None:
                obs, r_traps, r_npcs, r_objs = parse_region_s_bytes(rs_bytes, rx, ry)
                srv_obstacles[(rx, ry)] = obs
                for cx, cy, span, tid in r_traps:
                    ty = ry * 32 + cy
                    for dx in range(span):
                        tx = rx * 16 + cx + dx
                        cell_traps[(tx, ty)] = tid
                        trap_cells_by_id.setdefault(tid, []).append((tx, ty))

                for tpl_id, px, py, raw_name, kind_override, scr_bytes in r_npcs:
                    if tpl_id not in npc_templates:
                        continue
                    def_name, def_kind = npc_templates[tpl_id]
                    eff_kind = kind_override if kind_override in (0, 3) else def_kind
                    name = translate_npc_name(raw_name, def_name, rep_b, rep_s)
                    disp_scr = normalize_script_display(scr_bytes)
                    if eff_kind == 3:
                        sem = inspect_lua_semantics(resolve_script_on_disk(srv_dir, scr_bytes))
                        dialog_npcs.append(
                            NpcEntity(
                                source="Region_S",
                                template_id=tpl_id,
                                display_name=name,
                                kind=3,
                                mps_x=px,
                                mps_y=py,
                                script_path=disp_scr,
                                level_gates=sem["level_gates"],
                                shop_ids=sem["shop_ids"],
                                tasks_referenced=sem["tasks_referenced"],
                                uses_input_number=sem["uses_input_number"],
                            )
                        )
                    else:
                        monster_summary[name] = monster_summary.get(name, 0) + 1
                        if disp_scr:
                            scripted_monsters.append(
                                NpcEntity(
                                    source="Region_S",
                                    template_id=tpl_id,
                                    display_name=name,
                                    kind=eff_kind,
                                    mps_x=px,
                                    mps_y=py,
                                    script_path=disp_scr,
                                )
                            )

                for data_id, mx, my, scr_bytes in r_objs:
                    def_oname, def_oscr = obj_templates.get(data_id, (f"Obj_{data_id}", ""))
                    disp_scr = normalize_script_display(scr_bytes) or def_oscr
                    sem = inspect_lua_semantics(resolve_script_on_disk(srv_dir, scr_bytes if scr_bytes else disp_scr))
                    objects.append(
                        ObjEntity(
                            data_id=data_id,
                            display_name=def_oname,
                            mps_x=mx,
                            mps_y=my,
                            script_path=disp_scr,
                            opens_box=sem["opens_box"],
                        )
                    )

            if cli_paks is not None:
                suffix_c = f"\\v_{ry:03d}\\{rx:03d}_region_c.dat".encode("ascii")
                rc_bytes = cli_paks.read_entry(b"\\maps\\" + sub_path_bytes + suffix_c)
                if rc_bytes is not None:
                    c_obs, _, _, _ = parse_region_s_bytes(rc_bytes, rx, ry)
                    cli_obstacles[(rx, ry)] = c_obs

    global_npcs, skipped_spawners = scan_global_npc_spawns(srv_dir, map_id, npc_templates, rep_b, rep_s)
    dialog_npcs.extend(global_npcs)

    traps: list[TrapEntity] = []
    for tid, cells in sorted(trap_cells_by_id.items()):
        cells_sorted = sorted(set(cells))
        disp_scr, lua_fp = trap_script_lookup.get(tid, ("", None))
        sem = inspect_lua_semantics(lua_fp)
        tmap = sem["target_map_id"]
        in_min = True if (tmap is None or not minimal_maps) else (tmap in minimal_maps)
        traps.append(
            TrapEntity(
                trap_id=tid,
                cells=cells_sorted,
                script_path=disp_scr,
                is_bidirectional_gate=sem["is_bidirectional_gate"],
                target_map_id=tmap,
                min_level=sem["min_level"],
                in_minimal_worldset=in_min,
            )
        )

    return MapGroundTruth(
        map_id=map_id,
        map_name=map_name,
        sub_path_display=sub_path_disp,
        rect=(left, top, right, bottom),
        in_minimal_worldset=(not minimal_maps) or (map_id in minimal_maps),
        srv_obstacles=srv_obstacles,
        cli_obstacles=cli_obstacles,
        cell_traps=cell_traps,
        dialog_npcs=dialog_npcs,
        scripted_monsters=scripted_monsters,
        monster_summary=monster_summary,
        objects=objects,
        traps=traps,
        skipped_spawners=skipped_spawners,
    )


# ---------------------------------------------------------------------------
# Geometric Greedy Disk Set Cover + 2-opt TSP Planner
# ---------------------------------------------------------------------------

def find_nearest_clean_tile(gt: MapGroundTruth, target_tx: int, target_ty: int, min_dist: int = 1, max_dist: int = 14) -> tuple[int, int] | None:
    best: tuple[float, int, int] | None = None
    for r in range(min_dist, max_dist + 1):
        for dx in range(-r, r + 1):
            for dy in range(-r, r + 1):
                d = math.hypot(dx, dy)
                if d < min_dist or d > r + 0.5:
                    continue
                cx, cy = target_tx + dx, target_ty + dy
                if gt.is_roomy_clean_tile(cx, cy):
                    cand = (d, cx, cy)
                    if best is None or cand < best:
                        best = cand
        if best is not None:
            return best[1], best[2]
    # Fallback to any clean tile
    for r in range(min_dist, max_dist + 1):
        for dx in range(-r, r + 1):
            for dy in range(-r, r + 1):
                cx, cy = target_tx + dx, target_ty + dy
                if gt.is_clean_tile(cx, cy):
                    return cx, cy
    return None


def solve_npc_disk_cover(
    gt: MapGroundTruth,
    radius_tiles: int = 26,
    exclude_names: set[str] | None = None,
) -> list[dict[str, Any]]:
    """Greedy Disk Set Cover + 2-opt TSP ordering over exact Mps distance matching WireNpcDialogCrawler."""
    excl = {n.strip().lower() for n in (exclude_names or set())}
    targets = [
        n for n in gt.dialog_npcs
        if n.display_name.strip().lower() not in excl and n.script_path
    ]
    if not targets:
        return []

    # WireNpcDialogCrawler measures Distance(npc.MpsX, npc.MpsY, originX, originY) <= radiusTiles * 32.0
    # Leave a 1.5-tile safety margin so minor pathing offsets after gm_teleport never drop boundary NPCs.
    safe_mps_radius = max(4.0, radius_tiles - 1.5) * 32.0

    # Generate candidate clean tiles around every target NPC (step 2 tiles + exact nearest clean tile)
    cand_tiles: set[tuple[int, int]] = set()
    for n in targets:
        ntx, nty = n.tile_x, n.tile_y
        near = find_nearest_clean_tile(gt, ntx, nty, min_dist=1, max_dist=6)
        if near:
            cand_tiles.add(near)
        for dx in range(-radius_tiles + 2, radius_tiles - 1, 3):
            for dy in range(-radius_tiles + 2, radius_tiles - 1, 3):
                if dx * dx + dy * dy <= (radius_tiles - 1) ** 2:
                    cx, cy = ntx + dx, nty + dy
                    if gt.is_roomy_clean_tile(cx, cy):
                        cand_tiles.add((cx, cy))

    uncovered = set(range(len(targets)))
    chosen: list[tuple[int, int, list[int]]] = []

    while uncovered:
        best_tile: tuple[int, int] | None = None
        best_cov: list[int] = []
        best_score = (-1, 0.0)

        for cx, cy in cand_tiles:
            omx, omy = cx * 32 + 16, cy * 32 + 16
            crx, cry = cx // 16, cy // 32
            cov = [
                i for i in uncovered
                if abs((targets[i].tile_x // 16) - crx) <= 1
                and abs((targets[i].tile_y // 32) - cry) <= 1
                and math.hypot(targets[i].mps_x - omx, targets[i].mps_y - omy) <= safe_mps_radius
            ]
            if not cov:
                continue
            avg_dist = sum(math.hypot(targets[i].mps_x - omx, targets[i].mps_y - omy) for i in cov) / len(cov)
            score = (len(cov), -avg_dist)
            if score > best_score:
                best_score = score
                best_tile = (cx, cy)
                best_cov = cov

        if best_tile is None:
            # Isolated NPC fallback
            idx = next(iter(uncovered))
            ntx, nty = targets[idx].tile_x, targets[idx].tile_y
            fallback = find_nearest_clean_tile(gt, ntx, nty, min_dist=0, max_dist=20) or (ntx, nty)
            chosen.append((fallback[0], fallback[1], [idx]))
            uncovered.remove(idx)
        else:
            chosen.append((best_tile[0], best_tile[1], best_cov))
            uncovered.difference_update(best_cov)

    # Order centers via Nearest-Neighbor + 2-opt TSP
    if len(chosen) > 2:
        order = [0]
        rem = set(range(1, len(chosen)))
        while rem:
            last = order[-1]
            nxt = min(rem, key=lambda j: math.hypot(chosen[j][0] - chosen[last][0], chosen[j][1] - chosen[last][1]))
            order.append(nxt)
            rem.remove(nxt)
        improved = True
        while improved:
            improved = False
            for i in range(1, len(order) - 1):
                for k in range(i + 1, len(order)):
                    a, b = order[i - 1], order[i]
                    c = order[k]
                    d = order[k + 1] if k + 1 < len(order) else None
                    cur = math.hypot(chosen[a][0] - chosen[b][0], chosen[a][1] - chosen[b][1])
                    swp = math.hypot(chosen[a][0] - chosen[c][0], chosen[a][1] - chosen[c][1])
                    if d is not None:
                        cur += math.hypot(chosen[c][0] - chosen[d][0], chosen[c][1] - chosen[d][1])
                        swp += math.hypot(chosen[b][0] - chosen[d][0], chosen[b][1] - chosen[d][1])
                    if swp + 1e-6 < cur:
                        order[i : k + 1] = reversed(order[i : k + 1])
                        improved = True
        chosen = [chosen[i] for i in order]

    result: list[dict[str, Any]] = []
    for rank, (cx, cy, idxs) in enumerate(chosen, 1):
        result.append(
            {
                "hubIndex": rank,
                "teleportTileX": cx,
                "teleportTileY": cy,
                "radiusTiles": radius_tiles,
                "coveredCount": len(idxs),
                "coveredNpcs": [
                    {
                        "name": targets[i].display_name,
                        "tileX": targets[i].tile_x,
                        "tileY": targets[i].tile_y,
                        "source": targets[i].source,
                    }
                    for i in idxs
                ],
            }
        )
    return result


# ---------------------------------------------------------------------------
# Static Scenario Simulator & Coverage Auditor
# ---------------------------------------------------------------------------

def audit_scenario(scenario_path: Path, strict: bool = False) -> dict[str, Any]:
    sc = json.loads(scenario_path.read_text(encoding="utf-8"))
    target_map_id = int(sc["mapId"])
    gt = load_map_ground_truth(target_map_id)

    steps: list[dict[str, Any]] = sc.get("steps", [])
    terrain_errors: list[str] = []
    sequence_errors: list[str] = []

    # State machine
    cur_map = int(sc.get("nativePlaceId", target_map_id))
    # Default spawn center approx if starting on map 53
    cur_tx, cur_ty = (1620, 3178) if cur_map == 53 else (1577, 3238)
    cur_level = 1
    cur_fight = 0

    covered_npc_indices: set[int] = set()
    npc_levels_seen: dict[int, set[str]] = {i: set() for i in range(len(gt.dialog_npcs))}
    covered_obj_indices: set[int] = set()
    covered_trap_ids: set[int] = set()
    trap_fight_edges: dict[int, set[tuple[int, int]]] = {t.trap_id: set() for t in gt.traps}
    trap_level_edges: dict[int, set[str]] = {t.trap_id: set() for t in gt.traps}
    covered_dummy_scripts: set[str] = set()
    saw_shop_buy = False
    saw_storage_action = False

    if not steps or steps[0].get("type", "").lower() != "login":
        sequence_errors.append("steps[0] must have type='login'.")

    for idx, step in enumerate(steps, 1):
        stype = step.get("type", "").strip().lower()
        sname = step.get("name", f"Step_{idx}")
        smap = int(step.get("mapId") or cur_map)

        if stype in ("gm_teleport", "resync_if_drifted"):
            tx = step.get("tileX")
            ty = step.get("tileY")
            if tx is not None and ty is not None:
                tx, ty = int(tx), int(ty)
                if smap == target_map_id and stype == "gm_teleport":
                    srv_o = gt.get_srv_obs(tx, ty)
                    cli_o = gt.get_cli_obs(tx, ty)
                    tid = gt.get_trap_id(tx, ty)
                    if srv_o != 0 or cli_o != 0 or tid != 0:
                        terrain_errors.append(
                            f"Step #{idx} '{sname}' gm_teleport ({tx},{ty}) is NOT clean: srvObs={srv_o}, cliObs={cli_o}, trapId={tid}"
                        )
                cur_map = smap
                cur_tx, cur_ty = tx, ty

        elif stype == "gm_set_level":
            lv = int(step.get("level") or step.get("value") or cur_level)
            if lv < cur_level:
                sequence_errors.append(
                    f"Step #{idx} '{sname}' attempts to lower level ({cur_level} -> {lv}), which gm_set_level cannot do."
                )
            cur_level = max(cur_level, lv)

        elif stype == "gm_set_fight_mode":
            cur_fight = 1 if int(step.get("value", 1)) != 0 else 0

        elif stype == "walk_to":
            if step.get("tileX") is not None and step.get("tileY") is not None:
                cur_tx, cur_ty = int(step["tileX"]), int(step["tileY"])
            elif step.get("mpsX") is not None and step.get("mpsY") is not None:
                cur_tx, cur_ty = int(step["mpsX"]) // 32, int(step["mpsY"]) // 32

        elif stype == "interact_npc":
            npc_name = (step.get("npcName") or "").strip().lower()
            tx = int(step["tileX"]) if step.get("tileX") is not None else cur_tx
            ty = int(step["tileY"]) if step.get("tileY") is not None else cur_ty
            max_r = int(step.get("maxRadiusTiles") or 14)
            if smap == target_map_id:
                matched = [
                    i for i, n in enumerate(gt.dialog_npcs)
                    if (npc_name in n.display_name.lower() or n.display_name.lower() in npc_name)
                    and math.hypot(n.tile_x - tx, n.tile_y - ty) <= max_r
                ]
                if not matched:
                    # Fallback by name anywhere on map if tile wasn't specified
                    if step.get("tileX") is None:
                        matched = [
                            i for i, n in enumerate(gt.dialog_npcs)
                            if npc_name in n.display_name.lower() or n.display_name.lower() in npc_name
                        ]
                for i in matched:
                    covered_npc_indices.add(i)
                    n = gt.dialog_npcs[i]
                    for gate_lv in n.level_gates:
                        npc_levels_seen[i].add("blocked" if cur_level < gate_lv else "unlocked")
                    cur_tx, cur_ty = n.tile_x, n.tile_y

        elif stype == "crawl_region_npcs":
            if cur_map == target_map_id:
                r_tiles = int(step.get("maxRadiusTiles") or 22)
                max_mps = max(4, r_tiles) * 32.0
                excl = {x.strip().lower() for x in (step.get("excludeNpcNames") or [])}
                omx, omy = cur_tx * 32 + 16, cur_ty * 32 + 16
                crx, cry = cur_tx // 16, cur_ty // 32
                last_pos: tuple[int, int] | None = None
                for i, n in enumerate(gt.dialog_npcs):
                    if n.display_name.strip().lower() in excl:
                        continue
                    # JX1 Engine AOI syncs only the 3x3 Region neighborhood around the player's origin region
                    if (
                        abs((n.tile_x // 16) - crx) <= 1
                        and abs((n.tile_y // 32) - cry) <= 1
                        and math.hypot(n.mps_x - omx, n.mps_y - omy) <= max_mps
                    ):
                        covered_npc_indices.add(i)
                        for gate_lv in n.level_gates:
                            npc_levels_seen[i].add("blocked" if cur_level < gate_lv else "unlocked")
                        last_pos = (n.tile_x, n.tile_y)
                if last_pos is not None:
                    cur_tx, cur_ty = last_pos

        elif stype == "click_object":
            tx = int(step["tileX"]) if step.get("tileX") is not None else cur_tx
            ty = int(step["tileY"]) if step.get("tileY") is not None else cur_ty
            max_r = int(step.get("maxRadiusTiles") or 12)
            if smap == target_map_id:
                for i, o in enumerate(gt.objects):
                    if math.hypot(o.tile_x - tx, o.tile_y - ty) <= max_r:
                        if step.get("dataId") is None or int(step["dataId"]) == o.data_id:
                            covered_obj_indices.add(i)
                            cur_tx, cur_ty = o.tile_x, o.tile_y

        elif stype == "step_trap":
            tid = int(step["trapId"]) if step.get("trapId") is not None else 0
            if smap == target_map_id and tid != 0:
                covered_trap_ids.add(tid)
                t_ent = next((t for t in gt.traps if t.trap_id == tid), None)
                if t_ent is not None:
                    cur_tx, cur_ty = t_ent.sample_tile
                    if t_ent.is_bidirectional_gate:
                        next_f = int(step["expectedFightMode"]) if step.get("expectedFightMode") is not None else (1 - cur_fight)
                        trap_fight_edges[tid].add((cur_fight, next_f))
                        cur_fight = next_f
                    if t_ent.min_level is not None:
                        trap_level_edges[tid].add("blocked" if cur_level < t_ent.min_level else "unlocked")
            if step.get("expectedMapId") is not None:
                cur_map = int(step["expectedMapId"])
            if step.get("expectedFightMode") is not None:
                cur_fight = int(step["expectedFightMode"])

        elif stype == "attack_npc":
            npc_name = (step.get("npcName") or "").strip().lower()
            tpl_id = step.get("templateId")
            tx = int(step["tileX"]) if step.get("tileX") is not None else cur_tx
            ty = int(step["tileY"]) if step.get("tileY") is not None else cur_ty
            max_r = int(step.get("maxRadiusTiles") or 20)
            if smap == target_map_id:
                for m in gt.scripted_monsters:
                    name_ok = bool(npc_name) and (npc_name in m.display_name.lower() or m.display_name.lower() in npc_name)
                    tpl_ok = tpl_id is not None and int(tpl_id) == m.template_id
                    if (name_ok or tpl_ok) and math.hypot(m.tile_x - tx, m.tile_y - ty) <= max_r:
                        covered_dummy_scripts.add(m.script_path)
                        cur_tx, cur_ty = m.tile_x, m.tile_y

        elif stype == "buy_shop_item":
            saw_shop_buy = True

        elif stype in ("box_operate", "store_money"):
            saw_storage_action = True

    # Compute gaps
    scripted_dialog_indices = [i for i, n in enumerate(gt.dialog_npcs) if n.script_path]
    uncovered_npcs = [
        {
            "name": gt.dialog_npcs[i].display_name,
            "source": gt.dialog_npcs[i].source,
            "tileX": gt.dialog_npcs[i].tile_x,
            "tileY": gt.dialog_npcs[i].tile_y,
            "script": gt.dialog_npcs[i].script_path,
        }
        for i in scripted_dialog_indices
        if i not in covered_npc_indices
    ]

    scripted_obj_indices = [i for i, o in enumerate(gt.objects) if o.script_path]
    uncovered_objs = [
        {
            "name": gt.objects[i].display_name,
            "dataId": gt.objects[i].data_id,
            "tileX": gt.objects[i].tile_x,
            "tileY": gt.objects[i].tile_y,
            "script": gt.objects[i].script_path,
        }
        for i in scripted_obj_indices
        if i not in covered_obj_indices
    ]

    uncovered_traps = [
        {
            "trapId": t.trap_id,
            "sampleTile": t.sample_tile,
            "script": t.script_path,
            "targetMapId": t.target_map_id,
        }
        for t in gt.traps
        if t.trap_id not in covered_trap_ids
    ]

    all_dummy_scripts = sorted({m.script_path for m in gt.scripted_monsters if m.script_path})
    uncovered_dummy_scripts = [s for s in all_dummy_scripts if s not in covered_dummy_scripts]

    branch_gaps: list[str] = []
    # Check bidirectional gate fight transitions (at least one 0->1 and one 1->0 across map gates, or per-gate in strict)
    bi_traps = [t for t in gt.traps if t.is_bidirectional_gate]
    if bi_traps:
        all_edges = set().union(*(trap_fight_edges[t.trap_id] for t in bi_traps))
        if (0, 1) not in all_edges:
            branch_gaps.append("Fight-toggle gates missing outbound transition (FightMode 0 -> 1).")
        if (1, 0) not in all_edges:
            branch_gaps.append("Fight-toggle gates missing inbound transition (FightMode 1 -> 0).")

    # Check level-gated portals (both blocked < min_level and unlocked >= min_level when target map is in minimal worldset)
    for t in gt.traps:
        if t.min_level is not None:
            seen = trap_level_edges[t.trap_id]
            if "blocked" not in seen:
                branch_gaps.append(f"Portal trap {t.trap_id} ({t.script_path}) missing low-level block check (Lv < {t.min_level}).")
            if t.in_minimal_worldset and "unlocked" not in seen:
                branch_gaps.append(f"Portal trap {t.trap_id} ({t.script_path} -> Map {t.target_map_id}) missing unlocked transition (Lv >= {t.min_level}).")

    # Check level-gated NPCs (Xa phu, Thuyen phu, Da Tau, Vo Su, etc.)
    for i in scripted_dialog_indices:
        n = gt.dialog_npcs[i]
        if n.level_gates:
            seen = npc_levels_seen[i]
            if "unlocked" not in seen:
                branch_gaps.append(
                    f"Level-gated NPC '{n.display_name}' ({n.tile_x},{n.tile_y}) only tested while blocked or skipped after gm_set_level (requires Lv >= {max(n.level_gates)})."
                )

    has_shops = any(n.shop_ids for n in gt.dialog_npcs)
    has_boxes = any(o.opens_box for o in gt.objects)
    if has_shops and not saw_shop_buy:
        branch_gaps.append("Map has vendor shops (Sale), but scenario has 0 'buy_shop_item' wire actions.")
    if has_boxes and not saw_storage_action:
        branch_gaps.append("Map has storage boxes (OpenBox), but scenario has 0 'store_money'/'box_operate' wire actions.")

    passed = (
        not terrain_errors
        and not sequence_errors
        and not uncovered_npcs
        and not uncovered_objs
        and not uncovered_traps
        and (not strict or (not uncovered_dummy_scripts and not branch_gaps))
    )

    return {
        "scenario": sc.get("name"),
        "mapId": target_map_id,
        "mapName": gt.map_name,
        "strict": strict,
        "passed": passed,
        "counts": {
            "steps": len(steps),
            "dialogNpcsTotal": len(scripted_dialog_indices),
            "dialogNpcsCovered": len(scripted_dialog_indices) - len(uncovered_npcs),
            "objectsTotal": len(scripted_obj_indices),
            "objectsCovered": len(scripted_obj_indices) - len(uncovered_objs),
            "trapsTotal": len(gt.traps),
            "trapsCovered": len(gt.traps) - len(uncovered_traps),
            "scriptedMonsterGroupsTotal": len(all_dummy_scripts),
            "scriptedMonsterGroupsCovered": len(all_dummy_scripts) - len(uncovered_dummy_scripts),
        },
        "terrainErrors": terrain_errors,
        "sequenceErrors": sequence_errors,
        "uncoveredDialogNpcs": uncovered_npcs,
        "uncoveredObjects": uncovered_objs,
        "uncoveredTraps": uncovered_traps,
        "uncoveredScriptedMonsterGroups": uncovered_dummy_scripts,
        "stateAndEconomyBranchGaps": branch_gaps,
    }


# ---------------------------------------------------------------------------
# CLI Entrypoint
# ---------------------------------------------------------------------------

def cmd_inventory(args: argparse.Namespace) -> int:
    gt = load_map_ground_truth(args.map)
    payload = {
        "mapId": gt.map_id,
        "mapName": gt.map_name,
        "subPath": gt.sub_path_display,
        "regionRect": {"left": gt.rect[0], "top": gt.rect[1], "right": gt.rect[2], "bottom": gt.rect[3]},
        "inMinimalWorldset": gt.in_minimal_worldset,
        "dialogNpcs": [
            {
                "name": n.display_name,
                "source": n.source,
                "templateId": n.template_id,
                "tileX": n.tile_x,
                "tileY": n.tile_y,
                "script": n.script_path,
                "levelGates": n.level_gates,
                "shopIds": n.shop_ids,
                "tasks": n.tasks_referenced,
                "usesInputNumber": n.uses_input_number,
            }
            for n in gt.dialog_npcs
        ],
        "objects": [
            {
                "name": o.display_name,
                "dataId": o.data_id,
                "tileX": o.tile_x,
                "tileY": o.tile_y,
                "script": o.script_path,
                "opensBox": o.opens_box,
            }
            for o in gt.objects
        ],
        "traps": [
            {
                "trapId": t.trap_id,
                "cellCount": len(t.cells),
                "sampleTile": t.sample_tile,
                "cleanTeleportNear": find_nearest_clean_tile(gt, t.sample_tile[0], t.sample_tile[1], 2, 12),
                "script": t.script_path,
                "bidirectionalGate": t.is_bidirectional_gate,
                "targetMapId": t.target_map_id,
                "minLevel": t.min_level,
                "targetInMinimalWorldset": t.in_minimal_worldset,
            }
            for t in gt.traps
        ],
        "scriptedMonsterGroups": [
            {
                "script": scr,
                "count": sum(1 for m in gt.scripted_monsters if m.script_path == scr),
                "sampleName": next(m.display_name for m in gt.scripted_monsters if m.script_path == scr),
                "sampleTile": next((m.tile_x, m.tile_y) for m in gt.scripted_monsters if m.script_path == scr),
            }
            for scr in sorted({m.script_path for m in gt.scripted_monsters})
        ],
        "monsterSummary": gt.monster_summary,
        "skippedSpawners": gt.skipped_spawners,
    }
    if args.json:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        return 0

    print(f"=== GROUND TRUTH INVENTORY: Map {gt.map_id} ({gt.map_name}) [{gt.sub_path_display}] ===")
    print(f"Regions: X={gt.rect[0]}..{gt.rect[2]}, Y={gt.rect[1]}..{gt.rect[3]} | In MINIMAL_TEST_MAPS: {gt.in_minimal_worldset}")
    print(f"\n[Dialog NPCs: {len(gt.dialog_npcs)}]")
    for n in gt.dialog_npcs:
        extras = []
        if n.level_gates:
            extras.append(f"LvGate={n.level_gates}")
        if n.shop_ids:
            extras.append(f"Shop={n.shop_ids}")
        if n.tasks_referenced:
            extras.append(f"Tasks={n.tasks_referenced[:6]}")
        if n.uses_input_number:
            extras.append("InputNumber")
        ext_s = (" | " + ", ".join(extras)) if extras else ""
        print(f"  - ({n.tile_x},{n.tile_y}) [{n.source}] '{n.display_name}' (tpl={n.template_id}) -> {n.script_path}{ext_s}")

    print(f"\n[Map Objects: {len(gt.objects)}]")
    for o in gt.objects:
        near = find_nearest_clean_tile(gt, o.tile_x, o.tile_y, 1, 8)
        print(f"  - ({o.tile_x},{o.tile_y}) '{o.display_name}' (dataId={o.data_id}, cleanTp={near}) -> {o.script_path}")

    print(f"\n[Traps / Portals: {len(gt.traps)}]")
    for t in payload["traps"]:
        print(
            f"  - trapId={t['trapId']} sample={t['sampleTile']} cleanTp={t['cleanTeleportNear']} "
            f"biGate={t['bidirectionalGate']} minLv={t['minLevel']} destMap={t['targetMapId']} "
            f"(inMinWorld={t['targetInMinimalWorldset']}) -> {t['script']}"
        )

    if payload["scriptedMonsterGroups"]:
        print(f"\n[Scripted Monsters / Dummies: {len(payload['scriptedMonsterGroups'])} groups]")
        for g in payload["scriptedMonsterGroups"]:
            near = find_nearest_clean_tile(gt, g["sampleTile"][0], g["sampleTile"][1], 1, 8)
            print(f"  - '{g['sampleName']}' x{g['count']} sample={g['sampleTile']} cleanTp={near} -> {g['script']}")

    if gt.monster_summary:
        print(f"\n[Ambient Monsters: {sum(gt.monster_summary.values())} total] {gt.monster_summary}")
    if gt.skipped_spawners:
        print(f"\n[Skipped Non-Runtime Spawners: {len(gt.skipped_spawners)}]")
        for s in gt.skipped_spawners:
            print(f"  - {s}")
    return 0


def cmd_plan_cover(args: argparse.Namespace) -> int:
    gt = load_map_ground_truth(args.map)
    excl = {x.strip() for x in (args.exclude or "").split(",") if x.strip()}
    hubs = solve_npc_disk_cover(gt, radius_tiles=args.radius, exclude_names=excl)
    if args.json:
        print(json.dumps({"mapId": gt.map_id, "radiusTiles": args.radius, "hubs": hubs}, ensure_ascii=False, indent=2))
        return 0

    print(f"=== OPTIMAL GREEDY SET-COVER + 2-OPT TSP FOR MAP {gt.map_id} ({gt.map_name}) [R={args.radius}] ===")
    for h in hubs:
        names = ", ".join(f"{c['name']}({c['tileX']},{c['tileY']})" for c in h["coveredNpcs"])
        print(f"  Hub #{h['hubIndex']}: gm_teleport ({h['teleportTileX']}, {h['teleportTileY']}) + crawl_region_npcs(r={h['radiusTiles']}) -> covers {h['coveredCount']} NPCs: {names}")
    return 0


def cmd_audit(args: argparse.Namespace) -> int:
    report = audit_scenario(Path(args.scenario), strict=args.strict)
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        c = report["counts"]
        status = "PASS" if report["passed"] else "FAIL"
        print(f"=== SCENARIO COVERAGE AUDIT [{status}] : {report['scenario']} (Map {report['mapId']} - {report['mapName']}) ===")
        print(
            f"Steps: {c['steps']} | DialogNPCs: {c['dialogNpcsCovered']}/{c['dialogNpcsTotal']} | "
            f"Objects: {c['objectsCovered']}/{c['objectsTotal']} | Traps: {c['trapsCovered']}/{c['trapsTotal']} | "
            f"ScriptedMonsters: {c['scriptedMonsterGroupsCovered']}/{c['scriptedMonsterGroupsTotal']}"
        )
        for label, key in (
            ("Terrain Errors", "terrainErrors"),
            ("Sequence Errors", "sequenceErrors"),
            ("Uncovered Dialog NPCs", "uncoveredDialogNpcs"),
            ("Uncovered Map Objects", "uncoveredObjects"),
            ("Uncovered Traps/Portals", "uncoveredTraps"),
            ("Uncovered Scripted Monster Groups", "uncoveredScriptedMonsterGroups"),
            ("State & Economy Branch Gaps", "stateAndEconomyBranchGaps"),
        ):
            items = report[key]
            if items:
                print(f"\n[!] {label} ({len(items)}):")
                for it in items:
                    print(f"    - {it}")
    return 0 if report["passed"] else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="Ground-Truth Map Extractor, Set-Cover Planner & Scenario Coverage Auditor.")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p_inv = sub.add_parser("inventory", help="Dump 100%% Ground-Truth entities and gates for a map.")
    p_inv.add_argument("--map", type=int, required=True, help="Map ID (e.g. 53, 78).")
    p_inv.add_argument("--json", action="store_true", help="Output JSON.")
    p_inv.set_defaults(func=cmd_inventory)

    p_cov = sub.add_parser("plan-cover", help="Compute Greedy Disk Set Cover + TSP crawl hubs.")
    p_cov.add_argument("--map", type=int, required=True, help="Map ID.")
    p_cov.add_argument("--radius", type=int, default=26, help="Crawl radius in tiles (default: 26).")
    p_cov.add_argument("--exclude", type=str, default="Xa phu,Thuyền Phu,Ngư dân", help="Comma-separated NPC names to exclude from crawl.")
    p_cov.add_argument("--json", action="store_true", help="Output JSON.")
    p_cov.set_defaults(func=cmd_plan_cover)

    p_aud = sub.add_parser("audit", help="Audit a .scenario.json file against Legacy Ground Truth.")
    p_aud.add_argument("--scenario", type=str, required=True, help="Path to .scenario.json.")
    p_aud.add_argument("--strict", action="store_true", help="Fail on scripted monster & bidirectional/economy branch gaps too.")
    p_aud.add_argument("--json", action="store_true", help="Output JSON.")
    p_aud.set_defaults(func=cmd_audit)

    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
