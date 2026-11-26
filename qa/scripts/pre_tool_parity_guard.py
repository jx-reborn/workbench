#!/usr/bin/env python3
"""Fast Per-Conversation Double-Blind & Clean-Room PreToolUse Guard (`pre_tool_parity_guard.py`).

Implements the Antigravity `hooks.json` `PreToolUse` stdin/stdout JSON contract (< 20ms, stdlib-only).

Concurrency-Safe Role Resolution (fixes v1.0 global singleton race):
  1. Scoped per-conversation lock: `workbench/artifacts/tdd-locks/conversations/<conversationId>.json`.
  2. Intrinsic transcript marker detection (cached by `conversationId`): inspects the head of
     `payload["transcriptPath"]` for `[HARNESS_ROLE:blind_qa]` or `[HARNESS_ROLE:blind_coder]`,
     automatically isolating every `jx-blind-qa` and `jx-blind-coder` worker in parallel without
     ever locking out the Coordinator (`excluded_conversation_ids`) or sibling parallel agents!
  3. Fallback `active_phase.json` (honored only when `conversationId` is not in `excluded_conversation_ids`).

Enforces 5 mechanical guardrails:
  - Blocks manual `write_file`/`edit_file` on signed `Baselines/*.capture.json` and `Baselines/*.oracle.json`.
  - Blocks Clean-Room leaks (`0x08...`, `0x004...`, `K*.cpp`, `ghidra`, `legacy-server`, `jx_linux_y`, `game.exe`)
    into `server/src/`.
  - Blocks shell commands stopping/removing protected containers (`jx-server-*`, `jx-mariadb-*`, `jx-manager`,
    `jx-standalone`, `sqlserver`).
  - `blind_qa` (`phase=red`): blocks reading production implementation bodies (`server/src/**`,
    `pack-tieudaotu/src/**/Scripts/**`) while whitelisting public C# interfaces/contracts/primitives,
    and blocks writing outside `tests/`.
  - `blind_coder` (`phase=green`): blocks reading/probing `legacy-server/`, `legacy-client/`, `**/tests/**`,
    `*.capture.json`, `*.oracle.json`, `*.scenario.json`, and `jxr-holdout-vault`, and blocks writing `tests/`.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

CLEAN_ROOM_PATTERNS = [
    (re.compile(r"\b0x08[0-9a-fA-F]{6}\b"), "ELF virtual address literal"),
    (re.compile(r"\b0x004[0-9a-fA-F]{5}\b"), "PE virtual address literal"),
    (re.compile(r"\bK[A-Z][A-Za-z0-9_]+\.(?:cpp|h|hpp)\b"), "Legacy C/C++ filename citation"),
    (re.compile(r"(?i)\b(?:ghidra|legacy-server|jx-linux-bin|jx_linux_y|game\.exe)\b"), "Forbidden legacy term in server/src"),
]

PROTECTED_CONTAINER_CMD_RE = re.compile(
    r"(?i)\b(?:wslc|docker|podman)\b[^\r\n;]*\b(?:rm|remove|stop|kill|restart)\b[^\r\n;]*\b(?:jx-server-|jx-mariadb-|jx-manager|jx-standalone|sqlserver)"
)

CODER_FORBIDDEN_CMD_RE = re.compile(
    r"(?i)(?:legacy-server|legacy-client|jxr-holdout-vault|\.capture\.json|\.oracle\.json|[\\/]+tests[\\/]+)"
)

QA_FORBIDDEN_CMD_RE = re.compile(
    r"(?i)(?:server[\\/]+src[\\/]+JXReborn\.(?:ZoneEngine|Domain|Application|Infrastructure|Adapter|Host)|pack-tieudaotu[\\/]+src[\\/]+[^\s\"']*[\\/]+Scripts[\\/]+)"
)


def allow() -> int:
    sys.stdout.write('{"decision":"allow"}\n')
    sys.stdout.flush()
    return 0


def deny(reason: str) -> int:
    sys.stdout.write(json.dumps({"decision": "deny", "reason": reason}, ensure_ascii=False) + "\n")
    sys.stdout.flush()
    return 0


def find_ecosystem_root() -> Path | None:
    here = Path(__file__).resolve()
    for p in (here.parent, *here.parents):
        if (p / "workbench").is_dir() and (p / "server").is_dir():
            return p
    return None


def detect_intrinsic_role_from_transcript(transcript_path_str: str) -> tuple[str, str]:
    """Inspect only the first 16 KB (initial prompt / system setup) of the agent's own transcript."""
    if not transcript_path_str:
        return "", ""
    tp = Path(transcript_path_str)
    if not tp.is_file():
        return "", ""
    try:
        with tp.open("rb") as f:
            head = f.read(16384).decode("utf-8", errors="ignore")
        # Only match the first 2 JSONL entries (the initial system/user prompt of a specialist subagent),
        # never later turns where a Coordinator discusses roles!
        first_lines = "\n".join(head.splitlines()[:2])
        if "[HARNESS_ROLE:blind_qa]" in first_lines:
            return "red", "blind_qa"
        if "[HARNESS_ROLE:blind_coder]" in first_lines:
            return "green", "blind_coder"
    except Exception:
        pass
    return "", ""


def resolve_effective_lock(eco_root: Path | None, payload: dict) -> dict:
    if eco_root is None:
        return {}

    conv_id = str(payload.get("conversationId") or "").strip()
    locks_dir = eco_root / "workbench" / "artifacts" / "tdd-locks"

    # 1. Per-conversation explicit lock file wins first
    if conv_id:
        conv_lock = locks_dir / "conversations" / f"{conv_id}.json"
        if conv_lock.is_file():
            try:
                return json.loads(conv_lock.read_text(encoding="utf-8-sig"))
            except Exception:
                pass

    # 2. Intrinsic per-agent transcript marker (`[HARNESS_ROLE:blind_qa|blind_coder]`)
    transcript_path = str(payload.get("transcriptPath") or "")
    intr_phase, intr_role = detect_intrinsic_role_from_transcript(transcript_path)
    if intr_role:
        return {"phase": intr_phase, "role": intr_role}

    # 3. Scoped fallback `active_phase.json` (skips any `excluded_conversation_ids` such as Coordinator!)
    global_lock_file = locks_dir / "active_phase.json"
    if global_lock_file.is_file():
        try:
            glock = json.loads(global_lock_file.read_text(encoding="utf-8-sig"))
            excluded = {str(x) for x in (glock.get("excluded_conversation_ids") or [])}
            if conv_id and conv_id in excluded:
                return {}
            target_conv = str(glock.get("target_conversation_id") or "").strip()
            if target_conv and conv_id and conv_id != target_conv:
                return {}
            return glock
        except Exception:
            pass

    return {}


def is_public_contract_allowed_for_qa(norm_lower: str) -> bool:
    if any(
        seg in norm_lower
        for seg in (
            "/jxreborn.primitives/",
            "/jxreborn.contracts/",
            "/jxreborn.gameassets/",
            "/scripting/iscript",
            "/scripting/inpc",
            "/scripting/iitem",
            "/scripting/iobj",
            "/scripting/itrap",
            "/scripting/imission",
            "/scripting/izone",
            "/scripting/legacysourceattribute.cs",
            "/dialogs/fluentdialogbuilder.cs",
            "/dialogs/dialogoption.cs",
        )
    ):
        return True
    fname = norm_lower.rsplit("/", 1)[-1]
    if re.match(r"^i[a-z][a-z0-9_]*\.cs$", fname):
        return True
    return False


def check_blind_read_path(target_path_str: str, role: str) -> str | None:
    if not target_path_str or not role:
        return None
    norm = target_path_str.replace("\\", "/")
    low = norm.lower()

    if role == "blind_qa":
        if ("/server/src/" in low or "/pack-tieudaotu/src/" in low) and not is_public_contract_allowed_for_qa(low):
            return (
                "Blocked by Double-Blind QA Barrier: Blind QA must derive test expectations strictly from "
                "Ground Truth (`legacy-server/`) + Clean-Room Spec + Public Contracts/Interfaces (`Primitives`, "
                "`Contracts`, `GameAssets`, `I*Script.cs`), without reading production implementation bodies."
            )

    elif role == "blind_coder":
        if any(
            seg in low
            for seg in (
                "/legacy-server/",
                "/legacy-client/",
                "/tests/",
                "jxr-holdout-vault",
            )
        ) or low.endswith((".capture.json", ".oracle.json", ".scenario.json")):
            return (
                "Blocked by Double-Blind Coder Barrier: Blind Coder must implement strictly from the Clean-Room Spec "
                "and Public Contracts without reading `legacy-server/`, `legacy-client/`, `tests/`, or wire baselines."
            )

    return None


def check_file_mutation(target_path_str: str, new_text: str, active_lock: dict) -> str | None:
    norm = target_path_str.replace("\\", "/")
    low = norm.lower()

    if "/baselines/" in low and (low.endswith(".capture.json") or low.endswith(".oracle.json")):
        return (
            "Blocked by JXReborn Parity Guard: Manual edits to signed Baselines/*.capture.json or *.oracle.json "
            "are prohibited (SHA-256 integrity). Record via WireCaptureStore / run-parity.ps1 -Mode Record."
        )

    if "/server/src/" in low and low.endswith(".cs"):
        for pat, label in CLEAN_ROOM_PATTERNS:
            for m in pat.finditer(new_text):
                if m.group(0).lower() == "0x08088405":
                    continue
                return (
                    f"Blocked by JXReborn Clean-Room Guard: Cannot write {label} ('{m.group(0)}') into server/src/. "
                    "Keep binary citations strictly inside legacy-server/discovery/ or workbench/."
                )

    phase = str(active_lock.get("phase", "")).lower()
    if phase in ("red", "green"):
        if "/jxreborn.wireparityproxy/diffing/" in low or (
            "/jxreborn.wireparityproxy/scenarios/maps/" in low and low.endswith(".scenario.json")
        ):
            return f"Blocked by TDD Phase Lock ({phase.upper()}): Judge/Differ and Scenario files are frozen."
    if phase == "red":
        if any(seg in low for seg in ("/server/src/", "/pack-tieudaotu/src/", "/pack-tieudaotu/data/")):
            return "Blocked by TDD Phase Lock (RED): Production src/ and data/ cannot be modified while authoring failing tests."
    elif phase == "green":
        if any(seg in low for seg in ("/server/tests/", "/pack-tieudaotu/tests/", "/workbench/tests/")) or (
            "/workbench/qa/" in low and ".tests/" in low
        ):
            return "Blocked by TDD Phase Lock (GREEN): Test files are frozen during production implementation."

    return None


def main() -> int:
    raw_in = sys.stdin.read()
    if not raw_in.strip():
        return allow()

    try:
        payload = json.loads(raw_in)
    except Exception:
        return allow()

    tool_call = payload.get("toolCall") or {}
    tool_name = str(tool_call.get("name") or "").lower()
    args = tool_call.get("args") or {}
    eco_root = find_ecosystem_root()
    active_lock = resolve_effective_lock(eco_root, payload)
    role = str(active_lock.get("role", "")).lower()

    if tool_name in ("read_file", "grep_search", "find_by_name", "list_dir"):
        for key in ("Path", "path", "SearchPath", "SearchDirectory", "DirectoryPath"):
            val = args.get(key)
            if isinstance(val, str) and val:
                violation = check_blind_read_path(val, role)
                if violation:
                    return deny(violation)
        return allow()

    if tool_name in ("write_file", "edit_file"):
        path_str = str(args.get("Path") or args.get("path") or "")
        text_body = str(args.get("Content") or args.get("Replacement") or "")
        if path_str:
            read_violation = check_blind_read_path(path_str, role)
            if read_violation:
                return deny(read_violation)
            violation = check_file_mutation(path_str, text_body, active_lock)
            if violation:
                return deny(violation)
        return allow()

    if tool_name == "run_command":
        cmd_line = str(args.get("CommandLine") or args.get("command") or "")
        if PROTECTED_CONTAINER_CMD_RE.search(cmd_line):
            return deny(
                "Blocked by JXReborn Container Safety Guard: Do not stop/remove non-sandbox containers "
                "(jx-server-*, jx-mariadb-*, jx-manager, jx-standalone, sqlserver). Only legacy-oracle-test is permitted."
            )
        if role == "blind_coder":
            if CODER_FORBIDDEN_CMD_RE.search(cmd_line):
                return deny(
                    "Blocked by Double-Blind Coder Shell Barrier: Blind Coder cannot run commands referencing "
                    "`tests/`, `legacy-server/`, `legacy-client/`, `*.capture.json`, or the holdout vault. "
                    "Verify compilation via `dotnet build` on `src/` projects; the Orchestrator runs holdout tests."
                )
        elif role == "blind_qa":
            if QA_FORBIDDEN_CMD_RE.search(cmd_line):
                return deny(
                    "Blocked by Double-Blind QA Shell Barrier: Blind QA cannot run commands inspecting production "
                    "`src/` implementation directories."
                )
        return allow()

    return allow()


if __name__ == "__main__":
    raise SystemExit(main())
