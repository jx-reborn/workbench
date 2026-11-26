"""Systemic Mechanic, Faction & Spec Convergence Workflow (`wf_mechanic_convergence.py`).

Executes via Antigravity `run_workflow`:
  run_workflow(
      ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_mechanic_convergence.py",
      Description="Verify and lock 18-Hz mechanic or Spec unit",
      Args={
          "domain": "engine_mechanics",
          "target_id": "traps.knpc_checktrap_latching",
          "goal_brief": "Verify KNpc::CheckTrap latching against discovery/traps/KNpc_And_KObj_Traps.cpp and lock L2 frame-exact tests"
      }
  )

Phases:
  Phase 1: Exhaustive Ground-Truth Specification (`legacy-server/discovery/` + `legacy-server/bin/`).
           Auto-invokes `discover-legacy-server` if `unresolved_binary_refs` are found.
  Phase 2: Atomic `Mechanical RED -> GREEN` (for parity defects) or Ground-Truth Frame-Exact Test Lock.
  Phase 3: Global `SolutionArchitectureComplianceTests` + `L3A+` WireCaptures + `parity_ledger.py verify-and-lock`.
"""

import asyncio
import json
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path

raw_args = globals().get("args") or {}
domain = str(raw_args.get("domain", "engine_mechanics"))
target_id = str(raw_args.get("target_id", ""))
goal_brief = str(raw_args.get("goal_brief", ""))
worker_timeout_sec = int(raw_args.get("worker_timeout_sec", 600))
max_coder_attempts = int(raw_args.get("max_coder_attempts", 2))

if not target_id:
    raise ValueError("Args['target_id'] is required (e.g. 'traps.knpc_checktrap_latching' or 'faction_wudang').")


def resolve_eco_root() -> Path:
    for ws in globals().get("WORKSPACES") or ():
        p = Path(ws).resolve()
        if (p.parent / "workbench").is_dir() and (p.parent / "server").is_dir():
            return p.parent
        if (p / "workbench").is_dir() and (p / "server").is_dir():
            return p
    cwd = Path.cwd().resolve()
    for anc in (cwd, *cwd.parents):
        if (anc / "workbench").is_dir() and (anc / "server").is_dir():
            return anc
    raise RuntimeError("Could not locate jx-reborn ecosystem root.")


ROOT = resolve_eco_root()
WORKBENCH = ROOT / "workbench"
SERVER = ROOT / "server"
PY = sys.executable

AUDIT_FEAT_PY = WORKBENCH / "qa" / "scripts" / "audit_feature_parity.py"
TDD_GATE_PY = WORKBENCH / "qa" / "scripts" / "verify_tdd_gate.py"
LEDGER_PY = WORKBENCH / "qa" / "scripts" / "parity_ledger.py"
TDD_LOCK_DIR = WORKBENCH / "artifacts" / "tdd-locks"
ACTIVE_PHASE_FILE = TDD_LOCK_DIR / "active_phase.json"


def run_cmd(cmd: list[str], cwd: Path | None = None) -> tuple[int, str]:
    proc = subprocess.run(
        cmd,
        cwd=str(cwd or ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    return proc.returncode, (proc.stdout + "\n" + proc.stderr).strip()


def set_tdd_phase(phase_name: str | None, role_name: str | None = None) -> None:
    TDD_LOCK_DIR.mkdir(parents=True, exist_ok=True)
    if phase_name is None:
        if ACTIVE_PHASE_FILE.exists():
            ACTIVE_PHASE_FILE.unlink()
    else:
        payload = {"phase": phase_name}
        if role_name:
            payload["role"] = role_name
        ACTIVE_PHASE_FILE.write_text(json.dumps(payload), encoding="utf-8")


# ============================================================================
# Phase 1: Ground-Truth Extraction & Binary Completeness Check
# ============================================================================
phase(f"Phase 1: Ground-Truth Spec ({target_id})")
spec = await agent(
    prompt=(
        f"Read `d:/Projects/jx-reborn/workbench/.agents/skills/khao-sat-co-che/SKILL.md` and "
        f"`d:/Projects/jx-reborn/workbench/.agents/skills/legacy-explore/SKILL.md`.\n"
        f"Target Domain: `{domain}` | Target ID: `{target_id}`\n"
        f"Goal Brief: {goal_brief}\n\n"
        f"1. Inspect exact Ground Truth in `legacy-server/discovery/`, `legacy-server/bin/server1/`, and current code/tests in `server/` & `pack-tieudaotu/`.\n"
        f"2. Do NOT edit any files in Phase 1. Produce an exact structured audit:\n"
        f"   - List any `unresolved_binary_refs` needing Ghidra decompilation.\n"
        f"   - Classify whether production code has a parity defect (`has_defect = true`) OR production code already matches Ground Truth but needs dedicated non-vacuous frame/boundary tests (`has_defect = false`)."
    ),
    schema={
        "type": "object",
        "properties": {
            "summary": {"type": "string"},
            "ground_truth_citations": {"type": "array", "items": {"type": "string"}},
            "unresolved_binary_refs": {"type": "array", "items": {"type": "string"}},
            "has_defect": {"type": "boolean"},
            "defect_details": {"type": "string"},
            "test_project": {"type": "string"},
            "test_filter": {"type": "string"},
            "legacy_lua_files": {"type": "array", "items": {"type": "string"}},
            "target_prod_files": {"type": "array", "items": {"type": "string"}},
        },
        "required": [
            "summary",
            "ground_truth_citations",
            "unresolved_binary_refs",
            "has_defect",
            "defect_details",
            "test_project",
            "test_filter",
            "legacy_lua_files",
            "target_prod_files",
        ],
    },
    role=f"GT Researcher ({target_id})",
)
log(f"Spec extracted: has_defect={spec['has_defect']} | citations={spec['ground_truth_citations']}")

if spec["unresolved_binary_refs"]:
    phase(f"Phase 1b: Binary Decompilation ({target_id})")
    decompile_note = await agent(
        prompt=(
            f"Read `d:/Projects/jx-reborn/legacy-server/.agents/skills/discover-legacy-server/SKILL.md`.\n"
            f"Decompile and resolve these binary references into `d:/Projects/jx-reborn/legacy-server/discovery/`:\n"
            f"{json.dumps(spec['unresolved_binary_refs'], indent=2)}"
        ),
        schema={"type": "string"},
        role=f"Binary Decompiler ({target_id})",
    )
    log(f"Decompilation completed: {decompile_note}")


# ============================================================================
# Phase 2: Mechanical RED -> GREEN (or Missing Ground-Truth Test Lock)
# ============================================================================
snap_path = TDD_LOCK_DIR / f"mech_{target_id.replace('.', '_')}_snap.json"
red_lock_path = TDD_LOCK_DIR / f"mech_{target_id.replace('.', '_')}_red.json"
s_code, s_out = run_cmd([PY, str(TDD_GATE_PY), "snapshot", "--out", str(snap_path)])
if s_code != 0:
    raise RuntimeError(f"Snapshot failed:\n{s_out}")

final_test_project = spec["test_project"]
final_test_filter = spec["test_filter"]

if spec["has_defect"]:
    vault_dir = Path(tempfile.gettempdir()) / f"jxr-holdout-vault-{uuid.uuid4().hex}"
    try:
        phase(f"Phase 2a: Double-Blind QA RED Test ({target_id})")
        set_tdd_phase("red", "blind_qa")
        red_res = await asyncio.wait_for(
            agent(
                prompt=(
                    f"You are a BLIND QA Test Author (`role = blind_qa`). You MUST NOT read production implementation bodies (`src/`).\n"
                    f"Write new standalone failing Ground-Truth test file(s) (`RED`) for `{target_id}` under `tests/` ONLY "
                    f"(reading only `legacy-server/` + Clean-Room Spec + Public Contracts/Interfaces).\n"
                    f"Follow `ground-truth-testing.md` and `khao-sat-co-che/SKILL.md` (1 type/file, `sealed`, `_Should`, 0 `Task.Delay`, exact Ground-Truth expectations).\n"
                    f"Spec:\n{json.dumps(spec, indent=2, ensure_ascii=False)}"
                ),
                schema={
                    "type": "object",
                    "properties": {
                        "test_project": {"type": "string"},
                        "test_filter": {"type": "string"},
                        "clean_room_spec_for_coder": {"type": "string"},
                    },
                    "required": ["test_project", "test_filter", "clean_room_spec_for_coder"],
                },
                role=f"Blind QA ({target_id})",
            ),
            timeout=worker_timeout_sec,
        )
        final_test_project = red_res["test_project"]
        final_test_filter = red_res["test_filter"]

        r_code, r_out = run_cmd([
            PY,
            str(TDD_GATE_PY),
            "check-red",
            "--snapshot",
            str(snap_path),
            "--project",
            final_test_project,
            "--filter",
            final_test_filter,
            "--save-red-lock",
            str(red_lock_path),
        ])
        if r_code != 0:
            raise RuntimeError(f"Mechanical RED Gate FAILED:\n{r_out}")
        log(f"[Mechanical RED PASS] {final_test_filter}")

        # Physically quarantine QA holdout tests outside workspace before Blind Coder starts!
        st_code, st_out = run_cmd([
            PY,
            str(TDD_GATE_PY),
            "stash-holdout",
            "--red-lock",
            str(red_lock_path),
            "--vault-dir",
            str(vault_dir),
        ])
        if st_code != 0:
            raise RuntimeError(f"Failed to stash holdout tests:\n{st_out}")
        log(st_out)

        sanitized_feedback = ""
        green_passed = False
        for attempt in range(1, max_coder_attempts + 1):
            phase(f"Phase 2b (Attempt {attempt}): Double-Blind Coder & Holdout Vault Evaluation ({target_id})")
            set_tdd_phase("green", "blind_coder")
            coder_prompt = (
                f"You are a BLIND Clean-Room Coder (`role = blind_coder`).\n"
                f"- You have ZERO access to `legacy-server/`, `tests/`, wire baselines, or the quarantined QA holdout vault.\n"
                f"- Implement the general Clean-Room production algorithm in `src/` strictly from the specification below and verify compilation via `dotnet build` on the `src/` project.\n"
                f"- NEVER write disassembly addresses (`0x08...`) or `.cpp` filenames in `server/src/`.\n\n"
                f"Clean-Room Specification:\n{red_res['clean_room_spec_for_coder']}\n"
                f"Target Production Files: {json.dumps(spec['target_prod_files'])}"
            )
            if sanitized_feedback:
                coder_prompt += f"\n\nPrevious Holdout Evaluation Feedback (Oracle literals redacted):\n{sanitized_feedback}"

            green_note = await asyncio.wait_for(
                agent(
                    prompt=coder_prompt,
                    schema={"type": "string"},
                    role=f"Blind Coder ({target_id} #{attempt})",
                ),
                timeout=worker_timeout_sec,
            )
            log(f"Coder attempt {attempt} note: {green_note}")

            set_tdd_phase(None)
            ev_code, ev_out = run_cmd([
                PY,
                str(TDD_GATE_PY),
                "evaluate-holdout",
                "--red-lock",
                str(red_lock_path),
                "--vault-dir",
                str(vault_dir),
                "--project",
                final_test_project,
                "--filter",
                final_test_filter,
                "--run-compliance",
            ])
            if ev_code == 0:
                green_passed = True
                log(f"[Double-Blind Holdout GREEN PASS] {final_test_filter}")
                break
            sanitized_feedback = ev_out[-4000:]
            log(f"Holdout attempt {attempt} failed; sanitized feedback prepared.")

        if not green_passed:
            raise RuntimeError(
                f"Blind Coder did not pass quarantined holdout suite after {max_coder_attempts} attempts "
                f"(possible spec ambiguity or test-dispute escalation needed):\n{sanitized_feedback}"
            )
    finally:
        set_tdd_phase(None)
else:
    phase(f"Phase 2: Author Ground-Truth Frame/Boundary Lock Tests ({target_id})")
    try:
        set_tdd_phase("red")
        lock_test_res = await agent(
            prompt=(
                f"Production code for `{target_id}` already matches Ground Truth, or needs dedicated frame-exact / boundary lock tests.\n"
                f"Write or verify Ground-Truth tests under `tests/` ONLY (strictly following `ground-truth-testing.md`: "
                f"1 type/file, `sealed`, `_Should`, real Ground-Truth values, 0 `Task.Delay`, 0 vacuous loops).\n"
                f"If updating `server/docs/parity_ledger.json` `test_files` is appropriate, list the new test file path in your summary (do NOT edit outside `tests/`).\n"
                f"Spec:\n{json.dumps(spec, indent=2, ensure_ascii=False)}"
            ),
            schema={
                "type": "object",
                "properties": {
                    "test_project": {"type": "string"},
                    "test_filter": {"type": "string"},
                    "test_file_rel": {"type": "string"},
                },
                "required": ["test_project", "test_filter", "test_file_rel"],
            },
            role=f"GT Test Lock Author ({target_id})",
        )
        final_test_project = lock_test_res["test_project"]
        final_test_filter = lock_test_res["test_filter"]
    finally:
        set_tdd_phase(None)


# ============================================================================
# Phase 3: Compliance, L3A+ Regression & Parity Master Ledger Lock
# ============================================================================
phase(f"Phase 3: Compliance & Ledger Lock ({target_id})")

if spec["legacy_lua_files"] and spec["target_prod_files"]:
    af_cmd = [PY, str(AUDIT_FEAT_PY), "audit", "--strict"]
    for lf in spec["legacy_lua_files"]:
        af_cmd.extend(["--lua", lf])
    for tf in spec["target_prod_files"]:
        af_cmd.extend(["--target", tf])
    af_code, af_out = run_cmd(af_cmd)
    if af_code != 0:
        raise RuntimeError(f"Gate 1 Feature Parity Audit FAILED:\n{af_out}")

comp_proj = SERVER / "tests" / "JXReborn.Domain.Tests" / "JXReborn.Domain.Tests.csproj"
c_code, c_out = run_cmd([
    "dotnet",
    "test",
    str(comp_proj),
    "--nologo",
    "--filter",
    "FullyQualifiedName~SolutionArchitectureComplianceTests",
])
if c_code != 0:
    raise RuntimeError(f"SolutionArchitectureComplianceTests FAILED:\n{c_out}")

wc_proj = SERVER / "tests" / "JXReborn.Integration.Tests" / "JXReborn.Integration.Tests.csproj"
wc_code, wc_out = run_cmd(["dotnet", "test", str(wc_proj), "--nologo", "--filter", "FullyQualifiedName~WireCaptures"])
if wc_code != 0:
    raise RuntimeError(f"Gate 3 Offline WireCaptures regression FAILED:\n{wc_out}")

lock_cmd = [
    PY,
    str(LEDGER_PY),
    "verify-and-lock",
    "--domain",
    domain,
    "--id",
    target_id,
    "--test-project",
    final_test_project,
    "--test-filter",
    final_test_filter,
    "--run-tests",
]
if spec["legacy_lua_files"] and spec["target_prod_files"]:
    lock_cmd.extend(["--feature-lua", spec["legacy_lua_files"][0]])
    for tf in spec["target_prod_files"]:
        lock_cmd.extend(["--feature-target", tf])

l_code, l_out = run_cmd(lock_cmd)
if l_code != 0:
    raise RuntimeError(f"Parity Ledger Lock REJECTED:\n{l_out}")

log(f"[LOCKED] {domain}::{target_id} -> zero_drift_locked!")
print(l_out)
