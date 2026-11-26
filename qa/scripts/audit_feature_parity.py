#!/usr/bin/env python3
"""Static Ground-Truth Feature & Script Parity Auditor (`audit_feature_parity.py`).

Gate 1 deterministic completeness verifier for porting/fixing Legacy Lua scripts & settings
into `pack-tieudaotu` (Tier 1 JSON + C# `JXReborn.Scripts`) and `server`.

Checks performed (< 1s, zero LLM self-grading):
  1. Decodes target `.lua` script(s) from `legacy-server/bin/server1/` (handling mixed TCVN3/GB18030/UTF-8
     and GBK-encoded Linux paths via `read_jx_file.py`) and recursively resolves `Include(...)` headers
     for symbolic `TSK_*` / numeric constant definitions.
  2. Extracts Ground-Truth Behavioral Fingerprints:
     - Persistent & temporary `TaskID`s (`GetTask`, `SetTask`, `GetTaskTemp`, `SetTaskTemp`).
     - Byte/Bit task operations (`GetByte`, `SetByte`, `GetBit`, `SetBit`).
     - Shop openings (`Sale(shopId, ...)`).
     - Revival bindings (`SetRevPos(revPosId)` / `SetRevPos(mapId, revPosId)`).
     - Skill mutations (`AddMagic(skillId, ...)`).
     - Item grants/checks (`AddEventItem(detail)`, `AddItem(g, d, p, ...)`, `HaveCommonItem(g, d, p)`).
     - Interactive UI & lifecycle hooks (`OpenBox`, `AskClientForNumber`, `AskClientForString`,
       `GiveItemUI`, `ShowLadder`, `OnDeath`, `OnTimer`).
  3. Verifies target C# / JSON files:
     - `[LegacySource(path, sha256)]` / `"sourceSha256"` matches exact SHA-256 of the real file on disk.
     - Zero no-op empty `{}` or `=> Task.CompletedTask` method stubs on script entrypoints.
     - Every extracted `TaskID`, `ShopID`, `SkillID`, item `(G, D, P)`, UI modal, and lifecycle hook
       is accounted for in the target C# / JSON files (resolving `PlayerTaskConsts` and item catalog codes).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


def find_ecosystem_root() -> Path:
    for anchor in (Path(__file__).resolve().parent, Path.cwd().resolve()):
        for candidate in (anchor, *anchor.parents):
            if (candidate / "workbench").is_dir() and (candidate / "server").is_dir():
                return candidate
    raise FileNotFoundError("Could not locate jx-reborn ecosystem root.")


def load_read_jx_file(ecosystem_root: Path):
    scripts_dir = ecosystem_root / "workbench" / ".agents" / "skills" / "legacy-explore" / "scripts"
    if str(scripts_dir) not in sys.path:
        sys.path.insert(0, str(scripts_dir))
    import read_jx_file  # type: ignore
    return read_jx_file


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def strip_lua_comments(text: str) -> str:
    text = re.sub(r"--\[\[.*?\]\]", "", text, flags=re.S)
    lines = [re.sub(r"--.*$", "", line) for line in text.splitlines()]
    return "\n".join(lines)


def strip_csharp_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    lines = [re.sub(r"//.*$", "", line) for line in text.splitlines()]
    return "\n".join(lines)


FRAMEWORK_INCLUDE_PREFIXES = (
    "script/lib/",
    "script/dailogsys/",
    "script/vng_lib/",
    "script/class/",
    "script/ui/",
    "script/misc/",
    "script/activitysys/functionlib.lua",
    "script/activitysys/playerfunlib.lua",
    "script/activitysys/g_activity.lua",
    "script/activitysys/npcfunlib.lua",
    "script/activitysys/npcdailog.lua",
    "script/task/system/task_string.lua",
)


def is_framework_or_header_only_include(norm_rel_path: str) -> bool:
    low = norm_rel_path.lower().lstrip("/")
    if any(low.startswith(pref) for pref in FRAMEWORK_INCLUDE_PREFIXES):
        return True
    fname = low.rsplit("/", 1)[-1]
    if fname.endswith("_head.lua") or fname == "head.lua":
        return True
    return False


def resolve_legacy_lua_path(server1_dir: Path, raw_rel_path: str, read_jx_file_mod) -> Path | None:
    norm = re.sub(r"[\\/]+", "/", raw_rel_path.strip().strip('"').strip("'")).lstrip("/")
    direct = server1_dir / norm
    if direct.is_file():
        return direct

    # Support GBK-encoded directory/file names when given Unicode Chinese path (or vice versa)
    try:
        resolved = read_jx_file_mod.resolve_target(norm, server1_dir)
        if resolved and Path(resolved).is_file():
            return Path(resolved)
    except Exception:
        pass

    # Fallback: match normalized GB18030/UTF-8 relative path under server1/script
    parts = Path(norm).parts
    curr = server1_dir
    for part in parts:
        cand = curr / part
        if cand.exists():
            curr = cand
            continue
        matched = None
        if curr.is_dir():
            part_lower = part.lower()
            for child in curr.iterdir():
                if child.name.lower() == part_lower:
                    matched = child
                    break
                try:
                    gbk_decoded = child.name.encode("latin1").decode("gb18030")
                    if gbk_decoded.lower() == part_lower:
                        matched = child
                        break
                except Exception:
                    pass
                try:
                    utf8_as_gbk = part.encode("gb18030").decode("latin1")
                    if child.name == utf8_as_gbk:
                        matched = child
                        break
                except Exception:
                    pass
        if matched is None:
            return None
        curr = matched
    return curr if curr.is_file() else None


def decode_jx_bytes(raw_bytes: bytes, read_jx_file_mod) -> str:
    return "\n".join(read_jx_file_mod.decode_line(ln) for ln in raw_bytes.splitlines())


def collect_lua_constants(server1_dir: Path, entry_lua: Path, read_jx_file_mod, max_depth: int = 3) -> dict[str, int]:
    """Recursively scan Included Lua headers for integer constant assignments (`TSK_FOO = 48`)."""
    visited: set[Path] = set()
    consts: dict[str, int] = {}

    def visit(path: Path, depth: int) -> None:
        if depth > max_depth or path in visited or not path.is_file():
            return
        visited.add(path)
        decoded = decode_jx_bytes(path.read_bytes(), read_jx_file_mod)
        clean = strip_lua_comments(decoded)

        for inc_match in re.finditer(r'\bInclude\s*\(\s*["\']([^"\']+)["\']\s*\)', clean):
            inc_path = resolve_legacy_lua_path(server1_dir, inc_match.group(1), read_jx_file_mod)
            if inc_path is not None:
                visit(inc_path, depth + 1)

        for m in re.finditer(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(\d+)\b", clean):
            consts[m.group(1)] = int(m.group(2))

    visit(entry_lua, 0)
    return consts


def eval_int_token(token: str, consts: dict[str, int]) -> int | None:
    tok = token.strip()
    if re.fullmatch(r"\d+", tok):
        return int(tok)
    if tok in consts:
        return consts[tok]
    return None


def extract_lua_fingerprint(server1_dir: Path, lua_path: Path, read_jx_file_mod) -> dict[str, Any]:
    raw_bytes = lua_path.read_bytes()
    consts = collect_lua_constants(server1_dir, lua_path, read_jx_file_mod)

    # Collect entry Lua + non-framework Included implementation templates (e.g. wrapper -> dicegame.lua / 轻功_木桩模板.lua)
    visited_impl: set[Path] = set()
    combined_chunks: list[str] = []
    included_impl_files: list[str] = []

    def gather_impl(p: Path, depth: int) -> None:
        if depth > 3 or p in visited_impl or not p.is_file():
            return
        visited_impl.add(p)
        dec = decode_jx_bytes(p.read_bytes(), read_jx_file_mod)
        cln = strip_lua_comments(dec)
        combined_chunks.append(cln)
        if depth > 0:
            included_impl_files.append(str(p))
        for inc_m in re.finditer(r'\bInclude\s*\(\s*["\']([^"\']+)["\']\s*\)', cln):
            raw_inc = inc_m.group(1)
            norm_inc = re.sub(r"[\\/]+", "/", raw_inc.strip()).lstrip("/")
            if is_framework_or_header_only_include(norm_inc):
                continue
            inc_resolved = resolve_legacy_lua_path(server1_dir, raw_inc, read_jx_file_mod)
            if inc_resolved is not None:
                gather_impl(inc_resolved, depth + 1)

    gather_impl(lua_path, 0)
    enc = "mixed-jx"
    clean = "\n".join(combined_chunks)

    task_ids: set[int] = set()
    for m in re.finditer(r"\b(?:GetTask|SetTask)\s*\(\s*([A-Za-z0-9_]+)", clean):
        val = eval_int_token(m.group(1), consts)
        if val is not None:
            task_ids.add(val)

    temp_task_ids: set[int] = set()
    for m in re.finditer(r"\b(?:GetTaskTemp|SetTaskTemp)\s*\(\s*([A-Za-z0-9_]+)", clean):
        val = eval_int_token(m.group(1), consts)
        if val is not None:
            temp_task_ids.add(val)

    shop_ids: set[int] = set()
    for m in re.finditer(r"\bSale\s*\(\s*([A-Za-z0-9_]+)", clean):
        val = eval_int_token(m.group(1), consts)
        if val is not None:
            shop_ids.add(val)

    skill_ids: set[int] = set()
    for m in re.finditer(r"\b(?:AddMagic|HaveMagic|DelMagic)\s*\(\s*([A-Za-z0-9_]+)", clean):
        val = eval_int_token(m.group(1), consts)
        if val is not None:
            skill_ids.add(val)

    rev_pos_ids: set[int] = set()
    for m in re.finditer(r"\bSetRevPos\s*\(\s*([A-Za-z0-9_]+)(?:\s*,\s*([A-Za-z0-9_]+))?\s*\)", clean):
        second = m.group(2)
        target_tok = second if second is not None else m.group(1)
        val = eval_int_token(target_tok, consts)
        if val is not None:
            rev_pos_ids.add(val)

    # Items: AddEventItem(detail) -> (4, detail), AddItem(g, d, p, ...) -> (g, d, p)
    event_item_details: set[int] = set()
    for m in re.finditer(r"\b(?:AddEventItem|HaveEventItem|DelEventItem)\s*\(\s*([A-Za-z0-9_]+)\s*\)", clean):
        val = eval_int_token(m.group(1), consts)
        if val is not None:
            event_item_details.add(val)

    item_tuples: set[tuple[int, int, int]] = set()
    for m in re.finditer(r"\b(?:AddItem|HaveCommonItem)\s*\(\s*([A-Za-z0-9_]+)\s*,\s*([A-Za-z0-9_]+)\s*,\s*([A-Za-z0-9_]+)", clean):
        g = eval_int_token(m.group(1), consts)
        d = eval_int_token(m.group(2), consts)
        p = eval_int_token(m.group(3), consts)
        if g is not None and d is not None and p is not None:
            item_tuples.add((g, d, p))

    ui_modals: set[str] = set()
    if re.search(r"\bOpenBox\s*\(", clean):
        ui_modals.add("OpenBox")
    if re.search(r"\b(?:AskClientForNumber|g_AskClientNumberEx)\s*\(", clean):
        ui_modals.add("AskClientForNumber")
    if re.search(r"\b(?:AskClientForString|g_AskClientStringEx)\s*\(", clean):
        ui_modals.add("AskClientForString")
    if re.search(r"\b(?:GiveItemUI|g_GiveItemUI)\s*\(", clean):
        ui_modals.add("GiveItemUI")
    if re.search(r"\bShowLadder\s*\(", clean):
        ui_modals.add("ShowLadder")
    if re.search(r"\bOpenDice\s*\(", clean):
        ui_modals.add("OpenDice")
    if re.search(r"\b(?:Describe|CreateNewSayEx|CreateTaskSay)\s*\(", clean) or "DailogClass:new" in clean:
        ui_modals.add("Describe")

    hooks: set[str] = set()
    for hook_name in ("main", "OnDeath", "OnTimer", "OnCancel"):
        if re.search(rf"\bfunction\s+{hook_name}\s*\(", clean):
            hooks.add(hook_name)

    has_byte_ops = bool(re.search(r"\b(?:GetByte|SetByte)\s*\(", clean))
    has_bit_ops = bool(re.search(r"\b(?:GetBit|SetBit)\s*\(", clean))

    return {
        "lua_file": str(lua_path),
        "included_impl_files": included_impl_files,
        "sha256": sha256_bytes(raw_bytes),
        "encoding": enc,
        "task_ids": sorted(task_ids),
        "temp_task_ids": sorted(temp_task_ids),
        "shop_ids": sorted(shop_ids),
        "skill_ids": sorted(skill_ids),
        "rev_pos_ids": sorted(rev_pos_ids),
        "event_item_details": sorted(event_item_details),
        "item_tuples": [list(t) for t in sorted(item_tuples)],
        "ui_modals": sorted(ui_modals),
        "hooks": sorted(hooks),
        "has_byte_ops": has_byte_ops,
        "has_bit_ops": has_bit_ops,
    }


def load_csharp_task_consts(ecosystem_root: Path) -> dict[str, int]:
    consts_file = ecosystem_root / "server" / "src" / "JXReborn.Primitives" / "Characters" / "PlayerTaskConsts.cs"
    mapping: dict[str, int] = {}
    if consts_file.is_file():
        txt = strip_csharp_comments(consts_file.read_text(encoding="utf-8"))
        for m in re.finditer(r"\bconst\s+(?:ushort|int)\s+(\w+)\s*=\s*(\d+)\s*;", txt):
            mapping[m.group(1)] = int(m.group(2))
            mapping[f"PlayerTaskConsts.{m.group(1)}"] = int(m.group(2))
    return mapping


def load_pack_item_catalog(ecosystem_root: Path) -> dict[str, tuple[int, int, int]]:
    items_dir = ecosystem_root / "pack-tieudaotu" / "data" / "items"
    code_to_gdp: dict[str, tuple[int, int, int]] = {}
    if not items_dir.is_dir():
        return code_to_gdp
    for jf in items_dir.glob("*.items.json"):
        try:
            data = json.loads(jf.read_text(encoding="utf-8"))
            for item in data.get("items", []):
                code = item.get("code")
                if code and "genre" in item and "detailType" in item and "particularType" in item:
                    code_to_gdp[code] = (int(item["genre"]), int(item["detailType"]), int(item["particularType"]))
        except Exception:
            continue
    return code_to_gdp


EMPTY_STUB_PATTERNS = [
    re.compile(
        r"\bpublic\s+(?:async\s+)?(?:ValueTask|Task|void)\s+(?:OnDialogAsync|OnUseItemAsync|OnInteractAsync|OnStepTrapAsync|OnNpcDeath|OnNpcTimer)\s*\([^)]*\)\s*\{\s*(?:return\s+(?:ValueTask|Task)\.CompletedTask\s*;\s*)?\}",
        re.S,
    ),
    re.compile(
        r"\bpublic\s+(?:ValueTask|Task)\s+(?:OnDialogAsync|OnUseItemAsync|OnInteractAsync|OnStepTrapAsync)\s*\([^)]*\)\s*=>\s*(?:ValueTask|Task)\.CompletedTask\s*;",
        re.S,
    ),
]


def extract_target_capabilities(
    ecosystem_root: Path,
    server1_dir: Path,
    target_paths: list[Path],
    read_jx_file_mod,
) -> tuple[dict[str, Any], list[str]]:
    task_consts = load_csharp_task_consts(ecosystem_root)
    item_catalog = load_pack_item_catalog(ecosystem_root)

    found_tasks: set[int] = set()
    found_temp_tasks: set[int] = set()
    found_shops: set[int] = set()
    found_skills: set[int] = set()
    found_rev_pos: set[int] = set()
    found_event_details: set[int] = set()
    found_item_tuples: set[tuple[int, int, int]] = set()
    found_modals: set[str] = set()
    found_hooks: set[str] = set()
    verified_legacy_sources: list[dict[str, str]] = []
    violations: list[str] = []

    for path in target_paths:
        if not path.is_file():
            violations.append(f"[MissingTargetFile] Target file does not exist: {path}")
            continue

        raw_text = path.read_text(encoding="utf-8-sig")

        if path.suffix.lower() == ".cs":
            clean = strip_csharp_comments(raw_text)

            # 1. Check for prohibited no-op stubs
            for pat in EMPTY_STUB_PATTERNS:
                if pat.search(clean):
                    violations.append(f"[EmptyHandlerStub] {path.name}: contains prohibited no-op empty script handler body.")

            # 2. Verify [LegacySource("...", "<sha256>")] against disk Ground Truth
            for lm in re.finditer(r'\[\s*LegacySource\s*\(\s*@?"([^"]+)"\s*,\s*"([0-9a-fA-F]{64})"\s*\)\s*\]', clean):
                src_rel, declared_sha = lm.group(1), lm.group(2).lower()
                resolved_lua = resolve_legacy_lua_path(server1_dir, src_rel, read_jx_file_mod)
                if resolved_lua is None:
                    violations.append(f"[BrokenLegacySource] {path.name}: [LegacySource('{src_rel}')] does not exist in legacy-server/bin/server1.")
                else:
                    actual_sha = sha256_bytes(resolved_lua.read_bytes())
                    if actual_sha != declared_sha:
                        violations.append(
                            f"[Sha256Mismatch] {path.name}: [LegacySource('{src_rel}')] declared {declared_sha[:12]}... != actual {actual_sha[:12]}..."
                        )
                    else:
                        verified_legacy_sources.append({"path": src_rel, "sha256": actual_sha})

            # Local integer constants in this C# file
            local_consts = dict(task_consts)
            for cm in re.finditer(r"\bconst\s+(?:ushort|int|short|byte)\s+(\w+)\s*=\s*(\d+)\s*;", clean):
                local_consts[cm.group(1)] = int(cm.group(2))

            # Local string constants in this C# file (e.g. `const string EarringCode = "hoa-tai-g4-d181-p0-r182";`)
            local_str_consts: dict[str, str] = {}
            for sm in re.finditer(r'\bconst\s+string\s+(\w+)\s*=\s*"([^"]+)"\s*;', clean):
                local_str_consts[sm.group(1)] = sm.group(2)

            # Tasks
            for tm in re.finditer(r"\b(?:GetTask|SetTask|GetTaskByte|SetTaskByte|GetTaskBit|SetTaskBit)\s*\(\s*([A-Za-z0-9_\.]+)", clean):
                tok = tm.group(1)
                if tok.isdigit():
                    found_tasks.add(int(tok))
                elif tok in local_consts:
                    found_tasks.add(local_consts[tok])

            for tm in re.finditer(r"\b(?:GetTempTask|SetTempTask)\s*\(\s*([A-Za-z0-9_\.]+)", clean):
                tok = tm.group(1)
                if tok.isdigit():
                    found_temp_tasks.add(int(tok))
                elif tok in local_consts:
                    found_temp_tasks.add(local_consts[tok])

            # Shops
            for sm in re.finditer(r"\bOpenShop\s*\(\s*([A-Za-z0-9_\.]+)", clean):
                tok = sm.group(1)
                if tok.isdigit():
                    found_shops.add(int(tok))
                elif tok in local_consts:
                    found_shops.add(local_consts[tok])

            # Skills
            for skm in re.finditer(r"\b(?:LearnSkill|AddSkill|HasSkill|GetSkillLevel|RemoveSkill|SetSkillLevel)\s*\(\s*([A-Za-z0-9_\.]+)", clean):
                tok = skm.group(1)
                if tok.isdigit():
                    found_skills.add(int(tok))
                elif tok in local_consts:
                    found_skills.add(local_consts[tok])

            # Revival points
            for rm in re.finditer(r"\b(?:SetRevivalPoint|BindRevivalPoint|SetRevPos)\s*\(\s*([A-Za-z0-9_\.]+)(?:\s*,\s*([A-Za-z0-9_\.]+))?", clean):
                for grp in (rm.group(1), rm.group(2)):
                    if grp:
                        if grp.isdigit():
                            found_rev_pos.add(int(grp))
                        elif grp in local_consts:
                            found_rev_pos.add(local_consts[grp])

            # Items via string codes (either direct string literals or local const identifiers)
            referenced_codes: set[str] = set(re.findall(r'"([a-z0-9]+(?:-[a-z0-9]+)+)"', clean))
            for ident in local_str_consts.values():
                referenced_codes.add(ident)
            for code in referenced_codes:
                if code in item_catalog:
                    g, d, p = item_catalog[code]
                    found_item_tuples.add((g, d, p))
                    if g == 4:
                        found_event_details.add(d)

            # Direct tuple calls e.g. `(4, 181, ...)` or `GrantEventItem(181)`
            for em in re.finditer(r"\b(?:TryGrantEventItem|GrantEventItem|HasEventItem|ConsumeEventItem)\s*\(\s*(\d+)", clean):
                found_event_details.add(int(em.group(1)))
            for im in re.finditer(r"\b(?:TryGrantItem|TryGrantEquipment|HasItemCount|ConsumeItem)\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", clean):
                g, d, p = int(im.group(1)), int(im.group(2)), int(im.group(3))
                found_item_tuples.add((g, d, p))
                if g == 4:
                    found_event_details.add(d)

            # UI Modals & Hooks
            if re.search(r"\bOpenStoreBox\s*\(", clean):
                found_modals.add("OpenBox")
            if re.search(r"\bAskClientForNumber\s*\(", clean):
                found_modals.add("AskClientForNumber")
            if re.search(r"\bAskClientForString\s*\(", clean):
                found_modals.add("AskClientForString")
            if re.search(r"\b(?:OpenGiveItemUi|GiveItemUI|PromptGiveItem)\s*\(", clean):
                found_modals.add("GiveItemUI")
            if re.search(r"\b(?:ShowLadder|SendLadder|OpenLadder)\s*\(", clean):
                found_modals.add("ShowLadder")
            if re.search(r"\bOpenDiceGame\s*\(", clean):
                found_modals.add("OpenDice")
            if re.search(r"\b(?:Describe|SendDescribe|SayByStringId)\s*\(", clean):
                found_modals.add("Describe")

            if re.search(r"\b(?:OnDialogAsync|OnUseItemAsync|OnInteractAsync|OnStepTrapAsync)\s*\(", clean):
                found_hooks.add("main")
            if re.search(r"\bOnNpcDeath(?:Async)?\s*\(", clean):
                found_hooks.add("OnDeath")
            if re.search(r"\bOnNpcTimer(?:Async)?\s*\(", clean):
                found_hooks.add("OnTimer")

        elif path.suffix.lower() == ".json":
            data = json.loads(raw_text)
            # Check sourceSha256 integrity if present anywhere in JSON entries
            def walk_json(node: Any) -> None:
                if isinstance(node, dict):
                    if "sourcePath" in node and "sourceSha256" in node:
                        sp = str(node["sourcePath"])
                        decl = str(node["sourceSha256"]).lower()
                        rl = resolve_legacy_lua_path(server1_dir, sp, read_jx_file_mod)
                        if rl is None:
                            violations.append(f"[BrokenJsonSource] {path.name}: sourcePath '{sp}' missing on disk.")
                        elif sha256_bytes(rl.read_bytes()) != decl:
                            violations.append(f"[JsonSha256Mismatch] {path.name}: sourcePath '{sp}' SHA-256 mismatch.")
                        else:
                            verified_legacy_sources.append({"path": sp, "sha256": decl})
                    if "shopId" in node and isinstance(node["shopId"], int):
                        found_shops.add(node["shopId"])
                    if "dialogs" in node or "lines" in node or "prompt" in node:
                        found_hooks.add("main")
                    for v in node.values():
                        walk_json(v)
                elif isinstance(node, list):
                    for elem in node:
                        walk_json(elem)

            walk_json(data)

    return {
        "task_ids": sorted(found_tasks),
        "temp_task_ids": sorted(found_temp_tasks),
        "shop_ids": sorted(found_shops),
        "skill_ids": sorted(found_skills),
        "rev_pos_ids": sorted(found_rev_pos),
        "event_item_details": sorted(found_event_details),
        "item_tuples": [list(t) for t in sorted(found_item_tuples)],
        "ui_modals": sorted(found_modals),
        "hooks": sorted(found_hooks),
        "verified_legacy_sources": verified_legacy_sources,
    }, violations


def audit_feature(
    ecosystem_root: Path,
    lua_specs: list[str],
    target_specs: list[str],
) -> tuple[bool, dict[str, Any]]:
    server1_dir = ecosystem_root / "legacy-server" / "bin" / "server1"
    read_jx_file_mod = load_read_jx_file(ecosystem_root)

    lua_files: list[Path] = []
    errors: list[str] = []

    for spec in lua_specs:
        cand = Path(spec)
        if cand.is_file():
            lua_files.append(cand.resolve())
            continue
        resolved = resolve_legacy_lua_path(server1_dir, spec, read_jx_file_mod)
        if resolved is not None:
            lua_files.append(resolved)
        else:
            errors.append(f"[MissingLegacyLua] Could not resolve legacy Lua script '{spec}' under {server1_dir}.")

    targets: list[Path] = []
    for tspec in target_specs:
        p = Path(tspec)
        if not p.is_absolute():
            p = (ecosystem_root / tspec).resolve()
        if p.is_dir():
            targets.extend(sorted(p.rglob("*.cs")))
            targets.extend(sorted(p.rglob("*.json")))
        else:
            targets.append(p)

    if errors:
        return False, {"passed": False, "violations": errors}

    merged_lua = {
        "files": [],
        "included_impl_files": [],
        "task_ids": set(),
        "temp_task_ids": set(),
        "shop_ids": set(),
        "skill_ids": set(),
        "rev_pos_ids": set(),
        "event_item_details": set(),
        "item_tuples": set(),
        "ui_modals": set(),
        "hooks": set(),
    }

    for lf in lua_files:
        fp = extract_lua_fingerprint(server1_dir, lf, read_jx_file_mod)
        merged_lua["files"].append({"path": str(lf), "sha256": fp["sha256"], "encoding": fp["encoding"]})
        for inc_f in fp.get("included_impl_files", []):
            if inc_f not in merged_lua["included_impl_files"]:
                merged_lua["included_impl_files"].append(inc_f)
        merged_lua["task_ids"].update(fp["task_ids"])
        merged_lua["temp_task_ids"].update(fp["temp_task_ids"])
        merged_lua["shop_ids"].update(fp["shop_ids"])
        merged_lua["skill_ids"].update(fp["skill_ids"])
        merged_lua["rev_pos_ids"].update(fp["rev_pos_ids"])
        merged_lua["event_item_details"].update(fp["event_item_details"])
        merged_lua["item_tuples"].update(tuple(x) for x in fp["item_tuples"])
        merged_lua["ui_modals"].update(fp["ui_modals"])
        merged_lua["hooks"].update(fp["hooks"])

    target_caps, target_violations = extract_target_capabilities(ecosystem_root, server1_dir, targets, read_jx_file_mod)
    errors.extend(target_violations)

    missing_tasks = sorted(merged_lua["task_ids"] - set(target_caps["task_ids"]))
    missing_temp_tasks = sorted(merged_lua["temp_task_ids"] - set(target_caps["temp_task_ids"]))
    missing_shops = sorted(merged_lua["shop_ids"] - set(target_caps["shop_ids"]))
    missing_skills = sorted(merged_lua["skill_ids"] - set(target_caps["skill_ids"]))
    missing_rev_pos = sorted(merged_lua["rev_pos_ids"] - set(target_caps["rev_pos_ids"]))
    missing_event_items = sorted(merged_lua["event_item_details"] - set(target_caps["event_item_details"]))
    missing_item_tuples = sorted(merged_lua["item_tuples"] - {tuple(x) for x in target_caps["item_tuples"]})
    missing_modals = sorted(merged_lua["ui_modals"] - set(target_caps["ui_modals"]))
    expected_hooks = {h for h in merged_lua["hooks"] if h in ("main", "OnDeath", "OnTimer")}
    missing_hooks = sorted(expected_hooks - set(target_caps["hooks"]))

    if missing_tasks:
        errors.append(f"[MissingTaskIDs] Legacy Lua uses TaskIDs {missing_tasks} not found in target C#/JSON.")
    if missing_temp_tasks:
        errors.append(f"[MissingTempTaskIDs] Legacy Lua uses TempTaskIDs {missing_temp_tasks} not found in target.")
    if missing_shops:
        errors.append(f"[MissingShops] Legacy Lua calls Sale({missing_shops}) not found in target.")
    if missing_skills:
        errors.append(f"[MissingSkills] Legacy Lua mutates/checks SkillIDs {missing_skills} not found in target.")
    if missing_rev_pos:
        errors.append(f"[MissingRevPos] Legacy Lua sets RevivalPoint {missing_rev_pos} not found in target.")
    if missing_event_items:
        errors.append(f"[MissingEventItems] Legacy Lua uses EventItem detail IDs (G=4) {missing_event_items} not found in target.")
    if missing_item_tuples:
        errors.append(f"[MissingItemTuples] Legacy Lua grants/checks Item (G,D,P) {[list(x) for x in missing_item_tuples]} not found in target.")
    if missing_modals:
        errors.append(f"[MissingUiModals] Legacy Lua invokes UI modals {missing_modals} not found in target.")
    if missing_hooks:
        errors.append(f"[MissingLifecycleHooks] Legacy Lua defines hooks {missing_hooks} not implemented in target.")

    report = {
        "passed": len(errors) == 0,
        "legacy_fingerprint": {
            "files": merged_lua["files"],
            "included_impl_files": merged_lua["included_impl_files"],
            "task_ids": sorted(merged_lua["task_ids"]),
            "temp_task_ids": sorted(merged_lua["temp_task_ids"]),
            "shop_ids": sorted(merged_lua["shop_ids"]),
            "skill_ids": sorted(merged_lua["skill_ids"]),
            "rev_pos_ids": sorted(merged_lua["rev_pos_ids"]),
            "event_item_details": sorted(merged_lua["event_item_details"]),
            "item_tuples": [list(x) for x in sorted(merged_lua["item_tuples"])],
            "ui_modals": sorted(merged_lua["ui_modals"]),
            "hooks": sorted(expected_hooks),
        },
        "target_capabilities": target_caps,
        "violations": errors,
    }
    return len(errors) == 0, report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Static Ground-Truth Feature Parity Auditor")
    sub = parser.add_subparsers(dest="command", required=True)

    p_fp = sub.add_parser("fingerprint", help="Extract Ground-Truth behavioral fingerprint from a Legacy Lua script")
    p_fp.add_argument("--lua", required=True, help="Relative path under legacy-server/bin/server1 or absolute path")

    p_aud = sub.add_parser("audit", help="Verify target C#/JSON files cover all Legacy Lua fingerprints with zero stubs")
    p_aud.add_argument("--lua", action="append", required=True, help="One or more legacy Lua script paths")
    p_aud.add_argument("--target", action="append", required=True, help="One or more target C# / JSON files or directories")
    p_aud.add_argument("--exec-vectors", action="store_true", help="Also execute Lua4Interpreter vs C# behavioral state vectors via JXReborn.ScriptMigrator")
    p_aud.add_argument("--strict", action="store_true")
    p_aud.add_argument("--json", action="store_true")

    args = parser.parse_args(argv)
    ecosystem_root = find_ecosystem_root()
    server1_dir = ecosystem_root / "legacy-server" / "bin" / "server1"
    read_jx_file_mod = load_read_jx_file(ecosystem_root)

    if args.command == "fingerprint":
        resolved = Path(args.lua).resolve() if Path(args.lua).is_file() else resolve_legacy_lua_path(server1_dir, args.lua, read_jx_file_mod)
        if resolved is None:
            print(f"Could not resolve legacy Lua script: {args.lua}", file=sys.stderr)
            return 1
        fp = extract_lua_fingerprint(server1_dir, resolved, read_jx_file_mod)
        print(json.dumps(fp, indent=2, ensure_ascii=False))
        return 0

    if args.command == "audit":
        passed, report = audit_feature(ecosystem_root, args.lua, args.target)
        if args.exec_vectors and passed:
            migrator_proj = ecosystem_root / "workbench" / "tools" / "JXReborn.ScriptMigrator" / "JXReborn.ScriptMigrator.csproj"
            legacy_bin = ecosystem_root / "legacy-server" / "bin"
            pack_dir = ecosystem_root / "pack-tieudaotu"
            vector_results = []
            for lua_arg in args.lua:
                proc = subprocess.run(
                    [
                        "dotnet",
                        "run",
                        "--project",
                        str(migrator_proj),
                        "--",
                        "verify-script",
                        "--legacy",
                        str(legacy_bin),
                        "--pack",
                        str(pack_dir),
                        "--lua",
                        lua_arg,
                        "--strict",
                    ],
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                )
                combined_out = (proc.stdout + "\n" + proc.stderr).strip()
                vector_results.append({"lua": lua_arg, "exit_code": proc.returncode, "output": combined_out})
                if proc.returncode != 0:
                    passed = False
                    report["passed"] = False
                    report["violations"].append(f"[BehavioralVectorMismatch] {lua_arg}:\n{combined_out}")
            report["behavioral_vector_verification"] = vector_results
        if args.json:
            print(json.dumps(report, indent=2, ensure_ascii=False))
        else:
            status_tag = "[PASS]" if passed else "[FAIL]"
            print(f"{status_tag} Feature Parity Audit ({len(report.get('legacy_fingerprint', {}).get('files', []))} Lua file(s) -> {len(args.target)} target(s))")
            if passed:
                fp = report["legacy_fingerprint"]
                print(
                    f"  Verified Tasks={fp['task_ids']}, Shops={fp['shop_ids']}, Skills={fp['skill_ids']}, "
                    f"Items={len(fp['item_tuples']) + len(fp['event_item_details'])}, Modals={fp['ui_modals']}, Hooks={fp['hooks']}"
                )
            else:
                for v in report["violations"]:
                    print(f"  - {v}", file=sys.stderr)
        return 0 if passed else 1

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
