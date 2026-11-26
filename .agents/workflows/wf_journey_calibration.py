"""Differential Parity Journey Calibration Workflow (`wf_journey_calibration.py`).

Executes via Antigravity `run_workflow`:
  run_workflow(
      ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_journey_calibration.py",
      Description="Calibrate and lock Differential Parity Journey J09",
      Args={"journey_id": "J09", "test_class": "DifferentialParityNpcShopTests", "max_attempts": 3}
  )

Automates Live Oracle calibration (`run-parity.ps1 -Mode Live`), mechanical `RED -> GREEN` root-cause
remediation on any `StateDiffEngine` divergence, golden baseline recording (`-Mode Record`), and
zero-Docker offline replay verification (`-Mode Replay`) before locking in `parity_master_ledger.json`.
"""

import json
import subprocess
import sys
from pathlib import Path

raw_args = globals().get("args") or {}
journey_id = str(raw_args.get("journey_id", "J09")).upper()
test_class = str(raw_args.get("test_class", ""))
max_attempts = int(raw_args.get("max_attempts", 3))

if not test_class:
    raise ValueError("Args['test_class'] is required (e.g. 'DifferentialParityNpcShopTests').")


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

RUN_PARITY_PS1 = WORKBENCH / "qa" / "scripts" / "run-parity.ps1"
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


def set_tdd_phase(phase_name: str | None) -> None:
    TDD_LOCK_DIR.mkdir(parents=True, exist_ok=True)
    if phase_name is None:
        if ACTIVE_PHASE_FILE.exists():
            ACTIVE_PHASE_FILE.unlink()
    else:
        ACTIVE_PHASE_FILE.write_text(json.dumps({"phase": phase_name}), encoding="utf-8")


for attempt in range(1, max_attempts + 1):
    phase(f"Phase 1 (Attempt {attempt}): Live Dual-Run {journey_id} ({test_class})")
    live_code, live_out = run_cmd(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(RUN_PARITY_PS1),
            "-Profile",
            "v1",
            "-Mode",
            "Live",
            "-Filter",
            test_class,
            "-StopAfter",
        ],
        cwd=WORKBENCH,
    )
    if live_code == 0:
        log(f"[Live Parity PASS] {journey_id} ({test_class}) passed clean T=0 dual-run!")
        break

    if attempt == max_attempts:
        raise RuntimeError(f"Journey {journey_id} still failing after {max_attempts} attempts:\n{live_out[-6000:]}")

    phase(f"Phase 2 (Attempt {attempt}): Mechanical RED -> GREEN Fix for {journey_id}")
    snap_path = TDD_LOCK_DIR / f"journey_{journey_id}_att{attempt}_snap.json"
    red_lock_path = TDD_LOCK_DIR / f"journey_{journey_id}_att{attempt}_red.json"
    run_cmd([PY, str(TDD_GATE_PY), "snapshot", "--out", str(snap_path)])

    try:
        set_tdd_phase("red")
        red_plan = await agent(
            prompt=(
                f"Diagnose the Live Differential Parity failure in `{journey_id}` (`{test_class}`).\n"
                f"1. Verify Ground Truth in `legacy-server/discovery/` and `legacy-server/bin/`.\n"
                f"2. Write an isolated fast `L1`/`L2` failing unit/simulation test under `tests/` ONLY reproducing the exact bug.\n\n"
                f"Live Dual-Run Output Tail:\n{live_out[-10000:]}"
            ),
            schema={
                "type": "object",
                "properties": {
                    "diagnosis": {"type": "string"},
                    "test_project": {"type": "string"},
                    "test_filter": {"type": "string"},
                },
                "required": ["diagnosis", "test_project", "test_filter"],
            },
            role=f"{journey_id} RED Test Author (Attempt {attempt})",
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
            raise RuntimeError(f"RED Gate failed:\n{r_out}")

        set_tdd_phase("green")
        await agent(
            prompt=(
                f"Implement the Clean-Room production fix for `{journey_id}` so `{red_plan['test_filter']}` passes.\n"
                f"Do NOT modify `tests/` or `WireParityProxy/Diffing/`.\n"
                f"Diagnosis:\n{json.dumps(red_plan, indent=2, ensure_ascii=False)}"
            ),
            schema={"type": "string"},
            role=f"{journey_id} GREEN Fixer (Attempt {attempt})",
        )

        g_code, g_out = run_cmd([
            PY,
            str(TDD_GATE_PY),
            "check-green",
            "--red-lock",
            str(red_lock_path),
            "--project",
            red_plan["test_project"],
            "--filter",
            red_plan["test_filter"],
            "--run-compliance",
        ])
        if g_code != 0:
            raise RuntimeError(f"GREEN Gate failed:\n{g_out}")
    finally:
        set_tdd_phase(None)

phase(f"Phase 3: Record Golden Baseline & Verify Zero-Docker Replay ({journey_id})")
rec_code, rec_out = run_cmd(
    [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(RUN_PARITY_PS1),
        "-Profile",
        "v1",
        "-Mode",
        "Record",
        "-Filter",
        test_class,
        "-StopAfter",
    ],
    cwd=WORKBENCH,
)
if rec_code != 0:
    raise RuntimeError(f"Record baseline failed for {journey_id}:\n{rec_out[-4000:]}")

rep_code, rep_out = run_cmd(
    [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(RUN_PARITY_PS1),
        "-Profile",
        "v1",
        "-Mode",
        "Replay",
        "-Filter",
        test_class,
    ],
    cwd=WORKBENCH,
)
if rep_code != 0:
    raise RuntimeError(f"Offline Replay verification failed for {journey_id}:\n{rep_out[-4000:]}")

log(f"[Calibrated & Locked] {journey_id} ({test_class}) passed Live, Record, and Offline Replay!")
print(rep_out[-1500:])
