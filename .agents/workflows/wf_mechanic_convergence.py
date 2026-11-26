"""Systemic Mechanic, Faction & Spec Convergence Workflow (`wf_mechanic_convergence.py` — Harness v2.0).

Upgrades in Harness v2.0:
  1. Uses dedicated Specialist Subagents from `jx-parity-army`:
     - `jx-contract-architect` (Phase 0 Clean-Room Spec + `NotImplementedException` Contract Stubs -> 0 CS1061 deadlocks!)
     - `jx-binary-archaeologist` (Auto-decompilation of any `UNRESOLVED_BINARY_REF` symbols)
     - `jx-blind-qa` (Intrinsic `[HARNESS_ROLE:blind_qa]` read-isolated test author)
     - `jx-blind-coder` (Intrinsic `[HARNESS_ROLE:blind_coder]` clean-room coder with `test_dispute` support)
     - `jx-dispute-referee` (Supreme Court arbitrator resolving `test_bug` vs `spec_gap` vs `coder_bug`)
  2. Concurrency-safe per-conversation locks (`excluded_conversation_ids: [CONVERSATION_ID]`).
  3. Runs `audit_feature_parity.py --exec-vectors --strict` (`Lua4Interpreter` vs C# state vectors) when Lua scripts are ported.
"""

import asyncio
import hashlib
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
parent_conv_id = str(globals().get("CONVERSATION_ID") or "")

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
    proc = subprocess.run(cmd, cwd=str(cwd or ROOT), capture_output=True, text=True, encoding="utf-8", errors="replace")
    return proc.returncode, (proc.stdout + "\n" + proc.stderr).strip()


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def set_tdd_phase(phase_name: str | None, role_name: str | None = None) -> None:
    TDD_LOCK_DIR.mkdir(parents=True, exist_ok=True)
    if phase_name is None:
        if ACTIVE_PHASE_FILE.exists():
            ACTIVE_PHASE_FILE.unlink()
    else:
        payload: dict = {"phase": phase_name}
        if role_name:
            payload["role"] = role_name
        if parent_conv_id:
            payload["excluded_conversation_ids"] = [parent_conv_id]
        ACTIVE_PHASE_FILE.write_text(json.dumps(payload), encoding="utf-8")


def refresh_red_lock_hashes_from_vault(red_lock_path: Path, vault_dir: Path) -> None:
    """After Referee fixes a genuine test bug inside the Holdout Vault, update locked test SHA-256s."""
    lock = json.loads(red_lock_path.read_text(encoding="utf-8-sig"))
    manifest = json.loads((vault_dir / "vault_manifest.json").read_text(encoding="utf-8-sig"))
    for entry in manifest.get("entries", []):
        vfile = vault_dir / entry["vault_file"]
        orig_key = entry["original_path"]
        if vfile.is_file():
            lock["hashes"]["tests"][orig_key] = sha256_file(vfile)
    red_lock_path.write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8")


# ============================================================================
# Phase 0 & 1: Ground-Truth Audit, Binary Decompilation & Contract Stubs
# ============================================================================
phase(f"Phase 1: Contract & Ground-Truth Spec ({target_id})")
spec = await asyncio.wait_for(
    agent(
        prompt=(
            f"Target Domain: `{domain}` | Target ID: `{target_id}`\n"
            f"Goal Brief: {goal_brief}\n\n"
            f"1. Read relevant skills (`khao-sat-co-che`, `port-legacy-feature`, `port-s3relay-system`, `khao-sat-chien-truong-pho-ban`, or `dong-bo-hien-thi-client`).\n"
            f"2. Audit exact Ground Truth in `legacy-server/discovery/`, `legacy-server/bin/`, and `legacy-client/discovery/`.\n"
            f"3. Produce a 100% Clean-Room Specification (`clean_room_spec`) containing ZERO `0x08...` addresses or `.cpp` names.\n"
            f"4. ONLY IF a brand-new public interface method, `PlayerTaskConsts` constant, or new `sealed` script class signature is missing for compilation, "
            f"write minimal `throw new NotImplementedException();` contract stubs in `src/` and verify `dotnet build` succeeds."
        ),
        schema={
            "type": "object",
            "properties": {
                "summary": {"type": "string"},
                "clean_room_spec": {"type": "string"},
                "ground_truth_citations": {"type": "array", "items": {"type": "string"}},
                "unresolved_binary_refs": {"type": "array", "items": {"type": "string"}},
                "has_defect": {"type": "boolean"},
                "test_project": {"type": "string"},
                "test_filter": {"type": "string"},
                "legacy_lua_files": {"type": "array", "items": {"type": "string"}},
                "target_prod_files": {"type": "array", "items": {"type": "string"}},
                "exec_lua_vectors": {"type": "boolean"},
            },
            "required": [
                "summary",
                "clean_room_spec",
                "ground_truth_citations",
                "unresolved_binary_refs",
                "has_defect",
                "test_project",
                "test_filter",
                "legacy_lua_files",
                "target_prod_files",
                "exec_lua_vectors",
            ],
        },
        role=f"Contract Architect ({target_id})",
        type_name="jx-contract-architect",
    ),
    timeout=worker_timeout_sec,
)
log(f"Spec ready: has_defect={spec['has_defect']} | citations={len(spec['ground_truth_citations'])}")

if spec["unresolved_binary_refs"]:
    phase(f"Phase 1b: Binary Decompilation ({target_id})")
    decompile_note = await asyncio.wait_for(
        agent(
            prompt=(
                f"Read `d:/Projects/jx-reborn/legacy-server/.agents/skills/discover-legacy-server/SKILL.md`.\n"
                f"Decompile and resolve these binary references into `d:/Projects/jx-reborn/legacy-server/discovery/`:\n"
                f"{json.dumps(spec['unresolved_binary_refs'], indent=2)}"
            ),
            schema={"type": "string"},
            role=f"Binary Archaeologist ({target_id})",
            type_name="jx-binary-archaeologist",
        ),
        timeout=worker_timeout_sec,
    )
    log(f"Decompilation completed: {decompile_note}")


# ============================================================================
# Phase 2: Double-Blind QA + Holdout Vault + Blind Coder + Dispute Referee
# ============================================================================
snap_path = TDD_LOCK_DIR / f"mech_{target_id.replace('.', '_')}_snap.json"
red_lock_path = TDD_LOCK_DIR / f"mech_{target_id.replace('.', '_')}_red.json"
s_code, s_out = run_cmd([PY, str(TDD_GATE_PY), "snapshot", "--out", str(snap_path)])
if s_code != 0:
    raise RuntimeError(f"Snapshot failed:\n{s_out}")

final_test_project = spec["test_project"]
final_test_filter = spec["test_filter"]
active_clean_spec = spec["clean_room_spec"]

if spec["has_defect"]:
    vault_dir = Path(tempfile.gettempdir()) / f"jxr-holdout-vault-{uuid.uuid4().hex}"
    try:
        phase(f"Phase 2a: Double-Blind QA RED Test ({target_id})")
        set_tdd_phase("red", "blind_qa")
        red_res = await asyncio.wait_for(
            agent(
                prompt=(
                    f"[HARNESS_ROLE:blind_qa]\n"
                    f"Author standalone failing Ground-Truth Holdout Test file(s) (`RED`) for `{target_id}` under `tests/` ONLY.\n"
                    f"- Do NOT read production `src/` implementation bodies.\n"
                    f"- Ensure any `I*Script` test helper defines a `public` parameterless constructor.\n"
                    f"- Ground Truth Citations: {json.dumps(spec['ground_truth_citations'])}\n\n"
                    f"Clean-Room Spec:\n{active_clean_spec}"
                ),
                schema={
                    "type": "object",
                    "properties": {
                        "test_project": {"type": "string"},
                        "test_filter": {"type": "string"},
                    },
                    "required": ["test_project", "test_filter"],
                },
                role=f"Blind QA ({target_id})",
                type_name="jx-blind-qa",
            ),
            timeout=worker_timeout_sec,
        )
        final_test_project = red_res["test_project"]
        final_test_filter = red_res["test_filter"]

        set_tdd_phase(None)
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

        sanitized_feedback = ""
        green_passed = False
        referee_invoked = False

        attempt = 1
        total_budget = max_coder_attempts + 1  # +1 post-Referee attempt if needed
        while attempt <= total_budget:
            phase(f"Phase 2b (Attempt {attempt}): Blind Coder & Holdout Vault ({target_id})")
            set_tdd_phase("green", "blind_coder")
            coder_prompt = (
                f"[HARNESS_ROLE:blind_coder]\n"
                f"Implement the Clean-Room production solution in `src/` / `data/` strictly from the specification below.\n"
                f"Target Production Files: {json.dumps(spec['target_prod_files'])}\n\n"
                f"Clean-Room Specification:\n{active_clean_spec}"
            )
            if sanitized_feedback:
                coder_prompt += f"\n\nSanitized Holdout Evaluation Feedback:\n{sanitized_feedback}"

            coder_res = await asyncio.wait_for(
                agent(
                    prompt=coder_prompt,
                    schema={
                        "type": "object",
                        "properties": {
                            "status": {"type": "string", "enum": ["completed", "test_dispute"]},
                            "summary": {"type": "string"},
                            "dispute_proof": {"type": "string"},
                        },
                        "required": ["status", "summary", "dispute_proof"],
                    },
                    role=f"Blind Coder ({target_id} #{attempt})",
                    type_name="jx-blind-coder",
                ),
                timeout=worker_timeout_sec,
            )
            set_tdd_phase(None)
            log(f"Coder #{attempt}: status={coder_res['status']} | {coder_res['summary']}")

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
            should_escalate = (coder_res["status"] == "test_dispute" or attempt >= max_coder_attempts) and not referee_invoked

            if should_escalate:
                referee_invoked = True
                phase(f"Phase 2c: Supreme Court Dispute Referee ({target_id})")
                ref_verdict = await asyncio.wait_for(
                    agent(
                        prompt=(
                            f"Arbitrate the conflict between Blind QA's Holdout Tests and Blind Coder's implementation.\n"
                            f"- Holdout Vault Directory: `{vault_dir}`\n"
                            f"- Ground Truth Citations: {json.dumps(spec['ground_truth_citations'])}\n"
                            f"- Target Production Files: {json.dumps(spec['target_prod_files'])}\n"
                            f"- Coder Dispute Claim: {coder_res['dispute_proof']}\n"
                            f"- Failure Output:\n{sanitized_feedback}\n\n"
                            f"If `verdict == 'test_bug'`, fix ONLY the test file inside `{vault_dir}` with Ground-Truth proof. "
                            f"Otherwise, return an amended Clean-Room Spec or algorithmic hint (with ZERO literal test values)."
                        ),
                        schema={
                            "type": "object",
                            "properties": {
                                "verdict": {"type": "string", "enum": ["test_bug", "spec_gap", "coder_bug"]},
                                "ground_truth_ruling": {"type": "string"},
                                "guidance_for_coder": {"type": "string"},
                            },
                            "required": ["verdict", "ground_truth_ruling", "guidance_for_coder"],
                        },
                        role=f"Dispute Referee ({target_id})",
                        type_name="jx-dispute-referee",
                    ),
                    timeout=worker_timeout_sec,
                )
                log(f"[Referee Verdict: {ref_verdict['verdict']}] {ref_verdict['ground_truth_ruling']}")

                if ref_verdict["verdict"] == "test_bug":
                    refresh_red_lock_hashes_from_vault(red_lock_path, vault_dir)
                    ev2_code, ev2_out = run_cmd([
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
                    if ev2_code == 0:
                        green_passed = True
                        log(f"[Referee Test Fix -> GREEN PASS] {final_test_filter}")
                        break
                    sanitized_feedback = ev2_out[-4000:]
                else:
                    active_clean_spec += f"\n\n[Referee Clarification]:\n{ref_verdict['guidance_for_coder']}"

            attempt += 1

        if not green_passed:
            raise RuntimeError(f"Failed to converge `{target_id}` even after Supreme Court arbitration:\n{sanitized_feedback}")
    finally:
        set_tdd_phase(None)
else:
    phase(f"Phase 2: Blind QA Frame/Boundary Lock Tests ({target_id})")
    try:
        set_tdd_phase("red", "blind_qa")
        lock_test_res = await asyncio.wait_for(
            agent(
                prompt=(
                    f"[HARNESS_ROLE:blind_qa]\n"
                    f"Production code for `{target_id}` matches Ground Truth; write or verify non-vacuous Ground-Truth frame/boundary lock tests under `tests/` ONLY.\n"
                    f"Clean-Room Spec:\n{active_clean_spec}"
                ),
                schema={
                    "type": "object",
                    "properties": {
                        "test_project": {"type": "string"},
                        "test_filter": {"type": "string"},
                    },
                    "required": ["test_project", "test_filter"],
                },
                role=f"Blind QA Lock ({target_id})",
                type_name="jx-blind-qa",
            ),
            timeout=worker_timeout_sec,
        )
        final_test_project = lock_test_res["test_project"]
        final_test_filter = lock_test_res["test_filter"]
    finally:
        set_tdd_phase(None)


# ============================================================================
# Phase 3: Static + Behavioral Vector Audit, Compliance, WireCaptures & Lock
# ============================================================================
phase(f"Phase 3: Vector Equivalence, Compliance & Ledger Lock ({target_id})")

if spec["legacy_lua_files"] and spec["target_prod_files"]:
    af_cmd = [PY, str(AUDIT_FEAT_PY), "audit", "--strict"]
    if spec.get("exec_lua_vectors"):
        af_cmd.append("--exec-vectors")
    for lf in spec["legacy_lua_files"]:
        af_cmd.extend(["--lua", lf])
    for tf in spec["target_prod_files"]:
        af_cmd.extend(["--target", tf])
    af_code, af_out = run_cmd(af_cmd)
    if af_code != 0:
        raise RuntimeError(f"Gate 1 Feature & Vector Equivalence Audit FAILED:\n{af_out}")

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
