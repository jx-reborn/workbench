"""End-to-End Map Convergence Workflow (`wf_map_convergence.py`).

Executes via Antigravity `run_workflow`:
  run_workflow(
      ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_map_convergence.py",
      Description="Converge Map 53 to Zero Parity Drift",
      Args={"map_id": 53, "max_fix_iterations": 3, "reuse_existing_clean_run": False}
  )

Orchestrates isolated Worker Agents through 5 mechanical gates:
  Phase 1: Ground-Truth Set-Cover Survey & `audit_map_scenario.py --strict` Gate.
  Phase 2: Clean `T=0` Live `WireParityProxy` Dual-Run (or verified clean baseline check).
  Phase 3: Root-Cause Finding Clustering & Mechanical `RED` Test Authoring (`active_phase=red`).
  Phase 4: Clean-Room Pack/Engine Porting & Mechanical `GREEN` Gate (`active_phase=green`).
  Phase 5: `L3A+` WireCapture Regression + `parity_ledger.py verify-and-lock` (`zero_drift_locked`).
"""

import asyncio
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path

raw_args = globals().get("args") or {}
map_id = int(raw_args.get("map_id", 53))
max_fix_iterations = int(raw_args.get("max_fix_iterations", 3))
reuse_existing_clean_run = bool(raw_args.get("reuse_existing_clean_run", False))
allow_benign_warnings = bool(raw_args.get("allow_benign_warnings", True))
dry_run_gates_only = bool(raw_args.get("dry_run_gates_only", False))
worker_timeout_sec = int(raw_args.get("worker_timeout_sec", 600))
parent_conv_id = str(globals().get("CONVERSATION_ID") or "")


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
PACK = ROOT / "pack-tieudaotu"
PY = sys.executable

AUDIT_MAP_PY = WORKBENCH / ".agents" / "skills" / "khao-sat-ban-do" / "scripts" / "audit_map_scenario.py"
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
    combined = (proc.stdout + "\n" + proc.stderr).strip()
    return proc.returncode, combined


def set_tdd_phase(phase_name: str | None, role_name: str | None = None) -> None:
    TDD_LOCK_DIR.mkdir(parents=True, exist_ok=True)
    if phase_name is None:
        if ACTIVE_PHASE_FILE.exists():
            ACTIVE_PHASE_FILE.unlink()
    else:
        payload = {"phase": phase_name}
        if role_name:
            payload["role"] = role_name
        if parent_conv_id:
            payload["excluded_conversation_ids"] = [parent_conv_id]
        ACTIVE_PHASE_FILE.write_text(json.dumps(payload), encoding="utf-8")


def find_map_scenario(mid: int) -> Path | None:
    maps_dir = WORKBENCH / "qa" / "JXReborn.WireParityProxy" / "Scenarios" / "Maps"
    matches = sorted(maps_dir.glob(f"{mid:03d}_*.scenario.json"))
    return matches[0] if matches else None


def load_map_inventory_lines(mid: int) -> list[tuple[int, int, str]]:
    _, inv_text = run_cmd([PY, str(AUDIT_MAP_PY), "inventory", "--map", str(mid)])
    pts: list[tuple[int, int, str]] = []
    for ln in inv_text.splitlines():
        m = re.search(r"\((\d+)\s*,\s*(\d+)\)", ln)
        if m:
            pts.append((int(m.group(1)), int(m.group(2)), ln.strip()))
    return pts


def analyze_run_findings(run_dir: Path, mid: int = 0) -> dict:
    report_path = run_dir / "scenario-report.json"
    if not report_path.is_file():
        return {"valid": False, "reason": f"Missing {report_path}"}

    rep = json.loads(report_path.read_text(encoding="utf-8-sig"))
    step_failures = int(rep.get("StepFailures", -1))
    proto_violations = int(rep.get("ProtocolViolations", -1))
    crit_findings = int(rep.get("CriticalFindings", -1))
    warn_findings = int(rep.get("WarningFindings", 0))
    passed_steps = int(rep.get("PassedSteps", 0))
    total_steps = int(rep.get("TotalSteps", 0))

    clusters: dict[str, dict] = {}
    findings_files = sorted(run_dir.rglob("findings.jsonl"))
    for ff in findings_files:
        for line in ff.read_text(encoding="utf-8-sig").splitlines():
            if not line.strip():
                continue
            f = json.loads(line)
            sev = str(f.get("severity") if "severity" in f else f.get("Severity", ""))
            field_path = str(f.get("fieldPath") if "fieldPath" in f else f.get("FieldPath", ""))
            cat = str(f.get("category") if "category" in f else f.get("Category", ""))
            # Actionable = Critical findings, or non-benign Dialog/Shop/EntityTranslation semantic mismatches
            is_critical = sev in ("Critical", "2")
            is_benign_variance = field_path.endswith(".RandomPool") or field_path.endswith(".AmbientCombatVariance")
            is_semantic_logic = (not is_benign_variance) and any(
                field_path.startswith(pref)
                for pref in ("Dialog.", "C2S.EntityTranslation", "Shop.", "Player.MapId", "Player.Level")
            )
            if not (is_critical or is_semantic_logic):
                continue

            ckey = str(f.get("clusterKey") or f.get("ClusterKey") or f"{cat}::{field_path}")
            if ckey not in clusters:
                clusters[ckey] = {
                    "cluster_key": ckey,
                    "severity": sev,
                    "field_path": field_path,
                    "count": 0,
                    "sample_step_index": f.get("stepIndex", f.get("StepIndex")),
                    "sample_step_name": f.get("stepName", f.get("StepName")),
                    "legacy_value": f.get("legacyValue", f.get("LegacyValue")),
                    "reborn_value": f.get("rebornValue", f.get("RebornValue")),
                    "repro": f.get("repro", f.get("Repro")),
                }
            clusters[ckey]["count"] += 1

    failed_steps = [
        s for s in rep.get("StepResults", []) if str(s.get("Status", "")).upper() != "OK"
    ]

    sorted_clusters = sorted(clusters.values(), key=lambda c: (-c["count"], c["cluster_key"]))
    if sorted_clusters and mid > 0:
        inv_pts = load_map_inventory_lines(mid)
        for cl in sorted_clusters[:8]:
            repro = cl.get("repro") or {}
            ppos = repro.get("playerPos") if isinstance(repro, dict) else None
            if isinstance(ppos, list) and len(ppos) == 2 and inv_pts:
                tx, ty = int(ppos[0]) // 32, int(ppos[1]) // 32
                ranked = sorted(inv_pts, key=lambda it: (it[0] - tx) ** 2 + (it[1] - ty) ** 2)[:5]
                cl["player_tile"] = [tx, ty]
                cl["nearest_ground_truth_entities"] = [r[2] for r in ranked]
    return {
        "valid": True,
        "report_path": str(report_path),
        "total_steps": total_steps,
        "passed_steps": passed_steps,
        "step_failures": step_failures,
        "protocol_violations": proto_violations,
        "critical_findings": crit_findings,
        "warning_findings": warn_findings,
        "failed_steps": failed_steps[:5],
        "actionable_clusters": sorted_clusters[:8],
    }


# ============================================================================
# Phase 1: Ground-Truth Survey & Static Set-Cover Audit Gate
# ============================================================================
phase(f"Phase 1: Map {map_id:03d} Static Set-Cover Gate")
scenario_path = find_map_scenario(map_id)

if scenario_path is None:
    log(f"No existing scenario for Map {map_id:03d}; invoking Surveyor Agent...")
    _, inv_out = run_cmd([PY, str(AUDIT_MAP_PY), "inventory", "--map", str(map_id)])
    _, cov_out = run_cmd([PY, str(AUDIT_MAP_PY), "plan-cover", "--map", str(map_id), "--radius", "26"])

    survey_res = await agent(
        prompt=(
            f"Read and strictly follow `d:/Projects/jx-reborn/workbench/.agents/skills/khao-sat-ban-do/SKILL.md`.\n"
            f"Author the full Ground-Truth walkthrough Markdown and `.scenario.json` for Map {map_id} under "
            f"`d:/Projects/jx-reborn/workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/`.\n"
            f"Run `audit_map_scenario.py audit --strict` and register the map in `WireParityScenarioAndPathfinderTests.cs`.\n\n"
            f"Pre-computed Inventory:\n{inv_out[:12000]}\n\nPre-computed Set-Cover:\n{cov_out[:6000]}"
        ),
        schema={
            "type": "object",
            "properties": {
                "scenario_path": {"type": "string"},
                "walkthrough_path": {"type": "string"},
            },
            "required": ["scenario_path", "walkthrough_path"],
        },
        role=f"Map {map_id:03d} Surveyor",
    )
    scenario_path = Path(survey_res["scenario_path"]).resolve()

code, audit_out = run_cmd([PY, str(AUDIT_MAP_PY), "audit", "--scenario", str(scenario_path), "--strict"])
if code != 0:
    log(f"Static scenario audit failed; invoking Scenario Repair Agent:\n{audit_out}")
    await agent(
        prompt=(
            f"Fix scenario `{scenario_path}` so `audit_map_scenario.py audit --strict` passes with 100% Ground-Truth coverage.\n"
            f"Audit failure output:\n{audit_out}"
        ),
        schema={"type": "string"},
        role=f"Map {map_id:03d} Scenario Auditor Fix",
    )
    code, audit_out = run_cmd([PY, str(AUDIT_MAP_PY), "audit", "--scenario", str(scenario_path), "--strict"])
    if code != 0:
        raise RuntimeError(f"Gate 1 FAILED: audit_map_scenario.py --strict still failing:\n{audit_out}")

log(f"[Gate 1 PASS] {scenario_path.name} passed strict 100% entity & branch coverage audit.")


# ============================================================================
# Phase 2..4: Live T=0 Dual-Run & Mechanical RED -> GREEN Convergence Loop
# ============================================================================
latest_analysis = None
runs_root = WORKBENCH / "artifacts" / "scenario-runs" / f"{map_id:03d}"

if reuse_existing_clean_run and runs_root.is_dir():
    for cand in sorted(runs_root.rglob("scenario-report.json"), key=lambda p: p.stat().st_mtime, reverse=True):
        cand_analysis = analyze_run_findings(cand.parent, map_id)
        if (
            cand_analysis["valid"]
            and cand_analysis["total_steps"] > 0
            and cand_analysis["step_failures"] == 0
            and cand_analysis["protocol_violations"] == 0
            and cand_analysis["critical_findings"] == 0
            and not cand_analysis["actionable_clusters"]
        ):
            latest_analysis = cand_analysis
            log(f"Found verified zero-critical run report: {cand}")
            break

if not dry_run_gates_only and latest_analysis is None:
    for iteration in range(1, max_fix_iterations + 1):
        phase(f"Phase 2 (Iter {iteration}): Live T=0 Dual-Run Map {map_id:03d}")
        iter_dir = runs_root / f"wf-iter-{iteration}"
        if iter_dir.exists():
            shutil.rmtree(iter_dir, ignore_errors=True)
        iter_dir.mkdir(parents=True, exist_ok=True)

        # Clean ONLY sandbox container `legacy-oracle-test` + volume `legacy-oracle-test-v1` for deterministic T=0 state
        run_cmd(["wslc", "remove", "-f", "legacy-oracle-test"])
        run_cmd(["wslc", "volume", "remove", "legacy-oracle-test-v1"])

        # Ensure pack-tieudaotu/dist/server/ and JXReborn.Host.dll reflect the latest pack & server assemblies before launching standalone host
        pc_proj = SERVER / "tools" / "JXReborn.PackCompiler" / "JXReborn.PackCompiler.csproj"
        pc_code, pc_out = run_cmd(["dotnet", "run", "--project", str(pc_proj), "--", "--pack", str(PACK)])
        if pc_code != 0:
            raise RuntimeError(f"PackCompiler rebuild failed before Phase 2:\n{pc_out}")

        host_proj = SERVER / "src" / "JXReborn.Host" / "JXReborn.Host.csproj"
        hb_code, hb_out = run_cmd(["dotnet", "build", str(host_proj), "-c", "Debug", "--nologo", "-v", "q"])
        if hb_code != 0:
            hb_code, hb_out = run_cmd(["dotnet", "build", str(host_proj), "-c", "Debug-Standalone", "--nologo", "-v", "q"])
            if hb_code != 0:
                raise RuntimeError(f"Failed to build JXReborn.Host before Phase 2:\n{hb_out}")

        proxy_cmd = [
            "dotnet",
            "run",
            "--project",
            str(WORKBENCH / "qa" / "JXReborn.WireParityProxy" / "JXReborn.WireParityProxy.csproj"),
            "--",
            "--standalone",
            "--headless",
            "--no-browser",
            "--report-dir",
            str(iter_dir),
            "--scenario",
            str(scenario_path),
        ]
        p_code, p_out = run_cmd(proxy_cmd, cwd=WORKBENCH)
        run_cmd(["wslc", "remove", "-f", "legacy-oracle-test"])
        run_cmd(["wslc", "volume", "remove", "legacy-oracle-test-v1"])

        latest_analysis = analyze_run_findings(iter_dir, map_id)
        if not latest_analysis.get("valid"):
            raise RuntimeError(
                f"WireParityProxy failed to produce scenario-report.json (exit={p_code}):\n{p_out[-4000:]}"
            )
        log(
            f"Iter {iteration} Result: steps={latest_analysis.get('passed_steps')}/{latest_analysis.get('total_steps')}, "
            f"stepFailures={latest_analysis.get('step_failures')}, protoViolations={latest_analysis.get('protocol_violations')}, "
            f"crit={latest_analysis.get('critical_findings')}, actionableClusters={len(latest_analysis.get('actionable_clusters', []))}"
        )

        if (
            latest_analysis["valid"]
            and latest_analysis["step_failures"] == 0
            and latest_analysis["protocol_violations"] == 0
            and latest_analysis["critical_findings"] == 0
            and not latest_analysis["actionable_clusters"]
        ):
            log(f"[Gate 4 PASS] Map {map_id:03d} converged to 0 Critical / 0 Actionable Parity Findings!")
            break

        if iteration == max_fix_iterations:
            raise RuntimeError(
                f"Map {map_id:03d} did not converge within {max_fix_iterations} iterations. "
                f"Remaining clusters: {json.dumps(latest_analysis['actionable_clusters'], ensure_ascii=False)}"
            )

        # Phase 3: Mechanical RED Test Authoring
        phase(f"Phase 3 (Iter {iteration}): Mechanical RED Tests")
        snap_path = TDD_LOCK_DIR / f"map_{map_id:03d}_iter{iteration}_snap.json"
        red_lock_path = TDD_LOCK_DIR / f"map_{map_id:03d}_iter{iteration}_red.json"
        s_code, s_out = run_cmd([PY, str(TDD_GATE_PY), "snapshot", "--out", str(snap_path)])
        if s_code != 0:
            raise RuntimeError(f"TDD snapshot failed:\n{s_out}")

        vault_dir = Path(tempfile.gettempdir()) / f"jxr-holdout-vault-map{map_id:03d}-it{iteration}-{uuid.uuid4().hex}"
        try:
            set_tdd_phase("red", "blind_qa")
            red_plan = await asyncio.wait_for(
                agent(
                    prompt=(
                        f"You are the BLIND QA Test Author (`role = blind_qa`) for Map {map_id:03d} parity convergence.\n"
                        f"- Do NOT read production implementation bodies (`src/`). Read only `legacy-server/bin/`, `legacy-server/discovery/`, and Public Contracts/Interfaces.\n"
                        f"- Stay surgical: inspect ONLY the specific Legacy Lua files and public contracts implicated by `nearest_ground_truth_entities` below (keep total tool turns <= 8 and call finish promptly).\n"
                        f"- Write new standalone Ground-Truth failing unit/simulation/pack test file(s) ONLY inside `tests/`.\n"
                        f"- Produce a detailed `clean_room_spec_for_coder` (containing zero `0x08...` addresses or `.cpp` names) so a Blind Coder can implement the fix without seeing `legacy-server/` or your tests.\n\n"
                        f"Clusters & Failures:\n{json.dumps(latest_analysis, indent=2, ensure_ascii=False)}"
                    ),
                    schema={
                        "type": "object",
                        "properties": {
                            "root_cause_summary": {"type": "string"},
                            "clean_room_spec_for_coder": {"type": "string"},
                            "test_project": {"type": "string"},
                            "test_filter": {"type": "string"},
                            "legacy_lua_files": {"type": "array", "items": {"type": "string"}},
                            "target_prod_files": {"type": "array", "items": {"type": "string"}},
                        },
                        "required": [
                            "root_cause_summary",
                            "clean_room_spec_for_coder",
                            "test_project",
                            "test_filter",
                            "legacy_lua_files",
                            "target_prod_files",
                        ],
                    },
                    role=f"Map {map_id:03d} Blind QA (Iter {iteration})",
                    type_name="jx-blind-qa",
                ),
                timeout=worker_timeout_sec,
            )

            r_code, r_out = run_cmd([
                PY,
                str(TDD_GATE_PY),
                "check-red",
                "--snapshot",
                str(snap_path),
                "--project",
                red_plan["test_project"],
                "--filter",
                red_plan["test_filter"],
                "--save-red-lock",
                str(red_lock_path),
            ])
            if r_code != 0:
                raise RuntimeError(f"Mechanical RED Gate rejected worker output:\n{r_out}")
            log(f"[Mechanical RED Verified] {red_plan['test_filter']}")

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

            # Phase 4: Double-Blind Clean-Room Port/Fix & Holdout Vault Evaluation
            phase(f"Phase 4 (Iter {iteration}): Double-Blind Coder & Holdout Vault Gate")
            set_tdd_phase("green", "blind_coder")
            fix_summary = await asyncio.wait_for(
                agent(
                    prompt=(
                        f"You are the BLIND Clean-Room Coder (`role = blind_coder`) for Map {map_id:03d}.\n"
                        f"- You have ZERO read/write access to `tests/`, `legacy-server/`, `*.capture.json`, or `WireParityProxy/Diffing/`.\n"
                        f"- Implement the complete Clean-Room solution strictly from the specification below.\n"
                        f"- If you edit `pack-tieudaotu/data/`, recompile `server.pack` via `JXReborn.PackCompiler`.\n\n"
                        f"Clean-Room Specification:\n{red_plan['clean_room_spec_for_coder']}\n"
                        f"Target Production Files: {json.dumps(red_plan['target_prod_files'])}"
                    ),
                    schema={"type": "string"},
                    role=f"Map {map_id:03d} Blind Coder (Iter {iteration})",
                    type_name="jx-blind-coder",
                ),
                timeout=worker_timeout_sec,
            )
            log(f"Blind Coder completed: {fix_summary}")

            set_tdd_phase(None)
            if red_plan["legacy_lua_files"] and red_plan["target_prod_files"]:
                af_cmd = [PY, str(AUDIT_FEAT_PY), "audit", "--strict"]
                for lf in red_plan["legacy_lua_files"]:
                    af_cmd.extend(["--lua", lf])
                for tf in red_plan["target_prod_files"]:
                    af_cmd.extend(["--target", tf])
                af_code, af_out = run_cmd(af_cmd)
                if af_code != 0:
                    raise RuntimeError(f"Feature Parity Static Gate FAILED:\n{af_out}")

            g_code, g_out = run_cmd([
                PY,
                str(TDD_GATE_PY),
                "evaluate-holdout",
                "--red-lock",
                str(red_lock_path),
                "--vault-dir",
                str(vault_dir),
                "--project",
                red_plan["test_project"],
                "--filter",
                red_plan["test_filter"],
                "--run-compliance",
            ])
            if g_code != 0:
                raise RuntimeError(f"Double-Blind Holdout GREEN Gate FAILED:\n{g_out}")
            log(f"[Double-Blind Holdout GREEN Verified] {red_plan['test_filter']} passed cleanly.")

            # Always rebuild `pack-tieudaotu/dist/server/` (`server.pack` + `scripts/JXReborn.Packs.TieuDaoTu.dll`)
            # before the next live standalone run so C# script and JSON data fixes take effect immediately.
            pc_proj = SERVER / "tools" / "JXReborn.PackCompiler" / "JXReborn.PackCompiler.csproj"
            pc_code, pc_out = run_cmd(["dotnet", "run", "--project", str(pc_proj), "--", "--pack", str(PACK)])
            if pc_code != 0:
                raise RuntimeError(f"PackCompiler rebuild failed after Phase 4:\n{pc_out}")
            log("[PackCompiler] Rebuilt server.pack and script layer DLL.")
        finally:
            set_tdd_phase(None)


# ============================================================================
# Phase 5: Global Regression Ratchet & Parity Master Ledger Lock
# ============================================================================
phase(f"Phase 5: Ratchet Verification & Ledger Lock (Map {map_id:03d})")

wc_proj = SERVER / "tests" / "JXReborn.Integration.Tests" / "JXReborn.Integration.Tests.csproj"
wc_code, wc_out = run_cmd(["dotnet", "test", str(wc_proj), "--nologo", "--filter", "FullyQualifiedName~WireCaptures"])
if wc_code != 0:
    raise RuntimeError(f"Gate 3 FAILED: Offline L3A+ WireCaptures regression broke:\n{wc_out}")
log("[Gate 3 PASS] Offline L3A+ WireCaptures suite passed 100%.")

if latest_analysis and latest_analysis.get("valid"):
    lock_cmd = [
        PY,
        str(LEDGER_PY),
        "verify-and-lock",
        "--domain",
        "maps_world",
        "--id",
        f"map_{map_id:03d}",
        "--scenario",
        str(scenario_path),
        "--scenario-report",
        latest_analysis["report_path"],
        "--test-project",
        "workbench/qa/JXReborn.WireParityProxy.Tests/JXReborn.WireParityProxy.Tests.csproj",
        "--test-filter",
        "FullyQualifiedName~WireParityScenarioAndPathfinderTests",
        "--run-tests",
    ]
    if allow_benign_warnings:
        lock_cmd.append("--allow-benign-warnings")

    l_code, l_out = run_cmd(lock_cmd)
    if l_code != 0:
        raise RuntimeError(f"Parity Ledger Lock REJECTED:\n{l_out}")
    log(f"[LOCKED] map_{map_id:03d} promoted to zero_drift_locked in parity_master_ledger.json!")
    print(l_out)
else:
    log("Dry-run gates completed (skipping live report lock).")
